using System;
using System.Collections.Generic;

namespace ColorForge.Models;

public sealed class BenchmarkResult
{
    public string StreamId { get; init; } = string.Empty;
    public List<FrameStreamChoice> Streams { get; init; } = new();
    public double[] FrameTimesMs { get; init; } = Array.Empty<double>();
    public string Metric { get; init; } = string.Empty;
    public int DroppedFrameCount { get; init; }
    public int GeneratedFrameCount { get; init; }
    public int InvalidFrameCount { get; init; }
    public string ProcessName { get; init; } = string.Empty;

    public DateTime RecordedAt { get; init; } = DateTime.UtcNow;

    public int FrameCount { get; init; }

    public double DurationSeconds { get; init; }

    public double AverageFps { get; init; }

    public double OnePercentLowFps { get; init; }

    public double P95FrameTimeMs { get; init; }

    public double P99FrameTimeMs { get; init; }

    public int StutterCount { get; init; }

    public string CsvPath { get; init; } = string.Empty;

    public string Summary => $"{AverageFps:0} FPS · 1% low {OnePercentLowFps:0} · P95 {P95FrameTimeMs:0.0} ms";
    public string HistoryLabel => RecordedAt.ToLocalTime().ToString("dd.MM HH:mm") + " · " + ProcessName + " · " + Summary;
}

public sealed class FrameStreamChoice
{
    public string Id { get; init; } = string.Empty;
    public int FrameCount { get; init; }
    public override string ToString() => Id + " · " + FrameCount + " кадров";
}
