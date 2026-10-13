using ColorForge.Models;
using ColorForge.Services;
using LibreHardwareMonitor.Hardware;
using Xunit;
using System.Reflection;

public sealed class SensorSamplingTests
{
    [Fact] public void FailedDriverUpdateCannotReuseCachedTemperatureOrLoad()
    {
        var hardware = new FixtureHardware();
        Set(hardware.Temperature, 55);
        Set(hardware.Load, 80);
        // Real LHM Sensor caches these values when its hardware driver writes nothing.
        hardware.Update();
        Assert.Equal(55f, hardware.Temperature.Value);
        FreshSensorSampling.BeginUpdate(new[] { hardware });
        hardware.Update();
        Assert.Null(hardware.Temperature.Value);
        Assert.Null(hardware.Load.Value);
        Assert.Equal(55f, hardware.Temperature.Max); // No loss of historical maxima.
        Assert.Throws<InvalidOperationException>(() => HardwareSafety.RequireTemperature(new HardwareSnapshot
            { CpuTemperatureC = hardware.Temperature.Value }, false));
    }
    [Fact] public void RealNewSensorAssignmentsAreAcceptedAfterClearingCachedValues()
    {
        var hardware = new FixtureHardware { HasReading = true };
        FreshSensorSampling.BeginUpdate(new[] { hardware });
        hardware.Update();
        Assert.Equal(62, HardwareSafety.RequireTemperature(new HardwareSnapshot { CpuTemperatureC = hardware.Temperature.Value }, false));
        Assert.Equal(90f, hardware.Load.Value);
    }
    private static ISensor Create(string name, SensorType kind, Hardware hardware, ISettings settings)
    {
        Type type = typeof(ISensor).Assembly.GetType("LibreHardwareMonitor.Hardware.Sensor", true)!;
        return (ISensor)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { name, 0, kind, hardware, settings }, null)!;
    }
    private static void Set(ISensor sensor, float value) => sensor.GetType().GetProperty("Value")!.SetValue(sensor, value);

    private sealed class FixtureHardware : Hardware
    {
        public ISensor Temperature { get; }
        public ISensor Load { get; }
        public bool HasReading;
        public FixtureHardware() : base("fixture", new Identifier("fixture"), new MemorySettings())
        {
            Temperature = Create("CPU Package", SensorType.Temperature, this, _settings);
            Load = Create("CPU Total", SensorType.Load, this, _settings);
            _active.Add(Temperature); _active.Add(Load);
        }
        public override HardwareType HardwareType => HardwareType.Cpu;
        public override void Update() { if (HasReading) { Set(Temperature, 62); Set(Load, 90); } }
        public override string GetReport() => "fixture";
    }
    private sealed class MemorySettings : ISettings
    {
        public bool Contains(string name) => false;
        public string GetValue(string name, string value) => value;
        public void SetValue(string name, string value) { }
        public void Remove(string name) { }
    }
}
