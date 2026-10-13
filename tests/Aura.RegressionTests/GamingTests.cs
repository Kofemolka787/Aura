using System.Text;
using ColorForge.Models;
using ColorForge.Services;
using Xunit;

public sealed class GamingTests
{
    [Fact]
    public void GameOverridesAndHotkeyTogglesDoNotReplaceCommonSettingsOrSavedPresets()
    {
        AppSettings settings = new() { Crosshair = new() { Enabled = false, Size = 17 }, Overlay = new() { Enabled = false, Scale = 1.4 } };
        GameBinding game = new() { UseCrosshair = true, Crosshair = new() { Enabled = true, Size = 8 }, UseOverlay = true, Overlay = new() { Enabled = true, Scale = 0.8 } };
        GamePresentationSession session = new(settings);
        session.Activate(game);
        session.Crosshair.Enabled = false; session.Crosshair.Size = 25; session.Overlay.Enabled = false;
        Assert.True(game.Crosshair.Enabled); Assert.Equal(8, game.Crosshair.Size); Assert.True(game.Overlay.Enabled);
        Assert.Equal(17, settings.Crosshair.Size); Assert.Equal(1.4, settings.Overlay.Scale);
        session.Activate(null);
        Assert.Same(settings.Crosshair, session.Crosshair); Assert.Same(settings.Overlay, session.Overlay);
        Assert.False(session.Crosshair.Enabled); Assert.Equal(17, session.Crosshair.Size);
    }

    [Fact]
    public void FocusAndExitPoliciesHonorEnabledStateAndNewForegroundGames()
    {
        GameBinding held = new() { ProcessName = "held", RestoreOnFocusLoss = false };
        GameBinding normal = new() { ProcessName = "normal", RestoreOnFocusLoss = true };
        GameBinding[] games = { held, normal };
        Assert.Null(GameSessionSelector.Select(games, "desktop", _ => true, "normal"));
        Assert.Equal("held", GameSessionSelector.Select(games, "desktop", _ => true, "held")?.ProcessName);
        Assert.Null(GameSessionSelector.Select(games, "desktop", _ => false, "held"));
        Assert.Equal("normal", GameSessionSelector.Select(games, "NORMAL", _ => true, "held")?.ProcessName);
        held.Enabled = false;
        Assert.Null(GameSessionSelector.Select(games, "held", _ => true, "held"));
    }

    [Fact]
    public void ForegroundBindingsAreDeepSnapshots()
    {
        GameBinding game = new() { ProcessName = "game", Crosshair = new() { Size = 18 }, Overlay = new() { Scale = 1.2 } };
        var snapshot = GameSessionSelector.Select(new[] { game }, "game", _ => true, null)!;
        game.Crosshair.Size = 32; game.Overlay.Scale = 2;
        Assert.Equal(18, snapshot.Crosshair.Size); Assert.Equal(1.2, snapshot.Overlay.Scale);
    }

    [Fact]
    public void DifferentMonitorRestoresItsOwnManualColorInsteadOfSelectedMonitorColor()
    {
        AppSettings settings = new() { Current = new() { Gamma = 150 } };
        GameProfileController profiles = new(settings);
        profiles.SaveManual("other", new() { Gamma = 175, Temperature = 4400 });
        var color = profiles.Activate("other", new() { ProfileName = "AURA · CS2" }, profiles.Manual("other"));
        Assert.Equal(105, color.Gamma);
        var restored = profiles.Activate("other", null);
        Assert.Equal(175, restored.Gamma); Assert.Equal(4400, restored.Temperature);
        Assert.Equal(150, settings.Current.Gamma);
    }

    [Fact]
    public void NewSettingsRoundTripAndLegacyColorBindingsKeepTheirBehavior()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            SettingsStore store = new(directory);
            AppSettings old = SettingsStore.Normalize(new() { SchemaVersion = 2, ApplyProfileOnStartup = false,
                Games = new() { new() { ProcessName = "game.v2.exe", ProfileName = "Natural" } } });
            Assert.Equal(3, old.SchemaVersion); Assert.True(old.Games[0].ApplyColor);
            Assert.False(old.Games[0].UseCrosshair); Assert.False(old.Games[0].UseOverlay); Assert.True(old.Games[0].RestoreOnFocusLoss);
            old.Games[0].UseCrosshair = true; old.Games[0].Crosshair.Size = 31; old.Games[0].RestoreOnFocusLoss = false;
            old.Games[0].MonitorDeviceName = "DISPLAY2"; old.Overlay.Scale = 1.3; old.Hotkeys[0].Alt = true;
            store.Save(old); var saved = store.Load();
            Assert.Equal(31, saved.Games[0].Crosshair.Size); Assert.False(saved.Games[0].RestoreOnFocusLoss);
            Assert.Equal("DISPLAY2", saved.Games[0].MonitorDeviceName); Assert.Equal(1.3, saved.Overlay.Scale);
            Assert.True(saved.Hotkeys[0].Alt); Assert.False(saved.ApplyProfileOnStartup);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void InvalidGlobalHotkeysAndOverlayNumbersAreNotApplied()
    {
        AppSettings settings = SettingsStore.Normalize(new() { SchemaVersion = 3,
            Overlay = new() { Scale = double.NaN, Opacity = double.PositiveInfinity },
            Hotkeys = new() { new() { Action = HotkeyAction.ToggleOverlay, Key = "F12" },
                new() { Action = HotkeyAction.ToggleCrosshair, Key = "A", Control = false, Shift = false, Alt = false } } });
        Assert.All(settings.Hotkeys, x => Assert.False(x.Enabled));
        Assert.Equal(1, settings.Overlay.Scale); Assert.Equal(0.85, settings.Overlay.Opacity);
        Assert.Equal(0u, HotkeyBinding.VirtualKey("F12")); Assert.Equal(0x7Au, HotkeyBinding.VirtualKey("F11"));
        Assert.Equal((uint)'A', HotkeyBinding.VirtualKey("A"));
        Assert.Equal("Ctrl+Shift+F6", HotkeyBinding.Defaults()[0].Gesture);
    }
    [Fact]
    public void SettingsMigrationPreservesOriginalBytesBeforeFirstWriteOnly()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory); SettingsStore store = new(directory);
            string original = "{\"SchemaVersion\":2,\"Games\":[],\"Crosshair\":{\"Size\":15}}";
            File.WriteAllText(store.SettingsPath, original);
            AppSettings settings = store.Load(); store.Save(settings); store.Save(settings);
            string backup = Assert.Single(Directory.GetFiles(directory, "settings.pre-v0.12-*.json"));
            Assert.Equal(original, File.ReadAllText(backup)); Assert.Empty(store.Load().Games);
            Assert.Equal(15, store.Load().Crosshair.Size);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void NvidiaRawColorStatePreservesIntegerLevelsAcrossSerialization()
    {
        NvidiaColorState state = new(29, 0, 100, 50, false);
        Assert.True(state.IsValid);
        string json = System.Text.Json.JsonSerializer.Serialize(state);
        Assert.Equal(state, System.Text.Json.JsonSerializer.Deserialize<NvidiaColorState>(json));
        Assert.Equal(80, state.WithNormalized(0.59).Current);
        Assert.Equal(20, state.WithNormalized(-0.59).Current);
        Assert.False(new NvidiaColorState(0, 0, 0, 0, true).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.WithNormalized(double.NaN));
    }

    private static BenchmarkContext Context => new() { Scene = "route", GraphicsSettings = "high, 100%", GameVersion = "1.2", DriverVersion = "555",
        CpuName = "CPU", GpuName = "GPU", WindowsVersion = "Windows 11", Width = 1920, Height = 1080, RefreshRate = 144, RequestedDurationSeconds = 30 };
    private static BenchmarkResult Run(string id, int number, double fps, BenchmarkContext? context = null, string metric = "FrameTime") => new()
    {
        ProcessName = "game.exe", Metric = metric, FrameCount = 100, DurationSeconds = 30, AverageFps = fps, OnePercentLowFps = fps * 0.7,
        Run = new() { SeriesId = id, Variant = id, ExpectedRuns = 3, RunNumber = number, Context = context ?? Context }
    };
    [Fact]
    public void SeriesComparisonReportsAllRunRangesWithoutClaimingSmallChangesAsAWin()
    {
        var groups = BenchmarkSeriesService.Group(new[] { Run("before", 1, 95), Run("before", 2, 105), Run("before", 3, 100),
            Run("after", 1, 99), Run("after", 2, 103), Run("after", 3, 101) });
        var before = groups.Single(x => x.Id == "before"); var after = groups.Single(x => x.Id == "after");
        Assert.True(before.IsComplete); Assert.True(before.IsComparable); Assert.Equal(100, before.MeanFps);
        string text = BenchmarkSeriesService.Compare(before, after);
        Assert.Contains("Диапазоны перекрываются", text); Assert.Contains("Все прогоны включены", text);
    }
    [Fact]
    public void PartialAndDuplicateNumberedRunsCannotBecomeACompleteSeries()
    {
        var groups = BenchmarkSeriesService.Group(new[] { Run("partial", 1, 100), Run("partial", 2, 101),
            Run("duplicate", 1, 100), Run("duplicate", 1, 101), Run("duplicate", 3, 102) });
        Assert.All(groups, x => Assert.False(x.IsComplete));
        Assert.Contains("полные серии", BenchmarkSeriesService.Compare(groups[0], groups[1]));
    }
    [Fact]
    public void DifferentDriverVersionOrMetricBlocksSeriesComparison()
    {
        var before = BenchmarkSeriesService.Group(Enumerable.Range(1, 3).Select(n => Run("before", n, 100))).Single();
        var after = BenchmarkSeriesService.Group(Enumerable.Range(1, 3).Select(n => Run("after", n, 200, Context with { DriverVersion = "556" }))).Single();
        Assert.Contains("Условия различаются", BenchmarkSeriesService.Compare(before, after));
        var mixed = BenchmarkSeriesService.Group(new[] { Run("mixed", 1, 100), Run("mixed", 2, 100), Run("mixed", 3, 100, metric: "CPUFrameTime") }).Single();
        Assert.False(mixed.IsComparable);
    }
    [Fact]
    public void SelfComparisonAndUnknownHardwareAreNotComparable()
    {
        var series = BenchmarkSeriesService.Group(Enumerable.Range(1, 3).Select(n => Run("same", n, 100))).Single();
        Assert.Contains("та же серия", BenchmarkSeriesService.Compare(series, series));
        Assert.False((Context with { GpuName = "Видеокарта не определена" }).IsComplete);
        Assert.False((Context with { Scene = " " }).IsComplete);
    }
    [Fact]
    public void MissingRunContextAndShortCapturesCannotProduceAValidSeries()
    {
        var broken = new BenchmarkResult { ProcessName = "game.exe", FrameCount = 100, AverageFps = 100,
            Run = new() { SeriesId = "broken", Context = null! } };
        Assert.Empty(BenchmarkSeriesService.Group(new[] { broken }));
        var shortCapture = Enumerable.Range(1, 3).Select(n => new BenchmarkResult { ProcessName = "game.exe", Metric = "FrameTime",
            FrameCount = 100, AverageFps = 100, OnePercentLowFps = 70, DurationSeconds = 5,
            Run = new() { SeriesId = "short", Variant = "short", ExpectedRuns = 3, RunNumber = n, Context = Context } });
        var series = BenchmarkSeriesService.Group(shortCapture).Single();
        Assert.True(series.IsComplete); Assert.False(series.IsComparable);
    }
    [Fact]
    public void InvalidOrStaleSensorsBecomeMissingValuesAndRemainSerializable()
    {
        var sample = TelemetrySample.FromSnapshot(new() { CpuTemperatureC = double.NaN, CpuLoadPercent = 300,
            GpuTemperatureC = -128, GpuLoadPercent = 42, GpuCoreClockMhz = double.PositiveInfinity });
        Assert.Null(sample.CpuTemperatureC); Assert.Null(sample.CpuLoadPercent); Assert.Null(sample.GpuTemperatureC);
        Assert.Equal(42, sample.GpuLoadPercent); Assert.Null(sample.GpuCoreClockMhz);
        Assert.Contains("null", System.Text.Json.JsonSerializer.Serialize(sample));
        Assert.Null(TelemetrySample.FromSnapshot(new() { TimestampUtc = DateTime.UtcNow.AddSeconds(-5), CpuTemperatureC = 50 }).CpuTemperatureC);
    }
    [Fact]
    public void OverlayExpiresFramesEvenWhenCollectorStopsSendingCallbacks()
    {
        DateTime now = DateTime.UtcNow;
        LiveFrameSnapshot fresh = new(100, 80, new[] { 10d }, "live") { SampledAtUtc = now };
        Assert.Same(fresh, OverlayFreshness.Frames(fresh, now.AddSeconds(1)));
        var stale = OverlayFreshness.Frames(fresh, now.AddSeconds(3));
        Assert.Null(stale.Fps); Assert.Empty(stale.FrameTimesMs);
        var error = new LiveFrameSnapshot(null, null, Array.Empty<double>(), "ETW access denied") { SampledAtUtc = now.AddMinutes(-1) };
        Assert.Equal("ETW access denied", OverlayFreshness.Frames(error, now).Status);
    }
    [Fact]
    public void LiveCsvWaitsForCompleteLinesAndSeparatesGeneratedFramesAndStreams()
    {
        DateTime now = DateTime.UtcNow;
        LiveFrameTail tail = new("game.exe");
        tail.Append("Application,ProcessID,SwapChainAddress,MsCPUBusy,MsCPUWait,FrameType", now);
        Assert.Null(tail.Snapshot(now).Fps);
        tail.Append("\ngame.exe,42,0x1,4,", now);
        Assert.Null(tail.Snapshot(now).Fps);
        tail.Append("6,Application\n" + string.Concat(Enumerable.Repeat("game.exe,42,0x1,4,6,Application\n", 40))
            + string.Concat(Enumerable.Repeat("game.exe,42,0x2,20,20,Application\n", 20))
            + "game.exe,42,0x1,0.1,0.1,Interpolated\nother.exe,42,0x1,1,1,Application\n", now);
        var snapshot = tail.Snapshot(now);
        Assert.Equal(100, snapshot.Fps); Assert.Equal(100, snapshot.LowFps); Assert.Equal(41, snapshot.FrameTimesMs.Length);
        Assert.Null(tail.Snapshot(now.AddSeconds(3)).Fps);
    }
    [Fact]
    public void LiveFramesKeepLongStallsButRejectInvalidNumbersAndUnboundedRows()
    {
        LiveFrameTail tail = new("game.exe"); DateTime now = DateTime.UtcNow;
        tail.Append("FrameTime\n" + string.Concat(Enumerable.Repeat("10\n", 250)) + "NaN\n-1\n6000\n", now);
        Assert.Contains(6000, tail.Snapshot(now).FrameTimesMs);
        Assert.True(tail.Snapshot(now).Fps < 1);
        Assert.Throws<InvalidDataException>(() => tail.Append(new string('x', 65537), now));
        LiveFrameTail displayedOnly = new("game.exe");
        Assert.Throws<InvalidDataException>(() => displayedOnly.Append("DisplayedTime\n", now));
    }
    [Fact]
    public void GrowingUtf8CsvPreservesPartialByteSequencesAndPartialRows()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] data = Encoding.UTF8.GetBytes("Application,FrameTime\nигра.exe,10\n");
            int cut = Array.IndexOf(data, (byte)0xD0) + 1;
            File.WriteAllBytes(path, data[..cut]);
            using LiveCsvReader reader = new(path, "игра.exe");
            Assert.Null(reader.Read(DateTime.UtcNow).Fps);
            using (FileStream append = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) append.Write(data[cut..]);
            Assert.Equal(100, reader.Read(DateTime.UtcNow).Fps);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task LiveCaptureCancellationCleansOnlyItsOwnedSessionAndTemporaryCsv()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aura-live-" + Guid.NewGuid().ToString("N"));
        using CancellationTokenSource stop = new();
        OwnedRunner runner = new(stop);
        try
        {
            var service = new LiveFrameCaptureService(runner, () => "fixture", directory);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync("game.exe", new Progress<LiveFrameSnapshot>(), stop.Token));
            Assert.Equal(2, runner.Calls.Count);
            string[] first = runner.Calls[0]; string session = first[Array.IndexOf(first, "--session_name") + 1];
            Assert.StartsWith("AURA-Live-", session);
            Assert.Equal(new[] { "--session_name", session, "--terminate_existing_session" }, runner.Calls[1]);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class OwnedRunner(CancellationTokenSource stop) : IExternalToolRunner
    {
        public List<string[]> Calls { get; } = new();
        public async Task<ToolResult> RunAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken token, string? directory = null)
        {
            Calls.Add(arguments);
            if (Calls.Count == 1)
            {
                string path = arguments[Array.IndexOf(arguments, "--output_file") + 1];
                File.WriteAllText(path, "FrameTime\n10\n"); stop.CancelAfter(40);
                await Task.Delay(Timeout.Infinite, token);
            }
            return new(0, "", "");
        }
    }
}
