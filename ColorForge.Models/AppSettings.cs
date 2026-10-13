using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ColorForge.Models;

public sealed class AppSettings
{
    public OverlaySettings Overlay { get; set; } = new();
    public List<HotkeyBinding> Hotkeys { get; set; } = HotkeyBinding.Defaults();
    public int SchemaVersion { get; set; }
    public bool ApplyProfileOnStartup { get; set; } = true;
    public Dictionary<string, DisplayProfile> ManualMonitorProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	public bool EffectsEnabled { get; set; } = true;

	public bool AutoSwitchGames { get; set; } = true;

	public bool StartWithWindows { get; set; }

	public bool MinimizeToTray { get; set; } = true;

	public bool StretchScaling { get; set; } = true;

	public string SelectedProfileName { get; set; } = "AURA · Зима";

	public string SelectedMonitorDeviceName { get; set; } = string.Empty;

	public DisplayProfile Current { get; set; } = new DisplayProfile
	{
		Name = "AURA · Зима",
		Saturation = 145,
		Vibrance = 70,
		Brightness = -3,
		Contrast = 112,
		Gamma = 100,
		Temperature = 5800,
		Shadows = 12
	};

	public List<DisplayProfile> Profiles { get; set; } = Defaults.CreateProfiles();

	public List<GameBinding> Games { get; set; } = Defaults.CreateGames();

	public CrosshairSettings Crosshair { get; set; } = new CrosshairSettings();

	public WindowsTweakPreferences Tweaks { get; set; } = new WindowsTweakPreferences();

	public OverclockPreferences Overclock { get; set; } = new OverclockPreferences();

	public Dictionary<string, DisplayProfile> MonitorProfiles { get; set; } = new Dictionary<string, DisplayProfile>();

	[JsonIgnore]
	public DisplayProfile? SelectedProfile => Profiles.FirstOrDefault((DisplayProfile p) => p.Name == SelectedProfileName);
}
