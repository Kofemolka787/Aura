using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed record AuthClientConfiguration(string Mode, string BaseUrl, string PublicKey)
{
    public static AuthClientConfiguration Load()
    {
        Assembly assembly = typeof(AuthClientConfiguration).Assembly;
        string name = assembly.GetManifestResourceNames().Single(x => x.EndsWith("AuthClient.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<AuthClientConfiguration>(stream) ?? throw new InvalidDataException("Auth configuration missing.");
    }
    public IAuthService CreateService() => Mode == "Prototype" ? new DemoAuthService() : new ServerAuthService(this);
}

public sealed class ServerAuthService : IAuthService, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _publicKey;
    private readonly Func<string> _device;
    public ServerAuthService(AuthClientConfiguration config, HttpMessageHandler? handler = null, Func<string>? device = null)
    {
        if (config.Mode is not ("Server" or "LocalDevelopment")) throw new InvalidDataException("Unknown authentication mode.");
        var uri = new Uri(config.BaseUrl, UriKind.Absolute);
        if (uri.Scheme != "https" && !(config.Mode == "LocalDevelopment" && uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidDataException("Server authentication requires HTTPS. LocalDevelopment HTTP requires loopback.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new InvalidDataException("Invalid server URL.");
        // No public key is downloaded or trusted from the network. It is pinned at build time.
        using (var key = System.Security.Cryptography.ECDsa.Create()) key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(config.PublicKey), out _);
        _publicKey = config.PublicKey;
        _device = device ?? DeviceIdentityService.GetDeviceId;
        _http = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        _http.BaseAddress = uri; _http.Timeout = TimeSpan.FromSeconds(15); _http.MaxResponseContentBufferSize = 65536;
    }
    private async Task<AuthReply> PostAsync<T>(string path, T body, CancellationToken token)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(path, body, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuthReply>(cancellationToken: token) ?? throw new InvalidDataException("Empty auth response.");
    }
    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        AuthReply reply = await PostAsync("v1/login", new LoginRequest(email, password, _device()), cancellationToken);
        if (reply.Session != null) AuthProtocol.Verify(reply.Session, _publicKey, _device(), DateTimeOffset.UtcNow);
        return new(reply.Success, reply.Message, reply.LoginToken, reply.Email, reply.Session);
    }
    public async Task<ActivationResult> ActivateAsync(string loginToken, string licenseKey, string deviceId, CancellationToken cancellationToken = default)
    {
        AuthReply reply = await PostAsync("v1/activate", new ActivateRequest(loginToken, licenseKey, deviceId), cancellationToken);
        if (reply.Success) AuthProtocol.Verify(reply.Session ?? throw new InvalidDataException("Missing signed grant."), _publicKey, deviceId, DateTimeOffset.UtcNow);
        return new(reply.Success, reply.Message, reply.Session);
    }
    public async Task<SessionValidationResult> ValidateSessionAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        try {
            AuthReply reply = await PostAsync("v1/validate", new ValidateRequest(session.SessionToken, _device()), cancellationToken);
            if (!reply.Success || reply.Session == null) return new(false, reply.Message);
            AuthProtocol.Verify(reply.Session, _publicKey, _device(), DateTimeOffset.UtcNow);
            session.SignedPayload = reply.Session.SignedPayload; session.Signature = reply.Session.Signature;
            session.Email = reply.Session.Email; session.ExpiresAtUtc = reply.Session.ExpiresAtUtc; session.ActivatedAtUtc = reply.Session.ActivatedAtUtc;
            session.LicenseSuffix = reply.Session.LicenseSuffix; session.AuthMode = "Server";
            return new(true, reply.Message);
        }
        catch (Exception e) when ((e is HttpRequestException httpError && (httpError.StatusCode == null || (int)httpError.StatusCode >= 500 || httpError.StatusCode == System.Net.HttpStatusCode.RequestTimeout)) || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested)) {
            // Explicit denials, corrupt replies and signature errors never enter the offline path.
            try { AuthProtocol.Verify(session, _publicKey, _device(), DateTimeOffset.UtcNow); return new(true, "Сервер недоступен: подписанный офлайн-доступ, максимум 24 часа."); }
            catch (Exception invalid) when (invalid is System.Security.Cryptography.CryptographicException or FormatException or JsonException) { return new(false, invalid.Message); }
        }
    }
    public async Task RevokeSessionAsync(AuthSession session, CancellationToken cancellationToken = default) =>
        await PostAsync("v1/logout", new ValidateRequest(session.SessionToken, _device()), cancellationToken);
    public void Dispose() => _http.Dispose();
}
