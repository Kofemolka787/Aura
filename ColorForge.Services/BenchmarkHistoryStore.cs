using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed class BenchmarkHistoryStore
{
    private readonly string _directory;
    public BenchmarkHistoryStore(string directory) => _directory = directory;
    public IReadOnlyList<BenchmarkResult> Load()
    {
        if (!Directory.Exists(_directory)) return Array.Empty<BenchmarkResult>();
        List<BenchmarkResult> results = new();
        foreach (string path in Directory.GetFiles(_directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
        {
            try
            {
                if (new FileInfo(path).Length > 32 * 1024 * 1024) continue;
                var result = JsonSerializer.Deserialize<BenchmarkResult>(File.ReadAllText(path));
                if (result != null && result.FrameCount > 0 && double.IsFinite(result.AverageFps)) results.Add(result);
            }
            catch (Exception exception) { AppLogService.Error("Benchmark history file skipped.", exception); }
        }
        return results;
    }
    public static string Compare(BenchmarkResult before, BenchmarkResult after)
    {
        if (!before.ProcessName.Equals(after.ProcessName, StringComparison.OrdinalIgnoreCase) || before.Metric != after.Metric)
            return "Выбери замеры одной игры с одинаковой метрикой. Сцена, разрешение и настройки игры должны совпадать.";
        double fps = before.AverageFps > 0 ? (after.AverageFps / before.AverageFps - 1) * 100 : 0;
        double low = before.OnePercentLowFps > 0 ? (after.OnePercentLowFps / before.OnePercentLowFps - 1) * 100 : 0;
        return $"Средний FPS: {fps:+0.0;-0.0;0}% · 1% low: {low:+0.0;-0.0;0}% · скачки: {before.StutterCount} → {after.StutterCount}. Результат зависит от одинаковой сцены.";
    }
}
