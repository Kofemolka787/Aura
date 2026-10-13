using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;

namespace ColorForge.Services;

public enum CpuTuneBackend { None, AmdPbo, IntelTurboPower }
public sealed record CpuTuneSupport(bool IsSupported, string ProcessorName, string Detail, CpuTuneBackend Backend = CpuTuneBackend.None, string AdapterId = "none", bool TelemetryReady = false);

public sealed class CpuAutoTuneService
{
    private readonly IHardwareTelemetry _hardware;
    private readonly ICpuPowerController _controller;
    private readonly IHardwareRecoveryStore _recovery;
    public bool ExtendedTest { get; set; }

    public CpuAutoTuneService(IHardwareTelemetry hardware, ICpuPowerController? controller = null, IHardwareRecoveryStore? recovery = null)
    {
        _hardware = hardware;
        _controller = controller ?? new IntelPowerController();
        _recovery = recovery ?? new HardwareRecoveryStore();
    }

    public CpuTuneSupport ProbeSupport()
    {
        string name = "CPU";
        bool telemetryReady = false;
        try
        {
            name = _controller.ProcessorName;
            HardwareSafety.RequireTemperature(_hardware.ReadSnapshot(), false);
            telemetryReady = true;
            CpuPowerState state = _controller.Read();
            return new(true, name, $"Intel RAPL: PL1 {state.Pl1:0.0} W / PL2 {state.Pl2:0.0} W. Исходное состояние сохраняется до записи.", CpuTuneBackend.IntelTurboPower, state.DeviceId, true);
        }
        catch (Exception exception) { return new(false, name, exception.Message, TelemetryReady: telemetryReady); }
    }

    private HardwareChangeSnapshot Capture(CpuPowerState state)
    {
        HardwareChangeSnapshot? snapshot = _recovery.Load("cpu", state.DeviceId);
        if (snapshot == null)
        {
            snapshot = new() { Kind = "cpu", DeviceId = state.DeviceId, BootId = _recovery.BootId, OriginalValue = state.RawLimit, TargetValue = state.RawLimit };
            _recovery.Save(snapshot);
        }
        else if (state.RawLimit != snapshot.TargetValue && state.RawLimit != snapshot.OriginalValue)
            throw new InvalidOperationException("Лимиты CPU изменены другой программой. Сначала верни сохранённую базу или перезагрузи Windows.");
        return snapshot;
    }

    private void Write(HardwareChangeSnapshot snapshot, ulong target)
    {
        snapshot.TargetValue = target;
        snapshot.Pending = true;
        _recovery.Save(snapshot);
        _controller.WriteVerified(target);
    }

    private void Restore(HardwareChangeSnapshot snapshot)
    {
        _controller.WriteVerified(snapshot.OriginalValue);
        snapshot.TargetValue = snapshot.OriginalValue;
        snapshot.Pending = false;
        _recovery.Save(snapshot);
    }

    public void ResetToStock()
    {
        using IDisposable gate = HardwareOperations.Enter();
        CpuPowerState state = _controller.Read();
        HardwareChangeSnapshot? snapshot = _recovery.Load("cpu", state.DeviceId);
        if (snapshot == null) throw new InvalidOperationException("Нет исходного снимка AURA для этой загрузки. Предполагаемый Stock не записывается.");
        Restore(snapshot);
    }

    public bool RecoverPendingChanges()
    {
        var pending = _recovery.PendingSnapshots("cpu");
        if (pending.Count == 0) return false;
        using IDisposable gate = HardwareOperations.Enter();
        CpuPowerState current = _controller.Read();
        HardwareChangeSnapshot? snapshot = _recovery.Load("cpu", current.DeviceId);
        if (snapshot?.Pending != true) throw new InvalidOperationException("Исходный CPU незавершённого теста недоступен. Снимок сохранён.");
        Restore(snapshot);
        return true;
    }

    public void ApplySavedProfile(OverclockPreferences profile)
    {
        using IDisposable gate = HardwareOperations.Enter();
        HardwareSafety.RequireTemperature(_hardware.ReadSnapshot(), false);
        CpuPowerState current = _controller.Read();
        if (profile.CpuDeviceId != current.DeviceId || profile.CpuPl1Watts <= 0 || profile.CpuPl2Watts <= 0)
            throw new InvalidOperationException("Профиль CPU не проверен на этом устройстве. Выполни новый тест.");
        HardwareChangeSnapshot snapshot = Capture(current);
        if (snapshot.Pending) { Restore(snapshot); throw new InvalidOperationException("Восстановлен незавершённый тест CPU. Автоприменение пропущено."); }
        ulong target = IntelPowerPolicy.WithLimits(snapshot.OriginalValue, current.PowerUnit, profile.CpuPl1Watts, profile.CpuPl2Watts, current.MaximumWatts);
        try
        {
            Write(snapshot, target);
            snapshot.Pending = false;
            _recovery.Save(snapshot);
        }
        catch (Exception operation)
        {
            try { Restore(snapshot); }
            catch (Exception rollback) { throw new AggregateException("CPU: операция и возврат завершились ошибками; снимок сохранён.", operation, rollback); }
            throw;
        }
    }

    public async Task<AutoTuneResult> TuneAsync(IProgress<AutoTuneProgress>? progress, CancellationToken cancellationToken)
    {
        using IDisposable gate = await HardwareOperations.EnterAsync(cancellationToken).ConfigureAwait(false);
        HardwareSafety.RequireTemperature(_hardware.ReadSnapshot(), false);
        CpuPowerState current = _controller.Read();
        HardwareChangeSnapshot snapshot = Capture(current);
        if (snapshot.Pending) Restore(snapshot);
        try
        {
            Restore(snapshot);
            CpuPowerState baseline = _controller.Read();
            TestResult baseTest = await RunCpuLoadAsync(ExtendedTest ? 20 : 8, current.TemperatureLimitC, progress, 2, "Исходные лимиты", cancellationToken).ConfigureAwait(false);
            if (!baseTest.Stable) throw new InvalidOperationException("Температурный предел достигнут на исходных лимитах.");
            ulong selected = baseline.RawLimit;
            string selectedId = "stock";
            TestResult selectedTest = baseTest;
            double peak = baseTest.Peak;
            (string Id, double Factor)[] scales = { ("low", 1.05), ("medium", 1.12), ("ultra", 1.20) };
            for (int index = 0; index < scales.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scale = scales[index];
                double pl1 = Math.Min(baseline.MaximumWatts, baseline.Pl1 * scale.Factor);
                double pl2 = Math.Min(baseline.MaximumWatts, baseline.Pl2 * scale.Factor);
                ulong target = IntelPowerPolicy.WithLimits(baseline.RawLimit, baseline.PowerUnit, pl1, pl2, baseline.MaximumWatts);
                Write(snapshot, target);
                TestResult test = await RunCpuLoadAsync(ExtendedTest ? 30 : 10, current.TemperatureLimitC, progress, 18 + index * 18, scale.Id, cancellationToken).ConfigureAwait(false);
                peak = Math.Max(peak, test.Peak);
                if (!test.Stable || test.Score < selectedTest.Score * 0.985) { Write(snapshot, selected); break; }
                selected = target;
                selectedId = scale.Id;
                selectedTest = test;
            }
            Write(snapshot, selected);
            TestResult confirmation = await RunCpuLoadAsync(ExtendedTest ? 120 : 18, current.TemperatureLimitC, progress, 78, "Подтверждение", cancellationToken).ConfigureAwait(false);
            peak = Math.Max(peak, confirmation.Peak);
            if (!confirmation.Stable || confirmation.Score < baseTest.Score * 0.985)
            {
                Restore(snapshot); selected = baseline.RawLimit; selectedId = "stock"; confirmation = baseTest;
            }
            snapshot.Pending = false;
            snapshot.TargetValue = selected;
            _recovery.Save(snapshot);
            var limits = IntelPowerPolicy.Decode(selected, current.PowerUnit);
            progress?.Report(new(100, "Готово", $"PL1 {limits.Pl1:0.0} W / PL2 {limits.Pl2:0.0} W", _hardware.ReadSnapshot()));
            return new()
            {
                Target = AutoTuneTarget.Processor, Success = true, CpuProfileId = selectedId,
                CpuBackendId = "intel-rapl", CpuDeviceId = current.DeviceId,
                CpuPl1Watts = limits.Pl1, CpuPl2Watts = limits.Pl2, CpuPptWatts = (int)Math.Round(limits.Pl1),
                Title = selectedId == "stock" ? "Возвращены исходные лимиты CPU" : "Профиль Intel " + selectedId,
                Detail = $"Проверено чтением: PL1 {limits.Pl1:0.0} W / PL2 {limits.Pl2:0.0} W. Автоприменение выбирается отдельно.",
                PeakTemperatureC = peak, ImprovementPercent = Math.Max(0, (confirmation.Score / baseTest.Score - 1) * 100),
                GainKind = "Измерение CPU-нагрузки"
            };
        }
        catch (Exception operation)
        {
            try { Restore(snapshot); }
            catch (Exception rollback) { throw new AggregateException("CPU: возврат не подтверждён. Снимок сохранён; перезагрузи Windows.", operation, rollback); }
            throw;
        }
    }

    private async Task<TestResult> RunCpuLoadAsync(int seconds, double maximum, IProgress<AutoTuneProgress>? progress, int basePercent, string stage, CancellationToken token)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        long operations = 0;
        Task[] workers = Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount)).Select(worker => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                double x = 0.5;
                for (int i = 1; i <= 12000; i++) x = Math.Sqrt(x + i) * 1.0000001192092896;
                if (!double.IsFinite(x) || Math.Abs(x - 110.04564492874412) > 1e-9)
                    throw new InvalidOperationException("CPU вернул ошибочный результат вычисления.");
                Interlocked.Add(ref operations, 12000);
            }
        }, stop.Token)).ToArray();
        Stopwatch time = Stopwatch.StartNew();
        double peak = 0;
        bool stable = true;
        try
        {
            while (time.Elapsed.TotalSeconds < seconds)
            {
                token.ThrowIfCancellationRequested();
                if (workers.Any(x => x.IsFaulted)) await Task.WhenAll(workers.Where(x => x.IsFaulted)).ConfigureAwait(false);
                HardwareSnapshot snapshot = _hardware.ReadSnapshot();
                double temperature = HardwareSafety.RequireTemperature(snapshot, false);
                peak = Math.Max(peak, temperature);
                if (temperature >= maximum) { stable = false; break; }
                progress?.Report(new(Math.Clamp(basePercent + (int)(time.Elapsed.TotalSeconds / seconds * 17), 0, 98), stage,
                    $"CPU {temperature:0} °C; предел {maximum:0} °C", snapshot));
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(workers).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        return new(operations / Math.Max(0.1, time.Elapsed.TotalSeconds), peak, stable);
    }
    private sealed record TestResult(double Score, double Peak, bool Stable);
}
