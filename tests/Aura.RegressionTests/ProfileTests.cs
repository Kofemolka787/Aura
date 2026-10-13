using ColorForge.Models;
using ColorForge.Services;
using Xunit;

public sealed class ProfileTests
{
    [Fact]
    public void GameSwitchAndExitRestoreExactEditedManualProfile()
    {
        AppSettings settings = new();
        settings.Current.Gamma = 173; settings.Current.Temperature = 4100; settings.Current.Shadows = 27;
        GameProfileController profiles = new(settings);
        settings.Current = profiles.Activate("display", new() { ProcessName = "cs2", ProfileName = "AURA · CS2" });
        Assert.Equal(105, settings.Current.Gamma);
        settings.Current = profiles.Activate("display", new() { ProcessName = "dota2", ProfileName = "AURA · Dota 2" });
        settings.Current = profiles.Activate("display", null);
        Assert.Equal(173, settings.Current.Gamma);
        Assert.Equal(4100, settings.Current.Temperature);
        Assert.Equal(27, settings.Current.Shadows);
    }

    [Fact]
    public void ManualEditDuringGameBecomesNewManualBaseline()
    {
        AppSettings settings = new(); GameProfileController profiles = new(settings);
        settings.Current = profiles.Activate("display", new() { ProcessName = "cs2", ProfileName = "AURA · CS2" });
        settings.Current.Gamma = 155;
        profiles.ChooseManual("display", settings.Current);
        Assert.Equal(155, profiles.Activate("display", null).Gamma);
    }

    [Fact]
    public void ProfileRenameCascadesAndDeletionPreservesManualColor()
    {
        AppSettings settings = new(); ProfileCatalogService catalog = new(settings);
        DisplayProfile custom = catalog.Create("Mine", new() { Gamma = 140 });
        settings.Games.Add(new() { ProcessName = "another", ProfileName = "Mine" });
        settings.ManualMonitorProfiles["display"] = custom.Clone();
        catalog.Rename(custom, "Renamed");
        Assert.Equal("Renamed", settings.Games.Last().ProfileName);
        catalog.Delete(custom);
        Assert.Equal("Natural", settings.Games.Last().ProfileName);
        Assert.Equal(140, settings.ManualMonitorProfiles["display"].Gamma);
        Assert.Throws<InvalidOperationException>(() => catalog.Delete(settings.Profiles.First()));
    }

    [Fact]
    public void SaveDoesNotReplaceCollectionsBoundToUi()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aura-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppSettings settings = new(); var games = settings.Games; var profiles = settings.Profiles;
            SettingsStore store = new(directory); store.Save(settings);
            Assert.Same(games, settings.Games); Assert.Same(profiles, settings.Profiles);
            settings.Games.Clear(); store.Save(settings); Assert.Empty(store.Load().Games);
            File.WriteAllText(Path.Combine(directory, "import.json"), "[{\"Name\":\"Bad\",\"Gamma\":999}]");
            Assert.Throws<InvalidDataException>(() => store.ImportProfiles(Path.Combine(directory, "import.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void DottedProcessNamesAndIndependentStartupFlagSurviveRoundTrip()
    {
        var settings = SettingsStore.Normalize(new() { SchemaVersion = 2, EffectsEnabled = true, ApplyProfileOnStartup = false,
            Games = new() { new() { ProcessName = "game.v2.exe", ProfileName = "Natural" } } });
        Assert.Equal("game.v2", settings.Games[0].ProcessName);
        Assert.True(settings.EffectsEnabled); Assert.False(settings.ApplyProfileOnStartup);
    }

    [Fact]
    public void TCrosshairAndCrossShareCorrectGeometry()
    {
        Assert.Equal(3, CrosshairGeometry.Lines(new() { Shape = CrosshairShape.TShape }).Count);
        Assert.Equal(4, CrosshairGeometry.Lines(new() { Shape = CrosshairShape.Cross }).Count);
        Assert.DoesNotContain(CrosshairGeometry.Lines(new() { Shape = CrosshairShape.TShape }), line => line.Y1 < 0 || line.Y2 < 0);
        Assert.Empty(CrosshairGeometry.Lines(new() { Shape = CrosshairShape.Circle }));
    }
}
