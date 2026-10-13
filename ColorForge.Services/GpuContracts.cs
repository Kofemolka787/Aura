using System;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;
namespace ColorForge.Services;
public sealed record GpuTarget(string DeviceId, string Name, long AdapterLuid, string TelemetrySelector);
public sealed record GpuClockState(GpuTarget Target, int OffsetKhz, int MinimumKhz, int MaximumKhz);
public interface IGpuClockController
{
    GpuClockState Read(string? expectedDeviceId = null);
    void WriteVerified(string deviceId, int offsetKhz);
}
public sealed record GpuStressResult(bool Stable, double PeakTemperatureC, double AverageLoadPercent, TimeSpan Duration);
public interface IGpuStressRunner
{
    Task<GpuStressResult> RunAsync(int seconds, GpuTarget target, Action<HardwareSnapshot, int>? sample, CancellationToken token);
}
