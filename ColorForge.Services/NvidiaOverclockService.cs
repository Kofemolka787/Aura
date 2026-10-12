using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;

namespace ColorForge.Services;

public sealed record GpuTuneSupport(bool IsSupported, string GpuName, int MaximumOffsetMhz, string Detail);

public sealed class NvidiaOverclockService
{
    private readonly HardwareMonitoringService _hardware;
    private readonly GpuStressService _stress;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public string? ActiveGpuName { get; private set; }

    public NvidiaOverclockService(HardwareMonitoringService hardware)
    {
        _hardware = hardware;
        _stress = new GpuStressService(hardware);
    }

    public GpuTuneSupport ProbeSupport()
    {
        ActiveGpuName = null;
        try
        {
            GpuAdapter adapter = FindTunableAdapter();
            ActiveGpuName = adapter.Gpu.FullName;

            if (!_hardware.ReadSnapshot(adapter.Gpu.FullName).GpuTemperatureC.HasValue)
                return new GpuTuneSupport(false, adapter.Gpu.FullName, 0, "Недоступен датчик температуры GPU. AURA блокирует нагрузочный тест без теплового контроля.");

            return new GpuTuneSupport(
                true,
                adapter.Gpu.FullName,
                adapter.MaximumOffsetMhz,
                $"NVAPI P0 доступен · безопасный поиск до +{adapter.MaximumOffsetMhz} MHz · память, напряжение и power limit не изменяются.");
        }
        catch (Exception ex)
        {
            return new GpuTuneSupport(false, "NVIDIA", 0, "NVAPI: " + ex.Message);
        }
    }

    public async Task<AutoTuneResult> TuneAsync(
        IProgress<AutoTuneProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            GpuTuneSupport support = ProbeSupport();
            if (!support.IsSupported)
                throw new NotSupportedException(support.Detail);

            ApplyOffset(0);
            int selected = 0;
            double peak = 0;
            int step = 15;
            int testIndex = 0;
            int totalSteps = Math.Max(1, support.MaximumOffsetMhz / step);

            for (int offset = step; offset <= support.MaximumOffsetMhz; offset += step)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyOffset(offset);
                int candidate = offset;
                int localIndex = testIndex++;
                GpuStressResult test;
                try
                {
                    test = await _stress.RunAsync(6, support.GpuName, (snapshot, percent) =>
                    {
                        int overall = Math.Clamp(3 + (int)((localIndex + percent / 100d) / totalSteps * 76), 3, 79);
                        progress?.Report(new AutoTuneProgress(
                            overall,
                            $"Проверка +{candidate} MHz",
                            FormatSnapshot(snapshot),
                            snapshot,
                            candidate));
                    }, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    ApplyOffset(0);
                    throw;
                }
                catch
                {
                    ApplyOffset(selected);
                    break;
                }

                peak = Math.Max(peak, test.PeakTemperatureC);
                if (!test.Stable || test.PeakTemperatureC >= 83)
                {
                    ApplyOffset(selected);
                    break;
                }
                selected = offset;
            }

            int safeOffset = Math.Max(0, selected - (selected >= 30 ? 15 : 0));
            ApplyOffset(safeOffset);
            GpuStressResult confirmation = await _stress.RunAsync(18, support.GpuName, (snapshot, percent) =>
            {
                progress?.Report(new AutoTuneProgress(
                    80 + percent / 5,
                    "Финальная проверка",
                    FormatSnapshot(snapshot),
                    snapshot,
                    safeOffset));
            }, cancellationToken);
            peak = Math.Max(peak, confirmation.PeakTemperatureC);
            if (!confirmation.Stable)
            {
                safeOffset = 0;
                ApplyOffset(0);
            }

            HardwareSnapshot final = _hardware.ReadSnapshot(support.GpuName);
            double baseClock = Math.Max(1200, (final.GpuCoreClockMhz ?? 1800) - safeOffset);
            progress?.Report(new AutoTuneProgress(100, "Готово", $"Безопасный запас · +{safeOffset} MHz", final, safeOffset));
            return new AutoTuneResult
            {
                Target = AutoTuneTarget.GraphicsCard,
                Success = true,
                Title = safeOffset == 0 ? "Оставлена штатная частота" : $"Стабильное ядро +{safeOffset} MHz",
                Detail = "Настроено только ядро NVIDIA. Частота памяти, напряжение и лимит мощности не изменялись. Профиль сбрасывается драйвером/перезагрузкой.",
                GpuCoreOffsetMhz = safeOffset,
                PeakTemperatureC = peak,
                ImprovementPercent = safeOffset / baseClock * 100d
            };
        }
        catch
        {
            TryReset();
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void ApplyOffset(int offsetMhz)
    {
        GpuAdapter adapter = FindTunableAdapter();
        offsetMhz = Math.Clamp(offsetMhz, 0, adapter.MaximumOffsetMhz);

        PerformanceStates20ClockEntryV1 clock = new PerformanceStates20ClockEntryV1(
            PublicClockDomain.Graphics,
            new PerformanceStates20ParameterDelta(offsetMhz * 1000));
        PerformanceStates20InfoV1.PerformanceState20 state = new PerformanceStates20InfoV1.PerformanceState20(
            PerformanceStateId.P0_3DPerformance,
            new[] { clock },
            Array.Empty<PerformanceStates20BaseVoltageEntryV1>());
        PerformanceStates20InfoV1 info = new PerformanceStates20InfoV1(new[] { state }, 1, 0);
        GPUApi.SetPerformanceStates20(adapter.Gpu.Handle, info);
    }

    public void Reset() => ApplyOffset(0);

    private static string FormatSnapshot(HardwareSnapshot snapshot)
    {
        string temperature = snapshot.GpuTemperatureC.HasValue ? $"{snapshot.GpuTemperatureC:0} °C" : "температура —";
        string load = snapshot.GpuLoadPercent.HasValue ? $"{snapshot.GpuLoadPercent:0}%" : "нагрузка —";
        string clock = snapshot.GpuCoreClockMhz.HasValue ? $"{snapshot.GpuCoreClockMhz:0} MHz" : "частота —";
        return $"GPU {temperature} · {load} · {clock}";
    }

    private void TryReset()
    {
        try { Reset(); } catch { }
    }

    private static GpuAdapter FindTunableAdapter()
    {
        PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs().ToArray();
        if (gpus.Length == 0)
            throw new NotSupportedException("NVAPI не нашёл физическую видеокарту NVIDIA.");

        string lastReason = "Драйвер не открыл редактирование P0 State.";
        foreach (PhysicalGPU gpu in gpus)
        {
            try
            {
                var info = GPUApi.GetPerformanceStates20(gpu.Handle);
                if (!info.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out var clocks))
                {
                    lastReason = $"{gpu.FullName}: P0 State недоступен.";
                    continue;
                }

                var graphics = clocks.FirstOrDefault(x => x.DomainId == PublicClockDomain.Graphics);
                int maximum = graphics.FrequencyDeltaInkHz.DeltaRange.Maximum / 1000;
                if (!graphics.IsEditable || maximum <= 0)
                {
                    lastReason = $"{gpu.FullName}: смещение ядра заблокировано VBIOS или драйвером.";
                    continue;
                }

                return new GpuAdapter(gpu, Math.Clamp(maximum, 15, 300));
            }
            catch (Exception ex)
            {
                lastReason = $"{gpu.FullName}: {ex.Message}";
            }
        }

        throw new NotSupportedException(lastReason);
    }

    private sealed record GpuAdapter(PhysicalGPU Gpu, int MaximumOffsetMhz);
}
