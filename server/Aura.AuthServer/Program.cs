using System.Threading.RateLimiting;
using Aura.AuthServer;
using ColorForge.Models;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
string data = Path.GetFullPath(builder.Configuration["Aura:DataDirectory"] ?? Path.Combine(Environment.CurrentDirectory, "server-data"));
if (args.FirstOrDefault() == "admin")
{
    using var database = new AuthDatabase(data);
    switch (args.ElementAtOrDefault(1)) {
        case "account":
            Console.Write("Email: "); string email = Console.ReadLine() ?? "";
            Console.Write("Password (12–256 characters, hidden): "); string password = ReadSecret();
            await database.CreateAccountAsync(email, password); Console.WriteLine("Account created."); break;
        case "license":
            int days = int.Parse(args.ElementAtOrDefault(2) ?? "30");
            string key = await database.CreateLicenseAsync(days);
            string path = Path.Combine(data, "license-" + Guid.NewGuid().ToString("N") + ".txt");
            AuthDatabase.WritePrivateFile(path, System.Text.Encoding.UTF8.GetBytes(key));
            Console.WriteLine("License saved to protected file: " + path); break;
        case "revoke": Console.Write("License key (hidden): "); await database.RevokeLicenseAsync(ReadSecret()); Console.WriteLine("Revoked."); break;
        case "public-key": Console.WriteLine(database.PublicKey); break;
        default: Console.WriteLine("admin account | license [days] | revoke | public-key"); break;
    }
    return;
}
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5088");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 16384);
builder.Services.AddSingleton(sp => new AuthDatabase(Path.GetFullPath(sp.GetRequiredService<IConfiguration>()["Aura:DataDirectory"] ?? Path.Combine(Environment.CurrentDirectory, "server-data"))));
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetConcurrencyLimiter("global",
        _ => new ConcurrencyLimiterOptions { PermitLimit = 8, QueueLimit = 0 }));
});
var app = builder.Build();
app.Use(async (context, next) => {
    bool local = context.Connection.RemoteIpAddress != null && System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress);
    if (!context.Request.IsHttps && !(app.Environment.IsDevelopment() && local)) { context.Response.StatusCode = 403; return; }
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "ok", protocol = AuthProtocol.Issuer }));
app.MapPost("/v1/login", async (LoginRequest r, AuthDatabase db) => r.Email is null || r.Password is null || r.DeviceId is null ? Results.BadRequest() : Results.Ok(await db.LoginAsync(r))).RequireRateLimiting("auth");
app.MapPost("/v1/activate", async (ActivateRequest r, AuthDatabase db) => r.LoginToken is null || r.LicenseKey is null || r.DeviceId is null ? Results.BadRequest() : Results.Ok(await db.ActivateAsync(r))).RequireRateLimiting("auth");
app.MapPost("/v1/validate", async (ValidateRequest r, AuthDatabase db) => r.SessionToken is null || r.DeviceId is null ? Results.BadRequest() : Results.Ok(await db.ValidateAsync(r))).RequireRateLimiting("auth");
app.MapPost("/v1/logout", async (ValidateRequest r, AuthDatabase db) => r.SessionToken is null || r.DeviceId is null ? Results.BadRequest() : Results.Ok(await db.RevokeSessionAsync(r))).RequireRateLimiting("auth");
app.Run();

static string ReadSecret()
{
    if (Console.IsInputRedirected) throw new InvalidOperationException("Use an interactive terminal for secret entry.");
    var text = new System.Text.StringBuilder();
    for (;;) { ConsoleKeyInfo k = Console.ReadKey(true); if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; } else if (!char.IsControl(k.KeyChar) && text.Length < 256) text.Append(k.KeyChar);
    }
    Console.WriteLine(); return text.ToString();
}
public partial class Program { }
