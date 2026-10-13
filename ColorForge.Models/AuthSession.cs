using System;

namespace ColorForge.Models;

public sealed class AuthSession
{
	public string AuthMode { get; set; } = "Prototype";

    public string SignedPayload { get; set; } = string.Empty;

    public string Signature { get; set; } = string.Empty;

	public string Email { get; set; } = string.Empty;

	public string DisplayName { get; set; } = "AURA User";

	public string PlanName { get; set; } = "AURA · 30 дней";

	public DateTimeOffset ActivatedAtUtc { get; set; }

	public DateTimeOffset ExpiresAtUtc { get; set; }

	public string DeviceId { get; set; } = string.Empty;

	public string SessionToken { get; set; } = string.Empty;

	public string LicenseSuffix { get; set; } = string.Empty;

	public int DaysRemaining
	{
		get
		{
			double days = (ExpiresAtUtc - DateTimeOffset.UtcNow).TotalDays;
			return days <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(days));
		}
	}
}
