using ColorForge.Models;
using ColorForge.Services;
using Xunit;

public sealed class GpuSafetyTests
{
    [Fact] public void OriginalFractionalNegativeOffsetIsRestoredExactlyAndApplyIsIdempotent()
    {
        var controller = new Controller(); var recovery = new MemoryRecovery(); var telemetry = new Telemetry();
        controller.BeforeWrite = () => Assert.NotNull(recovery.Snapshot);
        var profile = new OverclockPreferences { GpuDeviceId = "gpu-fixture", LearnedGpuCoreOffsetKhz = 102450, LearnedGpuCoreOffsetMhz = 102 };
        new NvidiaOverclockService(telemetry, controller, new Stress(), recovery).ApplySavedProfile(profile);
        new NvidiaOverclockService(telemetry, controller, new Stress(), recovery).ApplySavedProfile(profile);
        Assert.Equal(102450, controller.Offset);
        telemetry.Fail = true;
        new NvidiaOverclockService(telemetry, controller, new Stress(), recovery).Reset();
        Assert.Equal(-12550, controller.Offset);
    }
    [Fact] public void MissingSensorAndWrongDevicePreventWrites()
    {
        var controller = new Controller(); var telemetry = new Telemetry { Fail = true };
        var service = new NvidiaOverclockService(telemetry, controller, new Stress(), new MemoryRecovery());
        Assert.Throws<InvalidOperationException>(() => service.ApplySavedProfile(new() { GpuDeviceId = "gpu-fixture", LearnedGpuCoreOffsetKhz = 15000 }));
        telemetry.Fail = false;
        Assert.Throws<InvalidOperationException>(() => service.ApplySavedProfile(new() { GpuDeviceId = "other-gpu", LearnedGpuCoreOffsetKhz = 15000 }));
        Assert.Empty(controller.Writes);
    }
    [Fact] public async Task CancellationAfterCandidateWriteRollsBackBeforeTaskCompletes()
    {
        var controller = new Controller(); var recovery = new MemoryRecovery(); var stop = new CancellationTokenSource();
        var stress = new Stress { AtCall = call => { if (call == 2) { stop.Cancel(); throw new OperationCanceledException(stop.Token); } } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NvidiaOverclockService(new Telemetry(), controller, stress, recovery).TuneAsync(null, stop.Token));
        Assert.Contains(2450, controller.Writes);
        Assert.Equal(-12550, controller.Offset);
        Assert.False(recovery.Snapshot!.Pending);
        stop.Dispose();
    }
    [Fact] public async Task FailedRollbackRetainsPendingSnapshotAndBothErrors()
    {
        var controller = new Controller(); var recovery = new MemoryRecovery();
        var stress = new Stress { AtCall = call => { if (call == 2) { controller.FailRestore = true; throw new InvalidOperationException("fixture device removed"); } } };
        var error = await Assert.ThrowsAsync<AggregateException>(() => new NvidiaOverclockService(new Telemetry(), controller, stress, recovery).TuneAsync(null, CancellationToken.None));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.True(recovery.Snapshot!.Pending);
    }
    [Fact] public async Task UnstableFinalConfirmationReturnsExactOriginalAndNoFpsClaim()
    {
        var controller = new Controller { Maximum = 30000 }; var recovery = new MemoryRecovery();
        var stress = new Stress { StableAtCall = call => call < 4 };
        var result = await new NvidiaOverclockService(new Telemetry(), controller, stress, recovery).TuneAsync(null, CancellationToken.None);
        Assert.Equal(-12550, controller.Offset);
        Assert.Equal(-12550, result.GpuCoreOffsetKhz);
        Assert.Equal(0, result.ImprovementPercent);
        Assert.False(recovery.Snapshot!.Pending);
    }
    [Fact] public void RecoveryUsesSnapshotDeviceWhenDefaultAdapterHasChanged()
    {
        var controller = new Controller { DefaultOtherGpu = true };
        var recovery = new MemoryRecovery { Snapshot = new() { Kind = "gpu-khz", DeviceId = "gpu-fixture", BootId = "fixture-boot",
            OriginalValue = unchecked((uint)-12550), TargetValue = 2450, Pending = true } };
        Assert.True(new NvidiaOverclockService(new Telemetry(), controller, new Stress(), recovery).RecoverPendingChanges());
        Assert.All(controller.ReadRequests, x => Assert.Equal("gpu-fixture", x));
        Assert.Equal(-12550, controller.Offset);
        Assert.False(recovery.Snapshot!.Pending);
    }
    private sealed class Telemetry : IHardwareTelemetry
    {
        public bool Fail;
        public HardwareSnapshot ReadSnapshot(string? name = null) => new() { TelemetryValid = !Fail, GpuTemperatureC = Fail ? null : 50 };
    }
    private sealed class Controller : IGpuClockController
    {
        public int Offset = -12550, Maximum = 300000;
        public List<int> Writes = new(); public Action? BeforeWrite; public bool FailRestore, DefaultOtherGpu;
        public List<string?> ReadRequests = new();
        public GpuClockState Read(string? expectedDeviceId = null) {
            ReadRequests.Add(expectedDeviceId);
            return new(new(DefaultOtherGpu && expectedDeviceId == null ? "other-gpu" : "gpu-fixture", "fixture", 123, "fixture-sensor"), Offset, -500000, Maximum);
        }
        public void WriteVerified(string id, int value) { BeforeWrite?.Invoke(); if (FailRestore && value == -12550) throw new InvalidOperationException("fixture restore error"); Offset = value; Writes.Add(value); }
    }
    private sealed class Stress : IGpuStressRunner
    {
        public Action<int>? AtCall; public Func<int, bool>? StableAtCall; private int _calls;
        public Task<GpuStressResult> RunAsync(int seconds, GpuTarget target, Action<HardwareSnapshot, int>? sample, CancellationToken token)
        { int call = ++_calls; AtCall?.Invoke(call); return Task.FromResult(new GpuStressResult(StableAtCall?.Invoke(call) ?? true, 60, 90, TimeSpan.FromSeconds(seconds))); }
    }
    private sealed class MemoryRecovery : IHardwareRecoveryStore
    {
        public string BootId => "fixture-boot"; public HardwareChangeSnapshot? Snapshot;
        public IReadOnlyList<HardwareChangeSnapshot> PendingSnapshots(string kind) => Snapshot?.Pending == true ? new[] { Snapshot } : Array.Empty<HardwareChangeSnapshot>();
        public HardwareChangeSnapshot? Load(string kind, string id) => Snapshot;
        public void Save(HardwareChangeSnapshot value) => Snapshot = System.Text.Json.JsonSerializer.Deserialize<HardwareChangeSnapshot>(System.Text.Json.JsonSerializer.Serialize(value));
    }
}
