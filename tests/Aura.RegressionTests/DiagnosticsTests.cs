using System.Globalization;
using ColorForge.Services;
using ColorForge.Models;
using Xunit;

public sealed class DiagnosticsTests
{
    [Fact]
    public void SwapchainsAreSeparateAndLongStallsRemainInStatistics()
    {
        using Csv csv = new("MsBetweenPresents,SwapChainAddress,ProcessID\n"
            + string.Concat(Enumerable.Repeat("10,0x1,42\n", 100))
            + "6000,0x1,42\n" + string.Concat(Enumerable.Repeat("30,0x2,42\n", 50)));
        var primary = BenchmarkService.Parse(csv.Path, "game.exe");
        Assert.Equal(101, primary.FrameCount);
        Assert.Equal(1, primary.StutterCount);
        Assert.Equal(7, primary.DurationSeconds);
        Assert.Equal(2, primary.Streams.Count);
        var other = BenchmarkService.Parse(csv.Path, "game.exe", "42 / 0x2");
        Assert.Equal(50, other.FrameCount);
        Assert.Equal(1000d / 30, other.AverageFps, 8);
        Assert.Equal(1.5, other.DurationSeconds);
    }

    [Fact]
    public void ModernCsvUsesApplicationTimingAndTracksDroppedGeneratedFrames()
    {
        string header = "Application,ProcessID,SwapChainAddress,CPUStartTime,MsCPUBusy,MsCPUWait,DisplayedTime,FrameType\n";
        string data = string.Concat(Enumerable.Range(0, 100).Select(i => $"game.exe,42,0x1,{i * 10},4,6,10,Application\n"));
        data += "game.exe,42,0x1,1000,4,6,NA,Application\n";
        data += "game.exe,42,0x1,1010,4,6,10,Interpolated\n";
        data += "other.exe,24,0x2,0,1,1,2,Application\n";
        using Csv csv = new(header + data);
        var result = BenchmarkService.Parse(csv.Path, "game.exe");
        Assert.Equal(101, result.FrameCount);
        Assert.Equal(100, result.AverageFps);
        Assert.Equal(1, result.DroppedFrameCount);
        Assert.Equal(1, result.GeneratedFrameCount);
        Assert.Equal("MsCPUBusy+MsCPUWait", result.Metric);
        Assert.Equal(1.01, result.DurationSeconds, 8);
    }

    [Fact]
    public void PercentilesAndLowFpsUseRealWorstTail()
    {
        using Csv csv = new("MsBetweenPresents\n" + string.Concat(Enumerable.Repeat("10\n", 98)) + "20\n100\n");
        var result = BenchmarkService.Parse(csv.Path, "game.exe");
        Assert.Equal(10, result.OnePercentLowFps);
        Assert.Equal(10, result.P95FrameTimeMs);
        Assert.Equal(20.8, result.P99FrameTimeMs, 6);
        Assert.Equal(1, result.StutterCount);
    }

    [Fact]
    public void DisplayedTimeAloneDoesNotMasqueradeAsApplicationFps()
    {
        using Csv csv = new("DisplayedTime\n" + string.Concat(Enumerable.Repeat("10\n", 100)));
        Assert.Throws<InvalidDataException>(() => BenchmarkService.Parse(csv.Path, "game.exe"));
    }

    [Fact]
    public async Task EventReadFailureIsNotAHealthySystemResult()
    {
        var result = await new StutterDoctorService(new FakeRunner(new(5, "", "Access denied"))).ScanAsync(null, CancellationToken.None);
        Assert.Contains(result, x => x.Title == "Журнал не проверен" && x.Severity == AuditSeverity.Caution);
        Assert.DoesNotContain(result, x => x.Title == "Системный журнал" && x.Severity == AuditSeverity.Good);
    }

    [Fact]
    public async Task EventProviderIsRequiredForHardwareClassification()
    {
        string xml = Event("UnrelatedProvider", 17) + Event("Microsoft-Windows-WHEA-Logger", 19);
        var result = await new StutterDoctorService(new FakeRunner(new(0, xml, ""))).ScanAsync(null, CancellationToken.None);
        Assert.DoesNotContain(result, x => x.Title == "Исправленная ошибка PCIe");
        Assert.Contains(result, x => x.Title == "Исправленная ошибка WHEA");
    }

    [Fact]
    public async Task MalformedXmlAndCancellationAreDistinct()
    {
        var malformed = await new StutterDoctorService(new FakeRunner(new(0, "<broken", ""))).ScanAsync(null, CancellationToken.None);
        Assert.Contains(malformed, x => x.Title == "Журнал не проверен");
        using CancellationTokenSource stop = new(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StutterDoctorService(new FakeRunner(new(0, "", ""))).ScanAsync(null, stop.Token));
    }

    [Fact]
    public async Task BothPipesDrainWithoutDeadlockAndArgumentsAreLiteral()
    {
        string fixture = ToolFixture();
        ToolResult burst = await new ExternalToolRunner().RunAsync("dotnet", new[] { fixture, "burst" }, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(0, burst.ExitCode);
        Assert.True(burst.Output.Length > 2 * 1024 * 1024);
        Assert.True(burst.Error.Length > 2 * 1024 * 1024);
        string literal = "a spaced argument \"with quotes\" & symbols";
        ToolResult echo = await new ExternalToolRunner().RunAsync("dotnet", new[] { fixture, "echo", literal }, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(literal, echo.Output);
    }

    [Fact]
    public async Task TimedOutAndCancelledChildrenAreTerminated()
    {
        var runner = new ExternalToolRunner();
        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync("dotnet", new[] { ToolFixture(), "wait" }, TimeSpan.FromMilliseconds(250), CancellationToken.None));
        using CancellationTokenSource stop = new(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("dotnet", new[] { ToolFixture(), "wait" }, TimeSpan.FromMinutes(1), stop.Token));
    }

    [Fact]
    public async Task RepeatedProviderEventsAreGroupedWithoutLosingCounts()
    {
        string xml = string.Concat(Enumerable.Repeat(Event("Microsoft-Windows-WHEA-Logger", 19), 1000));
        var result = await new StutterDoctorService(new FakeRunner(new(0, xml, ""))).ScanAsync(null, CancellationToken.None);
        var group = Assert.Single(result, x => x.Title == "Исправленная ошибка WHEA");
        Assert.Equal(1000, group.Occurrences);
        Assert.Equal("Microsoft-Windows-WHEA-Logger / 19", group.Source);
    }

    [Fact]
    public async Task NonzeroPresentMonExitCleansOnlyItsOwnedSession()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aura-session-" + Guid.NewGuid().ToString("N"));
        var runner = new RecordingRunner();
        try {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new BenchmarkService(runner, directory, () => "fixture-tool")
                .RunAsync("game.exe", 10, null, CancellationToken.None));
            Assert.Equal(2, runner.Calls.Count);
            string[] first = runner.Calls[0], cleanup = runner.Calls[1];
            string session = first[Array.IndexOf(first, "--session_name") + 1];
            Assert.StartsWith("AURA-", session);
            Assert.Equal(new[] { "--session_name", session, "--terminate_existing_session" }, cleanup);
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class RecordingRunner : IExternalToolRunner
    {
        public List<string[]> Calls = new();
        public Task<ToolResult> RunAsync(string e, string[] a, TimeSpan timeout, CancellationToken t, string? d = null)
        { Calls.Add(a); return Task.FromResult(new ToolResult(Calls.Count == 1 ? 5 : 0, "", "fixture access denied")); }
    }

    private static string Event(string provider, int id) =>
        $"<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><Provider Name=\"{provider}\"/><EventID>{id}</EventID><TimeCreated SystemTime=\"2026-10-13T00:00:00Z\"/></System></Event>";
    private static string ToolFixture()
    {
        string current = AppContext.BaseDirectory;
        // Both standard bin output and --artifacts-path output are supported.
        string artifacts = Path.GetFullPath(Path.Combine(current, "..", "..", "Aura.ToolFixture", "release", "Aura.ToolFixture.dll"));
        if (File.Exists(artifacts)) return artifacts;
        string standard = Path.GetFullPath(Path.Combine(current, "..", "..", "..", "..", "Aura.ToolFixture", "bin", "Release", "net8.0", "Aura.ToolFixture.dll"));
        if (File.Exists(standard)) return standard;
        throw new FileNotFoundException("Build the ToolFixture project before the process lifecycle tests.");
    }
    private sealed class FakeRunner(ToolResult result) : IExternalToolRunner
    {
        public Task<ToolResult> RunAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken token, string? directory = null)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(result); }
    }
    private sealed class Csv : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        public Csv(string contents) => File.WriteAllText(Path, contents);
        public void Dispose() => File.Delete(Path);
    }
}
