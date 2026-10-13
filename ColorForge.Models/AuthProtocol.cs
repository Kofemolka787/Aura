using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ColorForge.Models;

public sealed record LoginRequest(string Email, string Password, string DeviceId);
public sealed record ActivateRequest(string LoginToken, string LicenseKey, string DeviceId);
public sealed record ValidateRequest(string SessionToken, string DeviceId);
public sealed record AuthReply(bool Success, string Message, string? LoginToken = null, string? Email = null, AuthSession? Session = null);
public sealed record LicenseClaims(string Issuer, string SessionId, string Email, string DeviceId, string TokenHash,
    DateTimeOffset ActivatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset IssuedAtUtc, DateTimeOffset OfflineUntilUtc, string LicenseSuffix);

public static class AuthProtocol
{
    public const string Issuer = "AURA-AUTH-v1";
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static AuthSession Sign(LicenseClaims claims, string token, ECDsa key)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(claims);
        return new AuthSession {
            PlanName = "AURA · серверная лицензия", Email = claims.Email, DeviceId = claims.DeviceId, SessionToken = token, ActivatedAtUtc = claims.ActivatedAtUtc,
            ExpiresAtUtc = claims.ExpiresAtUtc, LicenseSuffix = claims.LicenseSuffix, AuthMode = "Server",
            SignedPayload = Convert.ToBase64String(payload), Signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256))
        };
    }
    public static LicenseClaims Verify(AuthSession session, string publicKey, string deviceId, DateTimeOffset now)
    {
        if (session.AuthMode != "Server" || session.SignedPayload.Length > 8192 || session.Signature.Length > 512)
            throw new CryptographicException("Неверный формат лицензии.");
        byte[] payload = Convert.FromBase64String(session.SignedPayload);
        using ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out int read);
        if (read != Convert.FromBase64String(publicKey).Length || !key.VerifyData(payload, Convert.FromBase64String(session.Signature), HashAlgorithmName.SHA256))
            throw new CryptographicException("Подпись лицензии недействительна.");
        LicenseClaims c = JsonSerializer.Deserialize<LicenseClaims>(payload) ?? throw new CryptographicException("Лицензия повреждена.");
        if (c.Issuer != Issuer || c.DeviceId != deviceId || c.DeviceId != session.DeviceId || c.Email != session.Email
            || c.TokenHash != Hash(session.SessionToken) || c.ExpiresAtUtc != session.ExpiresAtUtc || c.ActivatedAtUtc != session.ActivatedAtUtc
            || c.LicenseSuffix != session.LicenseSuffix || c.IssuedAtUtc > now.AddMinutes(2) || c.OfflineUntilUtc > c.IssuedAtUtc.AddHours(24)
            || c.OfflineUntilUtc > c.ExpiresAtUtc || c.ExpiresAtUtc <= now || c.OfflineUntilUtc <= now)
            throw new CryptographicException("Лицензия истекла или принадлежит другому устройству. Нужна проверка сервера.");
        return c;
    }
}
