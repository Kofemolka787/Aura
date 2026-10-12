namespace ColorForge.Models;

public sealed class OverclockPreferences
{
    public int LearnedGpuCoreOffsetMhz { get; set; }

    public string CpuProfileId { get; set; } = "stock";

    public string CpuBackendId { get; set; } = "none";

    public int CpuPptWatts { get; set; }

    public int CpuTdcAmps { get; set; }

    public int CpuEdcAmps { get; set; }

    public bool ApplyLearnedGpuOnStart { get; set; }

    public bool ApplyCpuProfileOnStart { get; set; }
}
