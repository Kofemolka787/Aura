using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    public string DirectoryPath { get; }
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public SettingsStore(string? directory = null) =>
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AURA");

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    if (new FileInfo(SettingsPath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Файл настроек превышает 4 МиБ.");
                    AppSettings settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options)
                        ?? throw new InvalidDataException("Пустой объект настроек.");
                    return Normalize(settings);
                }
            }
            catch (Exception exception)
            {
                AppLogService.Error("Settings could not be loaded; preserved source and safe defaults are used.", exception);
                if (File.Exists(SettingsPath))
                {
                    try { File.Copy(SettingsPath, Path.Combine(DirectoryPath, $"settings.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json")); }
                    catch (Exception backupError) { AppLogService.Error("Could not preserve unreadable settings.", backupError); }
                }
            }
            AppSettings defaults = new();
            WindowsTweaksService.InitializePreferences(defaults.Tweaks, false);
            return Normalize(defaults);
        }
    }

    public static DisplayProfile NormalizeProfile(DisplayProfile profile)
    {
        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Профиль" : profile.Name.Trim()[..Math.Min(64, profile.Name.Trim().Length)];
        profile.Saturation = Math.Clamp(profile.Saturation, 0, 220);
        profile.Vibrance = Math.Clamp(profile.Vibrance, 0, 100);
        profile.Brightness = Math.Clamp(profile.Brightness, -50, 50);
        profile.Contrast = Math.Clamp(profile.Contrast, 50, 150);
        profile.Gamma = Math.Clamp(profile.Gamma, 50, 200);
        profile.Temperature = Math.Clamp(profile.Temperature, 3000, 10000);
        profile.Shadows = Math.Clamp(profile.Shadows, 0, 100);
        return profile;
    }

    public static AppSettings Normalize(AppSettings settings)
    {
        if (settings.SchemaVersion < 2)
        {
            settings.ApplyProfileOnStartup = settings.EffectsEnabled;
            // Legacy learned profiles are not tied to verified hardware.
            if (settings.Overclock != null)
            {
                settings.Overclock.ApplyCpuProfileOnStart = false;
                settings.Overclock.ApplyLearnedGpuOnStart = false;
            }
        }
        settings.SchemaVersion = 3;
        settings.Profiles = (settings.Profiles ?? new()).Where(p => p != null)
            .Select(NormalizeProfile).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        foreach (DisplayProfile profile in Defaults.CreateProfiles())
            if (!settings.Profiles.Any(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
                settings.Profiles.Add(profile);
        settings.Current = NormalizeProfile(settings.Current ?? new());
        if (!settings.Profiles.Any(p => p.Name.Equals(settings.SelectedProfileName, StringComparison.OrdinalIgnoreCase)))
            settings.SelectedProfileName = settings.Current.Name;
        settings.Games = (settings.Games ?? new()).Where(g => g != null && !string.IsNullOrWhiteSpace(g.ProcessName))
            .Select(g => { g.ProcessName = Path.GetFileName(g.ProcessName.Trim()); if (g.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) g.ProcessName = g.ProcessName[..^4]; return g; })
            .Where(g => g.ProcessName.Length <= 128 && g.ProcessName.IndexOfAny(new[] { '\\', '/', '"', '\r', '\n' }) < 0)
            .GroupBy(g => g.ProcessName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        foreach (GameBinding game in settings.Games)
        {
            NormalizeGame(game);
            if (!settings.Profiles.Any(p => p.Name.Equals(game.ProfileName, StringComparison.OrdinalIgnoreCase)))
                game.ProfileName = settings.SelectedProfileName;
        }
        settings.MonitorProfiles = (settings.MonitorProfiles ?? new()).Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value != null)
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Key, p => NormalizeProfile(p.First().Value), StringComparer.OrdinalIgnoreCase);
        settings.ManualMonitorProfiles = (settings.ManualMonitorProfiles ?? new()).Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value != null)
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Key, p => NormalizeProfile(p.First().Value), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.MonitorProfiles)
            if (!settings.ManualMonitorProfiles.ContainsKey(pair.Key)) settings.ManualMonitorProfiles[pair.Key] = pair.Value.Clone();
        settings.Crosshair = NormalizeCrosshair(settings.Crosshair ?? new());
        settings.Overlay = NormalizeOverlay(settings.Overlay ?? new());
        settings.Hotkeys = (settings.Hotkeys ?? HotkeyBinding.Defaults()).Where(x => x != null && Enum.IsDefined(x.Action))
            .GroupBy(x => x.Action).Select(x => x.First()).ToList();
        foreach (HotkeyBinding hotkey in settings.Hotkeys)
        {
            hotkey.Key = (hotkey.Key ?? "").Trim().ToUpperInvariant();
            if (HotkeyBinding.VirtualKey(hotkey.Key) == 0 || hotkey.Modifiers == 0) hotkey.Enabled = false;
        }
        settings.Tweaks ??= new();
        WindowsTweaksService.InitializePreferences(settings.Tweaks, true);
        settings.Tweaks.EnabledTweakIds = (settings.Tweaks.EnabledTweakIds ?? new()).Where(id => id != null)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.Overclock ??= new();
        settings.Overclock.CpuProfileId ??= "stock";
        settings.Overclock.CpuDeviceId ??= string.Empty;
        settings.Overclock.GpuDeviceId ??= string.Empty;
        settings.Overclock.CpuBackendId ??= "none";
        if (!double.IsFinite(settings.Overclock.CpuPl1Watts) || !double.IsFinite(settings.Overclock.CpuPl2Watts)
            || settings.Overclock.CpuPl1Watts <= 0 || settings.Overclock.CpuPl2Watts < settings.Overclock.CpuPl1Watts
            || settings.Overclock.CpuPl2Watts > 1000 || string.IsNullOrEmpty(settings.Overclock.CpuDeviceId))
            settings.Overclock.ApplyCpuProfileOnStart = false;
        if (string.IsNullOrWhiteSpace(settings.Overclock.GpuDeviceId) || settings.Overclock.LearnedGpuCoreOffsetMhz is < -2000 or > 2000 || settings.Overclock.LearnedGpuCoreOffsetKhz is < -2000000 or > 2000000)
            settings.Overclock.ApplyLearnedGpuOnStart = false;
        settings.SelectedMonitorDeviceName ??= string.Empty;
        return settings;
    }

    public static CrosshairSettings NormalizeCrosshair(CrosshairSettings value)
    {
        value.Size = Math.Clamp(value.Size, 1, 40); value.Gap = Math.Clamp(value.Gap, 0, 40);
        value.Thickness = Math.Clamp(value.Thickness, 1, 10); value.Outline = Math.Clamp(value.Outline, 0, 10);
        if (!Enum.IsDefined(value.Shape)) value.Shape = CrosshairShape.Cross;
        return value;
    }
    public static OverlaySettings NormalizeOverlay(OverlaySettings value)
    {
        value.Scale = double.IsFinite(value.Scale) ? Math.Clamp(value.Scale, 0.7, 2) : 1;
        value.Opacity = double.IsFinite(value.Opacity) ? Math.Clamp(value.Opacity, 0.25, 1) : 0.85;
        if (!Enum.IsDefined(value.Corner)) value.Corner = OverlayCorner.TopLeft;
        return value;
    }
    public static GameBinding NormalizeGame(GameBinding game)
    {
        game.DisplayName = (game.DisplayName ?? "").Trim();
        if (game.DisplayName.Length > 64) game.DisplayName = game.DisplayName[..64];
        game.MonitorDeviceName = (game.MonitorDeviceName ?? "").Trim();
        if (game.MonitorDeviceName.Length > 128) game.MonitorDeviceName = string.Empty;
        game.Crosshair = NormalizeCrosshair(game.Crosshair ?? new());
        game.Overlay = NormalizeOverlay(game.Overlay ?? new());
        game.ProfileName ??= "Natural";
        return game;
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            AppSettings copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.SerializeToUtf8Bytes(settings, Options), Options)!;
            if (File.Exists(SettingsPath))
            {
                if (new FileInfo(SettingsPath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Существующий файл настроек превышает 4 МиБ; он сохранён без перезаписи.");
                byte[] original = File.ReadAllBytes(SettingsPath);
                bool legacy = true;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(original);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                        if (property.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase)
                            && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int version)) legacy = version < 3;
                }
                catch (JsonException) { }
                if (legacy) AtomicFile.Write(Path.Combine(DirectoryPath, $"settings.pre-v0.12-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"), original);
            }
            AtomicFile.Write(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(Normalize(copy), Options));
        }
    }

    public void ExportProfiles(string path, IEnumerable<DisplayProfile> profiles) =>
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(profiles.Select(p => NormalizeProfile(p.Clone())), Options));

    public List<DisplayProfile> ImportProfiles(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Файл профилей превышает 1 МиБ.");
        List<DisplayProfile> profiles = JsonSerializer.Deserialize<List<DisplayProfile>>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Файл не содержит список профилей.");
        if (profiles.Any(p => p == null || string.IsNullOrWhiteSpace(p.Name))) throw new InvalidDataException("Есть пустой профиль или название.");
        if (profiles.Any(p => p.Name.Trim().Length > 64 || p.Saturation is < 0 or > 220 || p.Vibrance is < 0 or > 100
            || p.Brightness is < -50 or > 50 || p.Contrast is < 50 or > 150 || p.Gamma is < 50 or > 200
            || p.Temperature is < 3000 or > 10000 || p.Shadows is < 0 or > 100))
            throw new InvalidDataException("Профиль содержит значения вне поддерживаемого диапазона.");
        if (profiles.Count > 100) throw new InvalidDataException("Разрешено не больше 100 профилей за импорт.");
        return profiles.Select(NormalizeProfile).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }
}
