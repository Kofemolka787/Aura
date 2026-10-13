using System;
using System.Collections.Generic;
using System.Linq;
using ColorForge.Models;

namespace ColorForge.Services;

public static class BenchmarkSeriesService
{
    public static IReadOnlyList<BenchmarkSeries> Group(IEnumerable<BenchmarkResult> history) => history
        .Where(x => x.Run?.Context != null && !string.IsNullOrWhiteSpace(x.Run.SeriesId)
            && !string.IsNullOrWhiteSpace(x.ProcessName) && x.Metric != null)
        .GroupBy(x => x.Run!.SeriesId, StringComparer.Ordinal)
        .Select(Build).OrderByDescending(x => x.Runs.Max(r => r.RecordedAt)).ToArray();

    private static BenchmarkSeries Build(IEnumerable<BenchmarkResult> values)
    {
        BenchmarkResult[] runs = values.OrderBy(x => x.Run!.RunNumber).ToArray();
        BenchmarkResult first = runs[0];
        BenchmarkRunInfo run = first.Run!;
        bool comparable = run.Context.IsComplete && runs.All(x => x.Run!.Context == run.Context
            && x.Run.Variant == run.Variant && x.Run.ExpectedRuns == run.ExpectedRuns
            && x.ProcessName.Equals(first.ProcessName, StringComparison.OrdinalIgnoreCase) && x.Metric == first.Metric
            && double.IsFinite(x.DurationSeconds) && x.DurationSeconds >= run.Context.RequestedDurationSeconds * 0.9
            && x.DurationSeconds <= run.Context.RequestedDurationSeconds * 1.2
            && double.IsFinite(x.AverageFps) && x.AverageFps > 0 && double.IsFinite(x.OnePercentLowFps) && x.OnePercentLowFps > 0);
        bool complete = run.ExpectedRuns is 3 or 5 && runs.Length == run.ExpectedRuns
            && runs.Select(x => x.Run!.RunNumber).SequenceEqual(Enumerable.Range(1, run.ExpectedRuns));
        return new() { Id = run.SeriesId, ProcessName = first.ProcessName, Variant = run.Variant, Runs = runs,
            ExpectedRuns = run.ExpectedRuns, IsComplete = complete, IsComparable = comparable,
            MeanFps = runs.Average(x => x.AverageFps), MeanLowFps = runs.Average(x => x.OnePercentLowFps),
            MinFps = runs.Min(x => x.AverageFps), MaxFps = runs.Max(x => x.AverageFps) };
    }

    public static string Compare(BenchmarkSeries before, BenchmarkSeries after)
    {
        if (before.Id == after.Id) return "Выбрана одна и та же серия. Нужны отдельные серии до и после изменений.";
        if (!before.IsComplete || !after.IsComplete || !before.IsComparable || !after.IsComparable)
            return "Нужны две полные серии по 3 или 5 прогонов с одинаковыми условиями внутри каждой серии. Заполни сцену, настройки и версии; фактическая запись кадров должна длиться 90–120% выбранного времени.";
        BenchmarkResult a = before.Runs[0], b = after.Runs[0];
        if (!a.ProcessName.Equals(b.ProcessName, StringComparison.OrdinalIgnoreCase) || a.Metric != b.Metric
            || a.Run!.Context != b.Run!.Context)
            return "Условия различаются: проверь игру, сцену, настройки графики, разрешение/Гц, длительность, оборудование, Windows и версии игры/драйвера.";
        double fps = (after.MeanFps / before.MeanFps - 1) * 100;
        double low = (after.MeanLowFps / before.MeanLowFps - 1) * 100;
        bool overlap = before.MinFps <= after.MaxFps && after.MinFps <= before.MaxFps;
        return $"Среднее по прогонам: {before.MeanFps:0.0} → {after.MeanFps:0.0} FPS ({fps:+0.0;-0.0;0}%). "
            + $"Средний 1% low: {low:+0.0;-0.0;0}%.\nДиапазон FPS: {before.MinFps:0.0}–{before.MaxFps:0.0} → {after.MinFps:0.0}–{after.MaxFps:0.0}. "
            + (overlap ? "Диапазоны перекрываются; устойчивый прирост этим сравнением не подтверждён."
                : "Диапазоны не перекрываются. Это наблюдение этих прогонов, а не гарантия прироста.")
            + "\nВсе прогоны включены. Одинаковое прохождение сцены нужно обеспечить вручную.";
    }
}
