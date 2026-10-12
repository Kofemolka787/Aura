using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed class SettingsStore
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	public string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AURA");

	public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

	public AppSettings Load()
	{
		try
		{
			if (File.Exists(SettingsPath))
			{
				AppSettings appSettings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
				if (appSettings != null)
				{
					AppSettings appSettings2 = appSettings;
					if (appSettings2.Profiles == null)
					{
						List<DisplayProfile> list = (appSettings2.Profiles = new List<DisplayProfile>());
					}
					foreach (DisplayProfile builtIn in Defaults.CreateProfiles())
					{
						if (!appSettings.Profiles.Any((DisplayProfile x) => x.Name.Equals(builtIn.Name, StringComparison.OrdinalIgnoreCase)))
						{
							appSettings.Profiles.Add(builtIn);
						}
					}
					appSettings2 = appSettings;
					if (appSettings2.Games == null)
					{
						List<GameBinding> list3 = (appSettings2.Games = new List<GameBinding>());
					}
					foreach (GameBinding builtIn2 in Defaults.CreateGames())
					{
						if (!appSettings.Games.Any((GameBinding x) => x.ProcessName.Equals(builtIn2.ProcessName, StringComparison.OrdinalIgnoreCase)))
						{
							appSettings.Games.Add(builtIn2);
						}
					}
					appSettings2 = appSettings;
					if (appSettings2.Current == null)
					{
						DisplayProfile displayProfile = (appSettings2.Current = new DisplayProfile());
					}
					appSettings2 = appSettings;
					if (appSettings2.Crosshair == null)
					{
						CrosshairSettings crosshairSettings = (appSettings2.Crosshair = new CrosshairSettings());
					}
					appSettings2 = appSettings;
					if (appSettings2.Tweaks == null)
					{
						appSettings2.Tweaks = new WindowsTweakPreferences();
					}
					WindowsTweaksService.InitializePreferences(appSettings2.Tweaks, migrateLegacy: true);
					appSettings2 = appSettings;
					if (appSettings2.Overclock == null)
					{
						appSettings2.Overclock = new OverclockPreferences();
					}
					appSettings2 = appSettings;
					if (appSettings2.MonitorProfiles == null)
					{
						Dictionary<string, DisplayProfile> dictionary = (appSettings2.MonitorProfiles = new Dictionary<string, DisplayProfile>());
					}
					return appSettings;
				}
			}
		}
		catch (Exception exception)
		{
			AppLogService.Error("Settings could not be loaded; safe defaults will be used.", exception);
			BackupUnreadableSettings();
		}
		AppSettings defaults = new AppSettings();
		WindowsTweaksService.InitializePreferences(defaults.Tweaks, migrateLegacy: false);
		return defaults;
	}

	public void Save(AppSettings settings)
	{
		Directory.CreateDirectory(DirectoryPath);
		string text = SettingsPath + ".tmp";
		try
		{
			File.WriteAllText(text, JsonSerializer.Serialize(settings, JsonOptions));
			File.Move(text, SettingsPath, overwrite: true);
		}
		finally
		{
			try { File.Delete(text); } catch { }
		}
	}

	private void BackupUnreadableSettings()
	{
		try
		{
			if (!File.Exists(SettingsPath))
				return;
			string backup = Path.Combine(DirectoryPath, $"settings.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
			File.Copy(SettingsPath, backup, overwrite: false);
			AppLogService.Warning("Unreadable settings were preserved at " + backup);
		}
		catch
		{
		}
	}

	public void ExportProfiles(string path, IEnumerable<DisplayProfile> profiles)
	{
		File.WriteAllText(path, JsonSerializer.Serialize(profiles, JsonOptions));
	}

	public List<DisplayProfile> ImportProfiles(string path)
	{
		return JsonSerializer.Deserialize<List<DisplayProfile>>(File.ReadAllText(path), JsonOptions) ?? new List<DisplayProfile>();
	}
}
