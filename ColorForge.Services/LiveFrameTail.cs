using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ColorForge.Services;

public sealed record LiveFrameSnapshot(double? Fps, double? LowFps, double[] FrameTimesMs, string Status)
{
    public DateTime SampledAtUtc { get; init; } = DateTime.UtcNow;
}

// Consumes only complete CSV lines, with bounded per-stream rolling histories.
public sealed class LiveFrameTail
{
    private sealed class Stream
    {
        public Queue<double> Frames { get; } = new();
        public double Duration;
        public DateTime LastData;
    }
    private readonly Dictionary<string, Stream> _streams = new(StringComparer.Ordinal);
    private readonly string _process;
    private string _pending = string.Empty;
    private string[]? _headers;
    private int _frame, _busy, _wait, _app, _pid, _swap, _type;
    public LiveFrameTail(string process) => _process = process;
    public void Append(string text, DateTime now)
    {
        string combined = _pending + text;
        int end, start = 0;
        while ((end = combined.IndexOf('\n', start)) >= 0)
        {
            string line = combined[start..end].TrimEnd('\r'); start = end + 1;
            if (line.Length > 65536) throw new InvalidDataException("Слишком длинная строка живого CSV.");
            if (_headers == null) { Headers(line.TrimStart('\uFEFF')); continue; }
            Read(line, now);
        }
        _pending = combined[start..];
        if (_pending.Length > 65536) throw new InvalidDataException("Незавершённая строка живого CSV превышает лимит.");
    }
    private void Headers(string line)
    {
        _headers = BenchmarkService.ParseCsvLine(line).ToArray();
        int Column(params string[] names) => names.Select(name => Array.FindIndex(_headers, x => x.Equals(name, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(x => x >= 0, -1);
        _frame = Column("MsBetweenPresents", "FrameTime", "CPUFrameTime"); _busy = Column("MsCPUBusy", "CPUBusy"); _wait = Column("MsCPUWait", "CPUWait");
        _app = Column("Application"); _pid = Column("ProcessID"); _swap = Column("SwapChainAddress"); _type = Column("FrameType");
        if (_frame < 0 && (_busy < 0 || _wait < 0)) throw new InvalidDataException("PresentMon не предоставил время кадров приложения.");
    }
    private void Read(string line, DateTime now)
    {
        var row = BenchmarkService.ParseCsvLine(line);
        string Cell(int index) => index >= 0 && index < row.Count ? row[index] : "";
        double? Number(int index) => double.TryParse(Cell(index), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? n : null;
        if (_app >= 0 && !Path.GetFileName(Cell(_app)).Equals(Path.GetFileName(_process), StringComparison.OrdinalIgnoreCase)) return;
        if (_type >= 0 && Cell(_type).Length > 0 && !Cell(_type).Equals("Application", StringComparison.OrdinalIgnoreCase)) return;
        double? interval = _frame >= 0 ? Number(_frame) : Number(_busy) + Number(_wait);
        if (interval is not (> 0 and <= 300000)) return;
        string key = Cell(_pid) + "/" + Cell(_swap);
        if (!_streams.TryGetValue(key, out Stream? stream))
        {
            if (_streams.Count == 16) _streams.Remove(_streams.MinBy(x => x.Value.LastData).Key);
            _streams[key] = stream = new();
        }
        stream.Frames.Enqueue(interval.Value); stream.Duration += interval.Value; stream.LastData = now;
        while (stream.Frames.Count > 2048 || (stream.Frames.Count > 1 && stream.Duration - stream.Frames.Peek() >= 2000))
            stream.Duration -= stream.Frames.Dequeue();
    }
    public LiveFrameSnapshot Snapshot(DateTime now)
    {
        Stream? stream = _streams.Values.Where(x => now - x.LastData <= TimeSpan.FromSeconds(2))
            .OrderByDescending(x => x.Frames.Count).FirstOrDefault();
        if (stream == null) return new(null, null, Array.Empty<double>(), "FPS — · ожидание новых кадров");
        double[] frames = stream.Frames.ToArray();
        double? low = frames.Length >= 30 ? 1000 / frames.Order().TakeLast(Math.Max(1, (int)Math.Ceiling(frames.Length * 0.01))).Average() : null;
        return new(1000 * frames.Length / stream.Duration, low, frames, $"PresentMon · окно {stream.Duration / 1000:0.0} с") { SampledAtUtc = now };
    }
}
