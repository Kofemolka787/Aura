using System;

namespace ColorForge.Models;

public sealed class HardwareChangeSnapshot
{
    public string Kind { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string BootId { get; set; } = string.Empty;
    public ulong OriginalValue { get; set; }
    public ulong TargetValue { get; set; }
    public bool Pending { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
