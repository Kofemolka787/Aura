namespace ColorForge.Models;

public enum AutoTuneTarget
{
    GraphicsCard,
    Processor
}

public sealed record AutoTuneProgress(
    int Percent,
    string Stage,
    string Detail,
    HardwareSnapshot Snapshot,
    int CandidateValue = 0);

public sealed class AutoTuneResult
{
    public AutoTuneTarget Target { get; init; }

    public bool Success { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public int GpuCoreOffsetMhz { get; init; }

    public string CpuProfileId { get; init; } = "stock";

    public string CpuBackendId { get; init; } = "none";

    public int CpuPptWatts { get; init; }

    public int CpuTdcAmps { get; init; }

    public int CpuEdcAmps { get; init; }

    public double PeakTemperatureC { get; init; }

    public double ImprovementPercent { get; init; }
}
