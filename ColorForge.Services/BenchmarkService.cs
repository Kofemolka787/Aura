using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed record BenchmarkProgress(int Percent, string Message);

public sealed class BenchmarkService
{
    private const string PresentMonHash = "9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191";

    public string OutputDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AURA",
        "Benchmarks");

    public IReadOnlyList<string> GetCandidateProcesses()
    {
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.Id != Environment.ProcessId && !string.IsNullOrWhiteSpace(process.ProcessName))
                    names.Add(process.ProcessName + ".exe");
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        string[] preferred = { "cs2.exe", "dota2.exe", "RustClient.exe" };
        return names
            .OrderBy(x => Array.FindIndex(preferred, p => p.Equals(x, StringComparison.OrdinalIgnoreCase)) is int rank && rank >= 0 ? rank : 99)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<BenchmarkResult> RunAsync(
        string processName,
        int durationSeconds,
        IProgress<BenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(processName))
            throw new ArgumentException("Выбери запущенную игру.", nameof(processName));
        durationSeconds = Math.Clamp(durationSeconds, 10, 180);

        string tool = EmbeddedToolExtractor.Extract(
            "PresentMon-2.5.1-x64.exe",
            "PresentMon-2.5.1-x64.exe",
            PresentMonHash);
        Directory.CreateDirectory(OutputDirectory);
        string safeName = Path.GetFileNameWithoutExtension(processName).Replace(' ', '-');
        string csv = Path.Combine(OutputDirectory, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = tool,
            Arguments = $"--process_name \"{processName}\" --output_file \"{csv}\" --timed {durationSeconds} --terminate_after_timed --stop_existing_session",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(tool)!
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить PresentMon.");
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
            }
        });

        Stopwatch timer = Stopwatch.StartNew();
        while (!process.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int percent = Math.Clamp((int)(timer.Elapsed.TotalSeconds / durationSeconds * 100), 0, 99);
            progress?.Report(new BenchmarkProgress(percent, $"Запись кадров · {Math.Max(0, durationSeconds - (int)timer.Elapsed.TotalSeconds)} сек."));
            await Task.Delay(500, cancellationToken);
        }

        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        if (process.ExitCode != 0 && !File.Exists(csv))
            throw new InvalidOperationException("PresentMon завершился с ошибкой: " + TrimMessage(stderr));

        progress?.Report(new BenchmarkProgress(100, "Анализ фреймтаймов…"));
        BenchmarkResult result = Parse(csv, processName);
        string summaryPath = Path.ChangeExtension(csv, ".json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return result;
    }

    internal static BenchmarkResult Parse(string csvPath, string processName)
    {
        using StreamReader reader = new StreamReader(csvPath, Encoding.UTF8, true);
        string? headerLine = reader.ReadLine();
        if (headerLine == null)
            throw new InvalidDataException("PresentMon не записал кадры.");

        string[] headers = ParseCsvLine(headerLine).ToArray();
        int frameIndex = FindColumn(headers, "MsBetweenPresents", "MsBetweenDisplayChange", "DisplayedTime");
        if (frameIndex < 0)
            throw new InvalidDataException("В отчёте PresentMon нет столбца фреймтайма.");

        List<double> frameTimes = new List<double>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            IReadOnlyList<string> fields = ParseCsvLine(line);
            if (frameIndex >= fields.Count)
                continue;
            if (double.TryParse(fields[frameIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && value > 0.05 && value < 5000)
            {
                frameTimes.Add(value);
            }
        }

        if (frameTimes.Count < 30)
            throw new InvalidDataException("Слишком мало кадров. Убедись, что игра запущена и выбрана верно.");

        frameTimes.Sort();
        double mean = frameTimes.Average();
        double median = Percentile(frameTimes, 0.50);
        int worstCount = Math.Max(1, frameTimes.Count / 100);
        double worstAverage = frameTimes.Skip(frameTimes.Count - worstCount).Average();
        double stutterThreshold = Math.Max(33.3, median * 2.0);

        return new BenchmarkResult
        {
            ProcessName = processName,
            FrameCount = frameTimes.Count,
            DurationSeconds = frameTimes.Sum() / 1000.0,
            AverageFps = 1000.0 / mean,
            OnePercentLowFps = 1000.0 / worstAverage,
            P95FrameTimeMs = Percentile(frameTimes, 0.95),
            P99FrameTimeMs = Percentile(frameTimes, 0.99),
            StutterCount = frameTimes.Count(x => x > stutterThreshold),
            CsvPath = csvPath
        };
    }

    private static int FindColumn(IReadOnlyList<string> headers, params string[] names)
    {
        foreach (string name in names)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                if (headers[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }
        return -1;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        double index = (sorted.Count - 1) * percentile;
        int low = (int)Math.Floor(index);
        int high = (int)Math.Ceiling(index);
        if (low == high)
            return sorted[low];
        return sorted[low] + (sorted[high] - sorted[low]) * (index - low);
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        List<string> values = new List<string>();
        StringBuilder current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == ',' && !quoted)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        values.Add(current.ToString());
        return values;
    }

    private static string TrimMessage(string text)
    {
        string value = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length > 240 ? value[..240] + "…" : value;
    }
}
