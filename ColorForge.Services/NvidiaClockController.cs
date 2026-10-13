using System;
using System.Linq;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;

namespace ColorForge.Services;
internal sealed class NvidiaClockController : IGpuClockController
{
    private readonly object _gate = new();
    private (PhysicalGPU Gpu, GpuClockState State) Find(string? expected)
    {
        foreach (PhysicalGPU gpu in PhysicalGPU.GetPhysicalGPUs())
        {
            GpuTarget target = GpuIdentity.Resolve(gpu);
            if (expected != null && target.DeviceId != expected) continue;
            var graphics = GPUApi.GetPerformanceStates20(gpu.Handle).Clocks[PerformanceStateId.P0_3DPerformance]
                .Single(x => x.DomainId == PublicClockDomain.Graphics);
            if (!graphics.IsEditable || graphics.FrequencyDeltaInkHz.DeltaRange.Maximum <= 0) continue;
            var delta = graphics.FrequencyDeltaInkHz;
            return (gpu, new(target, delta.DeltaValue, delta.DeltaRange.Minimum, delta.DeltaRange.Maximum));
        }
        throw new NotSupportedException("Выбранный NVIDIA-адаптер недоступен или P0 заблокирован.");
    }
    public GpuClockState Read(string? expectedDeviceId = null) { lock (_gate) return Find(expectedDeviceId).State; }
    public void WriteVerified(string deviceId, int offsetKhz)
    {
        lock (_gate)
        {
            var selected = Find(deviceId);
            if (offsetKhz < selected.State.MinimumKhz || offsetKhz > selected.State.MaximumKhz) throw new ArgumentOutOfRangeException(nameof(offsetKhz));
            var clock = new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(offsetKhz));
            var state = new PerformanceStates20InfoV1.PerformanceState20(PerformanceStateId.P0_3DPerformance, new[] { clock }, Array.Empty<PerformanceStates20BaseVoltageEntryV1>());
            GPUApi.SetPerformanceStates20(selected.Gpu.Handle, new PerformanceStates20InfoV1(new[] { state }, 1, 0));
            if (Find(deviceId).State.OffsetKhz != offsetKhz) throw new InvalidOperationException("NVAPI не подтвердил точное смещение ядра.");
        }
    }
}
public sealed partial class NvidiaOverclockService
{
    public NvidiaOverclockService(IHardwareTelemetry hardware) : this(hardware, new NvidiaClockController(), new GpuStressService(hardware), new HardwareRecoveryStore()) { }
}
