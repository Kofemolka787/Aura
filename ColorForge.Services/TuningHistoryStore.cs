using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ColorForge.Models;

namespace ColorForge.Services;
public sealed record TuningRecord(DateTimeOffset RecordedAtUtc, AutoTuneResult Result)
{
    public string Label => $"{RecordedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm} · {Result.Target} · {Result.Title}";
}
public sealed class TuningHistoryStore
{
    private readonly string _root;
    public TuningHistoryStore(string? directory = null) => _root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AURA", "TuningHistory");
    public void Save(AutoTuneResult result)
    {
        AtomicFile.Write(Path.Combine(_root, DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(new TuningRecord(DateTimeOffset.UtcNow, result)));
    }
    public IReadOnlyList<TuningRecord> Load()
    {
        if (!Directory.Exists(_root)) return Array.Empty<TuningRecord>();
        List<TuningRecord> records = new();
        foreach (string path in Directory.GetFiles(_root, "*.json").OrderByDescending(x => x).Take(100))
        {
            try { if (new FileInfo(path).Length <= 1024 * 1024 && JsonSerializer.Deserialize<TuningRecord>(File.ReadAllBytes(path)) is { Result: not null } r) records.Add(r); }
            catch (Exception e) { AppLogService.Warning("Unreadable tuning history: " + e.GetType().Name); }
        }
        return records;
    }
}
