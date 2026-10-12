using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed record LoginResult(bool Success, string Message, string? LoginToken = null, string? Email = null);

public sealed record ActivationResult(bool Success, string Message, AuthSession? Session = null);

public sealed record SessionValidationResult(bool Success, string Message);

public interface IAuthService
{
	Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default);

	Task<ActivationResult> ActivateAsync(string loginToken, string licenseKey, string deviceId, CancellationToken cancellationToken = default);

	Task<SessionValidationResult> ValidateSessionAsync(AuthSession session, CancellationToken cancellationToken = default);
}

public sealed class DemoAuthService : IAuthService
{
	public const string DemoEmail = "demo@aura.app";

	public const string DemoPassword = "aura2026";

	public const string DemoLicenseKey = "AURA-30D-DEMO-2026";

	private const string PasswordHash = "8fe77aa4f74c8066ddccdda080669abb065c75728ef855b711676df36876e7b4";

	private const string LicenseHash = "e817e19699ab5f2f95b7871900577fb57f94f5a847dc1077906f7a7964ff9ef7";

	private readonly ConcurrentDictionary<string, string> _pendingLogins = new(StringComparer.Ordinal);

	public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
	{
		await Task.Delay(520, cancellationToken);
		string normalizedEmail = email.Trim().ToLowerInvariant();
		string suppliedHash = Hash("aura-demo-v1|" + password);
		if (!string.Equals(normalizedEmail, DemoEmail, StringComparison.OrdinalIgnoreCase)
			|| !FixedEquals(suppliedHash, PasswordHash))
		{
			return new LoginResult(false, "Неверный логин или пароль.");
		}

		string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
		_pendingLogins[token] = normalizedEmail;
		return new LoginResult(true, "Аккаунт подтверждён.", token, normalizedEmail);
	}

	public async Task<ActivationResult> ActivateAsync(string loginToken, string licenseKey, string deviceId, CancellationToken cancellationToken = default)
	{
		await Task.Delay(620, cancellationToken);
		if (!_pendingLogins.TryRemove(loginToken, out string? email))
		{
			return new ActivationResult(false, "Сессия входа устарела. Вернись к первому шагу.");
		}

		string normalizedKey = NormalizeKey(licenseKey);
		if (!FixedEquals(Hash("aura-license-v1|" + normalizedKey), LicenseHash))
		{
			return new ActivationResult(false, "Ключ не найден или уже недействителен.");
		}

		DateTimeOffset now = DateTimeOffset.UtcNow;
		AuthSession session = new AuthSession
		{
			Email = email,
			DisplayName = "AURA User",
			PlanName = "AURA · 30 дней",
			ActivatedAtUtc = now,
			ExpiresAtUtc = now.AddDays(30),
			DeviceId = deviceId,
			SessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
			LicenseSuffix = normalizedKey.Length >= 4 ? normalizedKey[^4..] : normalizedKey
		};
		return new ActivationResult(true, "Подписка активирована на этом компьютере.", session);
	}

	public Task<SessionValidationResult> ValidateSessionAsync(AuthSession session, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(session.SessionToken) || session.SessionToken.Length < 32)
		{
			return Task.FromResult(new SessionValidationResult(false, "Сессия повреждена."));
		}
		if (!FixedEquals(session.DeviceId, DeviceIdentityService.GetDeviceId()))
		{
			return Task.FromResult(new SessionValidationResult(false, "Лицензия активирована на другом устройстве."));
		}
		if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
		{
			return Task.FromResult(new SessionValidationResult(false, "Срок подписки закончился."));
		}
		return Task.FromResult(new SessionValidationResult(true, "Подписка активна."));
	}

	private static string NormalizeKey(string value)
	{
		return value.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
	}

	private static string Hash(string value)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
	}

	private static bool FixedEquals(string left, string right)
	{
		byte[] a = Encoding.UTF8.GetBytes(left);
		byte[] b = Encoding.UTF8.GetBytes(right);
		return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
	}
}
