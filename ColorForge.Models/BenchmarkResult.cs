using System;

namespace ColorForge.Models;

public sealed class BenchmarkResult
{
    public string ProcessName { get; init; } = string.Empty;

    public DateTime RecordedAt { get; init; } = DateTime.Now;

    public int FrameCount { get; init; }

    public double DurationSeconds { get; init; }

    public double AverageFps { get; init; }

    public double OnePercentLowFps { get; init; }

    public double P95FrameTimeMs { get; init; }

    public double P99FrameTimeMs { get; init; }

    public int StutterCount { get; init; }

    public string CsvPath { get; init; } = string.Empty;

    public string Summary => $"{AverageFps:0} FPS · 1% low {OnePercentLowFps:0} · P95 {P95FrameTimeMs:0.0} ms";
}
