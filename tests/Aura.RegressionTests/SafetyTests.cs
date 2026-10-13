using ColorForge.Models;
using ColorForge.Services;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class SafetyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    [InlineData(140d)]
    public void TemperatureControlRejectsMissingOrInvalidValues(double? temperature) =>
        Assert.Throws<InvalidOperationException>(() => HardwareSafety.RequireTemperature(new() { CpuTemperatureC = temperature }, false));

    [Fact]
    public void StaleOrFailedTelemetryCannotAuthorizeLoad()
    {
        Assert.Throws<InvalidOperationException>(() => HardwareSafety.RequireTemperature(new() { CpuTemperatureC = 50, TimestampUtc = DateTime.UtcNow.AddSeconds(-10) }, false));
        Assert.Throws<InvalidOperationException>(() => HardwareSafety.RequireTemperature(new() { CpuTemperatureC = 50, TelemetryValid = false }, false));
    }

    [Fact]
    public void IntelAbsoluteProfileIsIdempotentAcrossServiceRestarts()
    {
        Controller controller = new();
        MemoryRecovery recovery = new();
        OverclockPreferences preferences = new() { CpuDeviceId = "fixture-cpu", CpuPl1Watts = 120, CpuPl2Watts = 180 };
        new CpuAutoTuneService(new Telemetry(), controller, recovery).ApplySavedProfile(preferences);
        Assert.Equal(120, controller.Read().Pl1);
        new CpuAutoTuneService(new Telemetry(), controller, recovery).ApplySavedProfile(preferences);
        Assert.Equal(120, controller.Read().Pl1);
        Assert.Equal(180, controller.Read().Pl2);
        new CpuAutoTuneService(new Telemetry(), controller, recovery).ResetToStock();
        Assert.Equal(100, controller.Read().Pl1);
        Assert.Equal(150, controller.Read().Pl2);
    }

    [Fact]
    public void SnapshotIsDurableBeforeWriteAndRestoredAfterLostSensor()
    {
        MemoryRecovery recovery = new();
        Controller controller = new() { BeforeWrite = () => Assert.NotNull(recovery.Snapshot) };
        Telemetry telemetry = new();
        CpuAutoTuneService tune = new(telemetry, controller, recovery);
        tune.ApplySavedProfile(new() { CpuDeviceId = "fixture-cpu", CpuPl1Watts = 120, CpuPl2Watts = 180 });
        telemetry.Fail = true;
        tune.ResetToStock();
        Assert.Equal(100, controller.Read().Pl1);
        Assert.False(recovery.Snapshot!.Pending);
    }

    [Fact]
    public async Task SensorLossDuringLoadRestoresOriginalLimits()
    {
        Telemetry telemetry = new() { FailAfterRead = 1 };
        Controller controller = new();
        MemoryRecovery recovery = new();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CpuAutoTuneService(telemetry, controller, recovery).TuneAsync(null, CancellationToken.None));
        Assert.Equal(100, controller.Read().Pl1);
        Assert.False(recovery.Snapshot!.Pending);
    }

    [Fact]
    public void WrongDeviceCannotReceiveSavedProfile()
    {
        Controller controller = new();
        Assert.Throws<InvalidOperationException>(() => new CpuAutoTuneService(new Telemetry(), controller, new MemoryRecovery())
            .ApplySavedProfile(new() { CpuDeviceId = "another-cpu", CpuPl1Watts = 120, CpuPl2Watts = 180 }));
        Assert.Equal(0, controller.Writes);
    }

    [Fact]
    public async Task AllHardwareWritesShareOneGate()
    {
        using IDisposable lease = await HardwareOperations.EnterAsync(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => HardwareOperations.Enter());
    }

    [Fact]
    public void PowerPolicyPreservesOriginalEnableBitsAndRejectsLockedOrUnknownLimits()
    {
        ulong original = 100UL | (150UL << 32) | (17UL << 17);
        ulong target = IntelPowerPolicy.WithLimits(original, 1, 120, 180, 250);
        Assert.Equal(original & ~(0x7FFFUL | (0x7FFFUL << 32)), target & ~(0x7FFFUL | (0x7FFFUL << 32)));
        Assert.Throws<NotSupportedException>(() => IntelPowerPolicy.WithLimits(original | (1UL << 63), 1, 120, 180, 250));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntelPowerPolicy.WithLimits(original, 1, 120, 180, 0));
    }

    [Fact]
    public async Task FailedKeyDoesNotConsumeLoginToken()
    {
        DemoAuthService auth = new();
        LoginResult login = await auth.LoginAsync(DemoAuthService.DemoEmail, DemoAuthService.DemoPassword);
        Assert.False((await auth.ActivateAsync(login.LoginToken!, "incorrect-license", "fixture")).Success);
        Assert.True((await auth.ActivateAsync(login.LoginToken!, DemoAuthService.DemoLicenseKey, "fixture")).Success);
        Assert.False((await auth.ActivateAsync(login.LoginToken!, DemoAuthService.DemoLicenseKey, "fixture")).Success);
    }

    [Fact]
    public void DeletedGamesStayDeletedAndNestedSettingsAreValidated()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aura-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            SettingsStore store = new(directory);
            store.Save(new() { Games = new() });
            Assert.Empty(store.Load().Games);
            File.WriteAllText(store.SettingsPath, "{\"MonitorProfiles\":{\"DISPLAY1\":null},\"Crosshair\":{\"Size\":-100},\"Current\":{\"Gamma\":99999}}");
            AppSettings settings = store.Load();
            Assert.Empty(settings.MonitorProfiles);
            Assert.Equal(1, settings.Crosshair.Size);
            Assert.Equal(200, settings.Current.Gamma);
            File.WriteAllText(store.SettingsPath, "{broken-json");
            Assert.NotEmpty(store.Load().Profiles);
            Assert.Single(Directory.GetFiles(directory, "settings.corrupt-*.json"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void PendingCpuRecoveryRestoresOriginalBeforeAllowingNewStartupProfile()
    {
        var controller = new Controller(); var recovery = new MemoryRecovery();
        var tune = new CpuAutoTuneService(new Telemetry(), controller, recovery);
        tune.ApplySavedProfile(new() { CpuDeviceId = "fixture-cpu", CpuPl1Watts = 120, CpuPl2Watts = 180 });
        recovery.Snapshot!.Pending = true;
        Assert.True(tune.RecoverPendingChanges());
        Assert.Equal(100, controller.Read().Pl1);
        Assert.False(recovery.Snapshot.Pending);
    }

    private sealed class Telemetry : IHardwareTelemetry
    {
        public bool Fail;
        public int FailAfterRead = int.MaxValue;
        private int _reads;
        public HardwareSnapshot ReadSnapshot(string? name = null) => Fail || ++_reads > FailAfterRead
            ? new() { TelemetryValid = false } : new() { CpuTemperatureC = 50 };
    }
    private sealed class Controller : ICpuPowerController
    {
        public string ProcessorName => "Intel fixture";
        private ulong _raw = 100UL | (150UL << 32);
        public int Writes;
        public Action? BeforeWrite;
        public CpuPowerState Read() => new(ProcessorName, "fixture-cpu", _raw, 1, 250, 90);
        public void WriteVerified(ulong value) { BeforeWrite?.Invoke(); _raw = value; Writes++; }
    }
    private sealed class MemoryRecovery : IHardwareRecoveryStore
    {
        public string BootId => "fixture-boot";
        public HardwareChangeSnapshot? Snapshot;
        public IReadOnlyList<HardwareChangeSnapshot> PendingSnapshots(string kind) => Snapshot?.Pending == true ? new[] { Snapshot } : Array.Empty<HardwareChangeSnapshot>();
        public HardwareChangeSnapshot? Load(string kind, string id) => Snapshot;
        public void Save(HardwareChangeSnapshot value) => Snapshot = value;
    }
}
