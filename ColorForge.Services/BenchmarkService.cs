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

public sealed record BenchmarkProgress(int Percent, string Message, LiveFrameSnapshot? Frames = null);
public sealed class BenchmarkService
{
    private const string PresentMonHash = "9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191";
    private readonly IExternalToolRunner _runner;
    private readonly Func<string> _toolProvider;
    public string OutputDirectory { get; }
    internal string GetToolPath() => _toolProvider();
    public BenchmarkService(IExternalToolRunner? runner = null, string? directory = null, Func<string>? toolProvider = null)
    {
        _runner = runner ?? new ExternalToolRunner();
        _toolProvider = toolProvider ?? (() => EmbeddedToolExtractor.Extract("PresentMon-2.5.1-x64.exe", "PresentMon-2.5.1-x64.exe", PresentMonHash));
        OutputDirectory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AURA", "Benchmarks");
    }

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


    public async Task<BenchmarkResult> RunAsync(string processName, int durationSeconds, IProgress<BenchmarkProgress>? progress, CancellationToken token, BenchmarkRunInfo? run = null)
    {
        if (string.IsNullOrWhiteSpace(processName)) throw new ArgumentException("Выбери запущенную игру.", nameof(processName));
        durationSeconds = Math.Clamp(durationSeconds, 10, 180);
        string tool = _toolProvider();
        Directory.CreateDirectory(OutputDirectory);
        string csv = Path.Combine(OutputDirectory, "capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".csv");
        string session = "AURA-" + Guid.NewGuid().ToString("N");
        DateTime startedAt = DateTime.UtcNow;
        Task<ToolResult> capture = _runner.RunAsync(tool,
            new[] { "--process_name", processName, "--output_file", csv, "--timed", durationSeconds.ToString(CultureInfo.InvariantCulture),
                "--terminate_after_timed", "--no_console_stats", "--v2_metrics", "--session_name", session },
            TimeSpan.FromSeconds(durationSeconds + 30), token, Path.GetDirectoryName(tool));
        Stopwatch time = Stopwatch.StartNew();
        using LiveCsvReader live = new(csv, processName);
        while (!capture.IsCompleted)
        {
            LiveFrameSnapshot? frames = null;
            try { if (progress != null) frames = live.Read(DateTime.UtcNow); }
            catch (IOException) { }
            progress?.Report(new(Math.Clamp((int)(time.Elapsed.TotalSeconds / durationSeconds * 100), 0, 99),
                $"Запись кадров · {Math.Max(0, durationSeconds - (int)time.Elapsed.TotalSeconds)} сек.", frames));
            await Task.WhenAny(capture, Task.Delay(250)).ConfigureAwait(false);
        }
        ToolResult result;
        try {
            result = await capture.ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidOperationException("PresentMon: " + TrimMessage(result.Error + " " + result.Output));
        }
        catch
        {
            try
            {
                await _runner.RunAsync(tool, new[] { "--session_name", session, "--terminate_existing_session" },
                    TimeSpan.FromSeconds(10), CancellationToken.None, Path.GetDirectoryName(tool)).ConfigureAwait(false);
            }
            catch (Exception cleanup) { AppLogService.Error("Owned PresentMon session cleanup failed.", cleanup); }
            throw;
        }
        if (!File.Exists(csv)) throw new InvalidDataException("PresentMon не создал CSV.");
        progress?.Report(new(100, "Анализ отдельного потока кадров"));
        BenchmarkResult benchmark = Parse(csv, processName, recordedAt: startedAt, run: run);
        Save(benchmark);
        return benchmark;
    }

    public void Save(BenchmarkResult result) => AtomicFile.Write(Path.ChangeExtension(result.CsvPath, ".json"), JsonSerializer.SerializeToUtf8Bytes(result));

    internal static BenchmarkResult Parse(string csvPath, string processName, string? selectedStream = null, DateTime? recordedAt = null, BenchmarkRunInfo? run = null)
    {
        if (new FileInfo(csvPath).Length > 128 * 1024 * 1024) throw new InvalidDataException("CSV превышает 128 МиБ.");
        using StreamReader reader = new(csvPath, Encoding.UTF8, true);
        string[] headers = ParseCsvLine(reader.ReadLine() ?? throw new InvalidDataException("Пустой CSV.")).ToArray();
        int frame = FindColumn(headers, "MsBetweenPresents", "FrameTime", "CPUFrameTime");
        int cpuBusy = FindColumn(headers, "MsCPUBusy", "CPUBusy");
        int cpuWait = FindColumn(headers, "MsCPUWait", "CPUWait");
        if (frame < 0 && (cpuBusy < 0 || cpuWait < 0))
            throw new InvalidDataException("Нет поддерживаемой метрики интервала кадров приложения. DisplayedTime отдельно не подменяет FPS приложения.");
        int swap = FindColumn(headers, "SwapChainAddress"), pid = FindColumn(headers, "ProcessID"), app = FindColumn(headers, "Application");
        int timestamp = FindColumn(headers, "CPUStartTime", "CPUStartQPCTime", "TimeInSeconds"), dropped = FindColumn(headers, "Dropped");
        int displayed = FindColumn(headers, "DisplayedTime"), frameType = FindColumn(headers, "FrameType");
        bool timestampSeconds = timestamp >= 0 && headers[timestamp].Equals("TimeInSeconds", StringComparison.OrdinalIgnoreCase);
        Dictionary<string, List<Frame>> streams = new(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length > 1024 * 1024) throw new InvalidDataException("Слишком длинная строка CSV.");
            var values = ParseCsvLine(line);
            string Cell(int index) => index >= 0 && index < values.Count ? values[index] : string.Empty;
            if (app >= 0 && !Path.GetFileName(Cell(app)).Equals(Path.GetFileName(processName), StringComparison.OrdinalIgnoreCase)) continue;
            string key = (Cell(pid).Length == 0 ? "PID ?" : Cell(pid)) + " / " + (Cell(swap).Length == 0 ? "один поток" : Cell(swap));
            if (!streams.TryGetValue(key, out List<Frame>? frames)) streams[key] = frames = new();
            double? Number(int index) => double.TryParse(Cell(index), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;
            double? interval = frame >= 0 ? Number(frame) : Number(cpuBusy) + Number(cpuWait);
            double? stamp = Number(timestamp);
            if (timestampSeconds && stamp.HasValue) stamp *= 1000;
            bool generated = frameType >= 0 && !string.IsNullOrWhiteSpace(Cell(frameType)) && !Cell(frameType).Equals("Application", StringComparison.OrdinalIgnoreCase);
            bool notDisplayed = Cell(dropped) is "1" or "True" or "true" || (displayed >= 0 && !Number(displayed).HasValue);
            frames.Add(new(interval, stamp, generated, notDisplayed));
        }
        var choices = streams.Select(pair => new FrameStreamChoice { Id = pair.Key, FrameCount = pair.Value.Count(x => !x.Generated && x.Interval is > 0 and <= 300000) })
            .OrderByDescending(x => x.FrameCount).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
        string chosen = selectedStream ?? choices.FirstOrDefault()?.Id ?? throw new InvalidDataException("Нет кадров выбранного процесса.");
        if (!streams.TryGetValue(chosen, out List<Frame>? rows)) throw new InvalidDataException("Выбранный поток отсутствует.");
        Frame[] valid = rows.Where(x => !x.Generated && x.Interval is > 0 and <= 300000).ToArray();
        if (valid.Length < 30) throw new InvalidDataException("В выбранном потоке меньше 30 кадров.");
        double[] ordered = valid.Select(x => x.Interval!.Value).ToArray();
        double[] sorted = ordered.Order().ToArray();
        double mean = sorted.Average(), median = Percentile(sorted, 0.5);
        double duration = ordered.Sum();
        if (valid.All(x => x.Timestamp.HasValue))
            duration = valid.Max(x => x.Timestamp!.Value + x.Interval!.Value) - valid.Min(x => x.Timestamp!.Value);
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("Некорректный интервал записи.");
        return new()
        {
            Run = run,
            ProcessName = processName, RecordedAt = recordedAt ?? File.GetCreationTimeUtc(csvPath), StreamId = chosen, Streams = choices, FrameCount = ordered.Length,
            DurationSeconds = duration / 1000, AverageFps = 1000 / mean,
            OnePercentLowFps = 1000 / sorted.TakeLast(Math.Max(1, (int)Math.Ceiling(sorted.Length * 0.01))).Average(),
            P95FrameTimeMs = Percentile(sorted, 0.95), P99FrameTimeMs = Percentile(sorted, 0.99),
            StutterCount = sorted.Count(x => x > Math.Max(33.3, median * 2)),
            DroppedFrameCount = rows.Count(x => x.NotDisplayed), GeneratedFrameCount = rows.Count(x => x.Generated),
            InvalidFrameCount = rows.Count(x => !x.Generated && !(x.Interval is > 0 and <= 300000)),
            FrameTimesMs = ordered, CsvPath = csvPath,
            Metric = frame >= 0 ? headers[frame] : headers[cpuBusy] + "+" + headers[cpuWait]
        };
    }
    private sealed record Frame(double? Interval, double? Timestamp, bool Generated, bool NotDisplayed);
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

    internal static IReadOnlyList<string> ParseCsvLine(string line)
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
