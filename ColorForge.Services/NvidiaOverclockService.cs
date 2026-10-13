using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed record GpuTuneSupport(bool IsSupported, string GpuName, int MaximumOffsetMhz, string Detail);
public sealed partial class NvidiaOverclockService
{
    private readonly IHardwareTelemetry _hardware;
    private readonly IGpuStressRunner _stress;
    private readonly IGpuClockController _controller;
    private readonly IHardwareRecoveryStore _recovery;
    private const string SnapshotKind = "gpu-khz";
    private GpuClockState? _adapter;
    public bool ExtendedTest { get; set; }
    public string? ActiveGpuName => _adapter?.Target.Name;
    public string? TelemetrySelector => _adapter?.Target.TelemetrySelector;

    public NvidiaOverclockService(IHardwareTelemetry hardware, IGpuClockController controller, IGpuStressRunner stress, IHardwareRecoveryStore recovery)
    { _hardware = hardware; _controller = controller; _stress = stress; _recovery = recovery; }

    public GpuTuneSupport ProbeSupport()
    {
        try
        {
            GpuClockState adapter = FindAdapter();
            HardwareSafety.RequireTemperature(_hardware.ReadSnapshot(adapter.Target.TelemetrySelector), true);
            return new(true, adapter.Target.Name, Math.Min(300, adapter.MaximumKhz / 1000), $"GPU сопоставлен по физическому устройству. поиск до {Math.Min(300, adapter.MaximumKhz / 1000)} MHz; проверка вычислений и температур.");
        }
        catch (Exception exception) { return new(false, ActiveGpuName ?? "NVIDIA", 0, exception.Message); }
    }

    private GpuClockState FindAdapter() => _adapter = _controller.Read(_adapter?.Target.DeviceId);
    private int ReadOffset(GpuClockState adapter) => _controller.Read(adapter.Target.DeviceId).OffsetKhz;

    private static ulong Encode(int value) => unchecked((uint)value);
    private static int Decode(ulong value) => unchecked((int)(uint)value);
    private HardwareChangeSnapshot Capture(GpuClockState adapter)
    {
        int current = ReadOffset(adapter);
        HardwareChangeSnapshot? snapshot = _recovery.Load(SnapshotKind, adapter.Target.DeviceId);
        if (snapshot == null)
        {
            snapshot = new() { Kind = SnapshotKind, DeviceId = adapter.Target.DeviceId, BootId = _recovery.BootId,
                OriginalValue = Encode(current), TargetValue = Encode(current) };
            _recovery.Save(snapshot);
        }
        else if (Encode(current) != snapshot.OriginalValue && Encode(current) != snapshot.TargetValue)
            throw new InvalidOperationException("GPU изменён другой программой. Автоматическая запись остановлена.");
        return snapshot;
    }

    private void SetVerified(GpuClockState adapter, int value) => _controller.WriteVerified(adapter.Target.DeviceId, value);

    private void Write(GpuClockState adapter, HardwareChangeSnapshot snapshot, int value)
    {
        snapshot.TargetValue = Encode(value); snapshot.Pending = true;
        _recovery.Save(snapshot);
        SetVerified(adapter, value);
    }
    private void Restore(GpuClockState adapter, HardwareChangeSnapshot snapshot)
    {
        SetVerified(adapter, Decode(snapshot.OriginalValue));
        snapshot.TargetValue = snapshot.OriginalValue; snapshot.Pending = false;
        _recovery.Save(snapshot);
    }

    public void ApplySavedProfile(OverclockPreferences preferences)
    {
        using IDisposable gate = HardwareOperations.Enter();
        GpuClockState adapter = _adapter = _controller.Read(preferences.GpuDeviceId);
        if (preferences.GpuDeviceId != adapter.Target.DeviceId) throw new InvalidOperationException("На этом GPU профиль не проверен. Запусти новый тест.");
        HardwareSafety.RequireTemperature(_hardware.ReadSnapshot(adapter.Target.TelemetrySelector), true);
        HardwareChangeSnapshot snapshot = Capture(adapter);
        if (snapshot.Pending) { Restore(adapter, snapshot); throw new InvalidOperationException("Восстановлен незавершённый тест GPU; автоприменение пропущено."); }
        try
        {
            Write(adapter, snapshot, preferences.LearnedGpuCoreOffsetKhz != 0 ? preferences.LearnedGpuCoreOffsetKhz : checked(preferences.LearnedGpuCoreOffsetMhz * 1000));
            snapshot.Pending = false; _recovery.Save(snapshot);
        }
        catch (Exception operation)
        {
            try { Restore(adapter, snapshot); }
            catch (Exception rollback) { throw new AggregateException("GPU: возврат не подтверждён; снимок сохранён.", operation, rollback); }
            throw;
        }
    }

    public void Reset()
    {
        using IDisposable gate = HardwareOperations.Enter();
        GpuClockState adapter = FindAdapter();
        HardwareChangeSnapshot snapshot = _recovery.Load(SnapshotKind, adapter.Target.DeviceId)
            ?? throw new InvalidOperationException("Нет исходного снимка AURA для выбранного GPU.");
        Restore(adapter, snapshot);
    }

    public bool RecoverPendingChanges()
    {
        var pending = _recovery.PendingSnapshots(SnapshotKind);
        if (pending.Count == 0) return false;
        using IDisposable gate = HardwareOperations.Enter();
        foreach (var snapshot in pending) Restore(_controller.Read(snapshot.DeviceId), snapshot);
        return true;
    }

    public async Task<AutoTuneResult> TuneAsync(IProgress<AutoTuneProgress>? progress, CancellationToken token)
    {
        using IDisposable gate = await HardwareOperations.EnterAsync(token).ConfigureAwait(false);
        GpuClockState adapter = FindAdapter();
        HardwareSafety.RequireTemperature(_hardware.ReadSnapshot(adapter.Target.TelemetrySelector), true);
        HardwareChangeSnapshot snapshot = Capture(adapter);
        int original = Decode(snapshot.OriginalValue), selected = original;
        double peak = 0;
        try
        {
            Restore(adapter, snapshot);
            GpuStressResult baseline = await _stress.RunAsync(ExtendedTest ? 20 : 8, adapter.Target, null, token).ConfigureAwait(false);
            if (!baseline.Stable) throw new InvalidOperationException("Исходный GPU-профиль не прошёл нагрузочную проверку.");
            peak = baseline.PeakTemperatureC;
            for (int offset = original + 15000; offset <= Math.Min(300000, adapter.MaximumKhz); offset += 15000)
            {
                token.ThrowIfCancellationRequested();
                Write(adapter, snapshot, offset);
                int candidate = offset;
                GpuStressResult result = await _stress.RunAsync(ExtendedTest ? 30 : 6, adapter.Target, (sample, percent) =>
                    progress?.Report(new(Math.Clamp((candidate - original) * 75 / Math.Max(15000, Math.Min(300000, adapter.MaximumKhz) - original), 1, 78),
                        $"Проверка {candidate / 1000d:0.###} MHz", $"GPU {sample.GpuTemperatureC:0} °C", sample, candidate)), token).ConfigureAwait(false);
                peak = Math.Max(peak, result.PeakTemperatureC);
                if (!result.Stable) break;
                selected = offset;
            }
            int safe = selected > original + 15000 ? selected - 15000 : selected;
            Write(adapter, snapshot, safe);
            GpuStressResult confirmation = await _stress.RunAsync(ExtendedTest ? 120 : 18, adapter.Target, (sample, percent) =>
                progress?.Report(new(80 + percent / 5, "Подтверждение вычислений", $"GPU {sample.GpuTemperatureC:0} °C", sample, safe)), token).ConfigureAwait(false);
            peak = Math.Max(peak, confirmation.PeakTemperatureC);
            if (!confirmation.Stable) { Restore(adapter, snapshot); safe = original; }
            snapshot.Pending = false; snapshot.TargetValue = Encode(safe); _recovery.Save(snapshot);
            return new()
            {
                Target = AutoTuneTarget.GraphicsCard, Success = true, GpuCoreOffsetMhz = safe / 1000, GpuCoreOffsetKhz = safe, GpuDeviceId = adapter.Target.DeviceId,
                Title = safe == original ? "Сохранены исходные частоты GPU" : $"Проверено смещение {safe / 1000d:0.###} MHz",
                Detail = "Проверены результат вычислений, нагрузка и температура. Исходные параметры сохранены; автоприменение выбирается отдельно.",
                PeakTemperatureC = peak, ImprovementPercent = 0, GainKind = "FPS измеряется отдельно в Benchmark Lab"
            };
        }
        catch (Exception operation)
        {
            try { Restore(adapter, snapshot); }
            catch (Exception rollback) { throw new AggregateException("GPU: возврат не подтверждён. Снимок сохранён; перезагрузи Windows.", operation, rollback); }
            throw;
        }
    }
}
