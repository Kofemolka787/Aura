using System;
using System.Collections.Generic;

namespace ColorForge.Models;

public sealed record BenchmarkContext
{
    public string Scene { get; init; } = string.Empty;
    public string GraphicsSettings { get; init; } = string.Empty;
    public string GameVersion { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;
    public string WindowsVersion { get; init; } = string.Empty;
    public string CpuName { get; init; } = string.Empty;
    public string GpuName { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public int RefreshRate { get; init; }
    public int RequestedDurationSeconds { get; init; }
    public bool IsComplete => !string.IsNullOrWhiteSpace(Scene) && !string.IsNullOrWhiteSpace(GraphicsSettings)
        && !string.IsNullOrWhiteSpace(GameVersion) && !string.IsNullOrWhiteSpace(DriverVersion)
        && !string.IsNullOrWhiteSpace(WindowsVersion) && !string.IsNullOrWhiteSpace(CpuName) && !string.IsNullOrWhiteSpace(GpuName)
        && CpuName != "Процессор не определён" && GpuName != "Видеокарта не определена"
        && Width > 0 && Height > 0 && RefreshRate > 0 && RequestedDurationSeconds is >= 10 and <= 180;
}
public sealed record BenchmarkRunInfo
{
    public string SeriesId { get; init; } = Guid.NewGuid().ToString("N");
    public string Variant { get; init; } = string.Empty;
    public int RunNumber { get; init; }
    public int ExpectedRuns { get; init; } = 3;
    public BenchmarkContext Context { get; init; } = new();
}
public sealed record TelemetrySample(DateTime TimestampUtc, double? CpuTemperatureC, double? CpuLoadPercent,
    double? CpuClockMhz, double? GpuTemperatureC, double? GpuLoadPercent, double? GpuCoreClockMhz, double? GpuMemoryUsedMb)
{
    public static TelemetrySample FromSnapshot(HardwareSnapshot value)
    {
        bool valid = value.TelemetryValid && DateTime.UtcNow - value.TimestampUtc <= TimeSpan.FromSeconds(3)
            && value.TimestampUtc <= DateTime.UtcNow.AddSeconds(1);
        double? Number(double? n, double max = double.MaxValue) => valid && n.HasValue && double.IsFinite(n.Value)
            && n >= 0 && n <= max ? n : null;
        return new(value.TimestampUtc, Number(value.CpuTemperatureC, 130), Number(value.CpuLoadPercent, 100), Number(value.CpuClockMhz),
            Number(value.GpuTemperatureC, 130), Number(value.GpuLoadPercent, 100), Number(value.GpuCoreClockMhz), Number(value.GpuMemoryUsedMb));
    }
}
public sealed class BenchmarkSeries
{
    public string Id { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string Variant { get; init; } = string.Empty;
    public IReadOnlyList<BenchmarkResult> Runs { get; init; } = Array.Empty<BenchmarkResult>();
    public int ExpectedRuns { get; init; }
    public bool IsComplete { get; init; }
    public bool IsComparable { get; init; }
    public double MeanFps { get; init; }
    public double MeanLowFps { get; init; }
    public double MinFps { get; init; }
    public double MaxFps { get; init; }
    public string Label => $"{Variant} · {ProcessName} · {Runs.Count}/{ExpectedRuns} · {MeanFps:0.0} FPS"
        + (IsComplete && IsComparable ? "" : " · неполная / несопоставимая");
}
