using System;
using System.Collections.Generic;
using System.Linq;
using ColorForge.Models;
using LibreHardwareMonitor.Hardware;

namespace ColorForge.Services;

public sealed class HardwareMonitoringService : IDisposable
{
    private readonly object _gate = new object();
    private readonly Computer? _computer;
    private readonly UpdateVisitor _visitor = new UpdateVisitor();

    public HardwareMonitoringService()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true
            };
            _computer.Open();
        }
        catch
        {
            try
            {
                _computer?.Close();
            }
            catch
            {
            }
            _computer = null;
        }
    }

    public bool IsAvailable => _computer != null;

    public HardwareSnapshot ReadSnapshot(string? preferredGpuName = null)
    {
        lock (_gate)
        {
            if (_computer == null)
                return new HardwareSnapshot();

            try
            {
                _computer.Accept(_visitor);
                List<IHardware> hardware = Flatten(_computer.Hardware).ToList();
                IHardware? cpu = hardware.FirstOrDefault(x => x.HardwareType == HardwareType.Cpu);
                IHardware? gpu = hardware
                    .Where(x => x.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    .OrderBy(x => string.IsNullOrWhiteSpace(preferredGpuName) || !GpuNamesMatch(x.Name, preferredGpuName) ? 1 : 0)
                    .ThenBy(x => x.HardwareType switch
                    {
                        HardwareType.GpuNvidia => 0,
                        HardwareType.GpuAmd => 1,
                        _ => 2
                    })
                    .FirstOrDefault();

                return new HardwareSnapshot
                {
                    CpuName = cpu?.Name ?? "Процессор не определён",
                    GpuName = gpu?.Name ?? "Видеокарта не определена",
                    CpuTemperatureC = Find(cpu, SensorType.Temperature, "Package", "Core (Tctl/Tdie)", "CPU Package", "Core"),
                    CpuLoadPercent = Find(cpu, SensorType.Load, "CPU Total", "Total"),
                    CpuClockMhz = Average(cpu, SensorType.Clock, "Core"),
                    CpuPowerW = Find(cpu, SensorType.Power, "Package", "CPU Package", "CPU PPT"),
                    GpuTemperatureC = Find(gpu, SensorType.Temperature, "GPU Core", "Core"),
                    GpuHotSpotC = Find(gpu, SensorType.Temperature, "Hot Spot", "GPU Hot Spot"),
                    GpuLoadPercent = Find(gpu, SensorType.Load, "GPU Core", "Core"),
                    GpuCoreClockMhz = Find(gpu, SensorType.Clock, "GPU Core", "Core"),
                    GpuMemoryClockMhz = Find(gpu, SensorType.Clock, "GPU Memory", "Memory"),
                    GpuPowerW = Find(gpu, SensorType.Power, "GPU Package", "GPU Power", "Package"),
                    GpuMemoryUsedMb = Find(gpu, SensorType.SmallData, "GPU Memory Used", "Memory Used"),
                    GpuMemoryTotalMb = Find(gpu, SensorType.SmallData, "GPU Memory Total", "Memory Total")
                };
            }
            catch
            {
                return new HardwareSnapshot();
            }
        }
    }

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> roots)
    {
        foreach (IHardware item in roots)
        {
            yield return item;
            foreach (IHardware child in Flatten(item.SubHardware))
                yield return child;
        }
    }

    private static double? Find(IHardware? hardware, SensorType type, params string[] preferredNames)
    {
        if (hardware == null)
            return null;

        ISensor? sensor = hardware.Sensors
            .Where(x => x.SensorType == type && x.Value.HasValue)
            .OrderBy(x => NameRank(x.Name, preferredNames))
            .FirstOrDefault();
        return sensor?.Value;
    }

    private static double? Average(IHardware? hardware, SensorType type, string contains)
    {
        if (hardware == null)
            return null;

        float[] values = hardware.Sensors
            .Where(x => x.SensorType == type && x.Value.HasValue && x.Name.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value!.Value)
            .Where(x => x > 0)
            .ToArray();
        return values.Length == 0 ? Find(hardware, type) : values.Average();
    }

    private static int NameRank(string name, IReadOnlyList<string> preferred)
    {
        for (int i = 0; i < preferred.Count; i++)
        {
            if (name.Contains(preferred[i], StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return preferred.Count + 1;
    }

    private static bool GpuNamesMatch(string left, string right)
    {
        static string Normalize(string value) => value
            .Replace("NVIDIA", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("GEFORCE", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("LAPTOP GPU", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        string a = Normalize(left);
        string b = Normalize(right);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
               a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _computer?.Close();
            }
            catch
            {
            }
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware child in hardware.SubHardware)
                child.Accept(this);
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}
