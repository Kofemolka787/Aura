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
    [Fact] public void FailedDynamicClockPowerAndUsedMemoryQueriesDoNotReuseOldValues()
    {
        var hardware = new FixtureHardware();
        Set(hardware.Clock, 1500); Set(hardware.Power, 40); Set(hardware.MemoryUsed, 900); Set(hardware.MemoryTotal, 8192);
        FreshSensorSampling.BeginUpdate(new[] { hardware }); hardware.Update();
        Assert.Null(hardware.Clock.Value); Assert.Null(hardware.Power.Value); Assert.Null(hardware.MemoryUsed.Value);
        Assert.Equal(8192f, hardware.MemoryTotal.Value); // Fixed capacity is metadata, not a changing measurement.
    }
    private static ISensor Create(string name, SensorType kind, Hardware hardware, ISettings settings, int index = 0)
    {
        Type type = typeof(ISensor).Assembly.GetType("LibreHardwareMonitor.Hardware.Sensor", true)!;
        return (ISensor)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { name, index, kind, hardware, settings }, null)!;
    }
    private static void Set(ISensor sensor, float value) => sensor.GetType().GetProperty("Value")!.SetValue(sensor, value);

    private sealed class FixtureHardware : Hardware
    {
        public ISensor Temperature { get; }
        public ISensor Load { get; }
        public ISensor Clock { get; }
        public ISensor Power { get; }
        public ISensor MemoryUsed { get; }
        public ISensor MemoryTotal { get; }
        public bool HasReading;
        public FixtureHardware() : base("fixture", new Identifier("fixture"), new MemorySettings())
        {
            Temperature = Create("CPU Package", SensorType.Temperature, this, _settings);
            Load = Create("CPU Total", SensorType.Load, this, _settings);
            Clock = Create("GPU Core", SensorType.Clock, this, _settings);
            Power = Create("GPU Power", SensorType.Power, this, _settings);
            MemoryUsed = Create("GPU Memory Used", SensorType.SmallData, this, _settings);
            MemoryTotal = Create("GPU Memory Total", SensorType.SmallData, this, _settings, 1);
            _active.Add(Temperature); _active.Add(Load);
            _active.Add(Clock); _active.Add(Power); _active.Add(MemoryUsed); _active.Add(MemoryTotal);
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
