using System;
using System.Linq;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed class GameProfileController
{
    private readonly AppSettings _settings;
    public string? ActiveGame { get; private set; }
    public GameProfileController(AppSettings settings) => _settings = settings;
    public void SaveManual(string monitor, DisplayProfile profile)
    {
        if (string.IsNullOrEmpty(monitor)) return;
        _settings.ManualMonitorProfiles[monitor] = profile.Clone();
        _settings.MonitorProfiles[monitor] = profile.Clone();
    }
    public DisplayProfile Manual(string monitor) =>
        _settings.ManualMonitorProfiles.TryGetValue(monitor, out DisplayProfile? profile) ? profile.Clone()
        : _settings.MonitorProfiles.TryGetValue(monitor, out profile) ? profile.Clone()
        : (_settings.SelectedProfile ?? _settings.Current).Clone();

    public DisplayProfile Activate(string monitor, GameBinding? binding, DisplayProfile? manual = null)
    {
        if (binding == null) { ActiveGame = null; return Manual(monitor); }
        DisplayProfile? profile = _settings.Profiles.FirstOrDefault(x => x.Name.Equals(binding.ProfileName, StringComparison.OrdinalIgnoreCase));
        if (profile == null) { ActiveGame = null; return Manual(monitor); }
        if (ActiveGame == null) SaveManual(monitor, manual ?? _settings.Current);
        ActiveGame = binding.ProcessName;
        return profile.Clone();
    }
    public void ChooseManual(string monitor, DisplayProfile profile)
    {
        ActiveGame = null;
        SaveManual(monitor, profile);
    }
}
