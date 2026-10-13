using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Aura.AuthServer;
using ColorForge.Models;
using ColorForge.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class AuthTests
{
    private const string Device = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherDevice = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Password = "fixture-password-56789";
    private static string DirectoryPath() => Path.Combine(Path.GetTempPath(), "aura-auth-test-" + Guid.NewGuid().ToString("N"));
    [Fact] public async Task WrongKeyDoesNotConsumeLoginAndSuccessfulActivationCannotReplay()
    {
        string path = DirectoryPath();
        try { using var db = new AuthDatabase(path); await db.CreateAccountAsync("user@test.example", Password); string key = await db.CreateLicenseAsync(30);
            var login = await db.LoginAsync(new("user@test.example", Password, Device)); Assert.True(login.Success);
            Assert.False((await db.ActivateAsync(new(login.LoginToken!, "WRONG", Device))).Success);
            var activated = await db.ActivateAsync(new(login.LoginToken!, key, Device)); Assert.True(activated.Success);
            Assert.False((await db.ActivateAsync(new(login.LoginToken!, key, Device))).Success);
            AuthProtocol.Verify(activated.Session!, db.PublicKey, Device, DateTimeOffset.UtcNow);
            var second = await db.LoginAsync(new("user@test.example", Password, OtherDevice));
            Assert.False((await db.ActivateAsync(new(second.LoginToken!, key, OtherDevice))).Success);
        } finally { Directory.Delete(path, true); }
    }
    [Fact] public async Task ConcurrentActivationHasExactlyOneWinner()
    {
        string path = DirectoryPath(); try { using var db = new AuthDatabase(path); await db.CreateAccountAsync("user@test.example", Password);
            string key = await db.CreateLicenseAsync(1); var login = await db.LoginAsync(new("user@test.example", Password, Device));
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => db.ActivateAsync(new(login.LoginToken!, key, Device))));
            Assert.Single(results, x => x.Success);
        } finally { Directory.Delete(path, true); }
    }
    [Fact] public async Task RevokeAndDeviceMismatchDenySessionAndResumeUsesExistingLicense()
    {
        string path = DirectoryPath(); try { using var db = new AuthDatabase(path); await db.CreateAccountAsync("user@test.example", Password); string key = await db.CreateLicenseAsync(30);
            var login = await db.LoginAsync(new("user@test.example", Password, Device)); var session = (await db.ActivateAsync(new(login.LoginToken!, key, Device))).Session!;
            Assert.False((await db.ValidateAsync(new(session.SessionToken, OtherDevice))).Success);
            var resumed = (await db.LoginAsync(new("user@test.example", Password, Device))).Session!;
            Assert.NotNull(resumed);
            Assert.True((await db.RevokeSessionAsync(new(session.SessionToken, Device))).Success);
            Assert.False((await db.ValidateAsync(new(session.SessionToken, Device))).Success);
            Assert.True((await db.ValidateAsync(new(resumed.SessionToken, Device))).Success);
            await db.RevokeLicenseAsync(key); Assert.False((await db.ValidateAsync(new(resumed.SessionToken, Device))).Success);
        } finally { Directory.Delete(path, true); }
    }
    [Fact] public async Task SignedGrantRejectsTamperWrongDeviceWrongKeyAndExpiredOfflineWindow()
    {
        string path = DirectoryPath(); try { using var db = new AuthDatabase(path); await db.CreateAccountAsync("user@test.example", Password); string key = await db.CreateLicenseAsync(30);
            var login = await db.LoginAsync(new("user@test.example", Password, Device)); var session = (await db.ActivateAsync(new(login.LoginToken!, key, Device))).Session!;
            Assert.Throws<CryptographicException>(() => AuthProtocol.Verify(session, db.PublicKey, OtherDevice, DateTimeOffset.UtcNow));
            Assert.Throws<CryptographicException>(() => AuthProtocol.Verify(session, db.PublicKey, Device, DateTimeOffset.UtcNow.AddDays(2)));
            using var alien = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Assert.Throws<CryptographicException>(() => AuthProtocol.Verify(session, Convert.ToBase64String(alien.ExportSubjectPublicKeyInfo()), Device, DateTimeOffset.UtcNow));
            session.ExpiresAtUtc = session.ExpiresAtUtc.AddYears(1);
            Assert.Throws<CryptographicException>(() => AuthProtocol.Verify(session, db.PublicKey, Device, DateTimeOffset.UtcNow));
        } finally { Directory.Delete(path, true); }
    }
    [Fact] public async Task PasswordAndPendingLoginExpireAndPersistenceSurvivesRestart()
    {
        string path = DirectoryPath(); var clock = new FakeClock(); string key, token, publicKey;
        try {
            using (var db = new AuthDatabase(path, clock)) {
                await db.CreateAccountAsync("user@test.example", Password); key = await db.CreateLicenseAsync(1); publicKey = db.PublicKey;
                Assert.False((await db.LoginAsync(new("user@test.example", "wrong", Device))).Success);
                token = (await db.LoginAsync(new("user@test.example", Password, Device))).LoginToken!;
                clock.Now = clock.Now.AddMinutes(11); Assert.False((await db.ActivateAsync(new(token, key, Device))).Success);
                token = (await db.LoginAsync(new("user@test.example", Password, Device))).LoginToken!;
                token = (await db.ActivateAsync(new(token, key, Device))).Session!.SessionToken;
            }
            using var reopened = new AuthDatabase(path, clock); Assert.Equal(publicKey, reopened.PublicKey);
            Assert.True((await reopened.ValidateAsync(new(token, Device))).Success);
            clock.Now = clock.Now.AddDays(2); Assert.False((await reopened.ValidateAsync(new(token, Device))).Success);
        } finally { Directory.Delete(path, true); }
    }
    [Fact] public void HttpAndUnpinnedServerConfigurationIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => new ServerAuthService(new("Server", "http://example.com/", "")));
        Assert.Throws<InvalidDataException>(() => new ServerAuthService(new("LocalDevelopment", "http://example.com/", "")));
    }
    [Fact] public async Task ActualHttpApiAndMatchingClientActivateValidateAndHonorRevocation()
    {
        string path = DirectoryPath(); try {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => {
                b.UseEnvironment("Development"); b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Aura:DataDirectory"] = path }));
            });
            using var http = factory.CreateClient();
            // TestServer remote address is null; use HTTPS to test production request contract.
            http.BaseAddress = new Uri("https://localhost/");
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("health")).StatusCode);
            var db = factory.Services.GetRequiredService<AuthDatabase>(); Assert.True(File.Exists(Path.Combine(path, "auth.db"))); await db.CreateAccountAsync("user@test.example", Password); string key = await db.CreateLicenseAsync(30);
            using var client = new ServerAuthService(new("Server", "https://localhost/", db.PublicKey), factory.Server.CreateHandler(), () => Device);
            var login = await client.LoginAsync("user@test.example", Password); Assert.True(login.Success);
            var activation = await client.ActivateAsync(login.LoginToken!, key, Device); Assert.True(activation.Success);
            Assert.True((await client.ValidateSessionAsync(activation.Session!)).Success);
            await db.RevokeLicenseAsync(key); Assert.False((await client.ValidateSessionAsync(activation.Session!)).Success);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("v1/login", new { email = (string?)null, password = Password, deviceId = Device })).StatusCode);
        } finally { await DeleteHttpFixtureAsync(path); }
    }
    [Fact] public async Task NetworkFailureAllowsOnlyValidSignedOfflineGrantAndHttpDenialCannotFallback()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
        var now = DateTimeOffset.UtcNow;
        var session = AuthProtocol.Sign(new(AuthProtocol.Issuer, "test", "user@test.example", Device,
            AuthProtocol.Hash("token"), now, now.AddDays(30), now, now.AddHours(24), "1234"), "token", signer);
        using var offline = new ServerAuthService(new("Server", "https://localhost/", publicKey), new FailingHandler(), () => Device);
        Assert.True((await offline.ValidateSessionAsync(session)).Success);
        session.ExpiresAtUtc = session.ExpiresAtUtc.AddDays(1);
        Assert.False((await offline.ValidateSessionAsync(session)).Success);
        using var denied = new ServerAuthService(new("Server", "https://localhost/", publicKey), new FailingHandler(HttpStatusCode.Unauthorized), () => Device);
        await Assert.ThrowsAsync<HttpRequestException>(() => denied.ValidateSessionAsync(session));
    }
    private static async Task DeleteHttpFixtureAsync(string path)
    {
        // TestServer shutdown and Windows file filters can release handles shortly
        // after host disposal. Bound cleanup retries; persistent locks still fail.
        for (int attempt = 0; attempt < 20; attempt++) {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); return; }
            catch (IOException) when (attempt < 19) { await Task.Delay(100); }
        }
    }
    private sealed class FailingHandler(HttpStatusCode? status = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => status == null
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture network unavailable"))
            : Task.FromResult(new HttpResponseMessage(status.Value));
    }
    private sealed class FakeClock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}
