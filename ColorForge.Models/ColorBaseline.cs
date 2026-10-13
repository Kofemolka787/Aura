using System;

namespace ColorForge.Models;

public sealed class ColorBaseline
{
    public bool PreviouslyModified { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string BootId { get; set; } = string.Empty;
    public ushort[]? Gamma { get; set; }
    public double? NvidiaLevel { get; set; }
    public NvidiaColorState? NvidiaState { get; set; }
    public bool Modified { get; set; }
}
