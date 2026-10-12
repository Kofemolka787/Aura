using System;

namespace ColorForge.Models;

public sealed class HardwareSnapshot
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public string CpuName { get; init; } = "Процессор не определён";

    public string GpuName { get; init; } = "Видеокарта не определена";

    public double? CpuTemperatureC { get; init; }

    public double? CpuLoadPercent { get; init; }

    public double? CpuClockMhz { get; init; }

    public double? CpuPowerW { get; init; }

    public double? GpuTemperatureC { get; init; }

    public double? GpuHotSpotC { get; init; }

    public double? GpuLoadPercent { get; init; }

    public double? GpuCoreClockMhz { get; init; }

    public double? GpuMemoryClockMhz { get; init; }

    public double? GpuPowerW { get; init; }

    public double? GpuMemoryUsedMb { get; init; }

    public double? GpuMemoryTotalMb { get; init; }

    public bool HasCpuTelemetry => CpuTemperatureC.HasValue || CpuLoadPercent.HasValue || CpuClockMhz.HasValue;

    public bool HasGpuTelemetry => GpuTemperatureC.HasValue || GpuLoadPercent.HasValue || GpuCoreClockMhz.HasValue;
}
