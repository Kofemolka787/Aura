using System;
using System.Linq;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed class ProfileCatalogService
{
    private readonly AppSettings _settings;
    public ProfileCatalogService(AppSettings settings) => _settings = settings;
    public static bool IsBuiltIn(string name) => Defaults.CreateProfiles().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    private string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 64) throw new ArgumentException("Название должно содержать от 1 до 64 символов.");
        if (_settings.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Профиль с таким названием уже существует.");
        return name;
    }
    public DisplayProfile Create(string name, DisplayProfile values)
    {
        DisplayProfile profile = SettingsStore.NormalizeProfile(values.Clone(ValidateName(name)));
        _settings.Profiles.Add(profile);
        return profile;
    }
    public void Rename(DisplayProfile profile, string name)
    {
        if (IsBuiltIn(profile.Name)) throw new InvalidOperationException("Встроенная сцена сохраняется как новый пользовательский профиль.");
        if (profile.Name.Equals(name.Trim(), StringComparison.Ordinal)) return;
        string replacement = ValidateName(name), previous = profile.Name;
        profile.Name = replacement;
        foreach (GameBinding game in _settings.Games.Where(g => g.ProfileName.Equals(previous, StringComparison.OrdinalIgnoreCase))) game.ProfileName = replacement;
        foreach (DisplayProfile manual in _settings.ManualMonitorProfiles.Values.Concat(_settings.MonitorProfiles.Values))
            if (manual.Name.Equals(previous, StringComparison.OrdinalIgnoreCase)) manual.Name = replacement;
        if (_settings.SelectedProfileName.Equals(previous, StringComparison.OrdinalIgnoreCase)) _settings.SelectedProfileName = replacement;
        if (_settings.Current.Name.Equals(previous, StringComparison.OrdinalIgnoreCase)) _settings.Current.Name = replacement;
    }
    public void Delete(DisplayProfile profile)
    {
        if (IsBuiltIn(profile.Name)) throw new InvalidOperationException("Встроенная сцена не удаляется.");
        _settings.Profiles.Remove(profile);
        foreach (GameBinding game in _settings.Games.Where(g => g.ProfileName.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))) game.ProfileName = "Natural";
        if (_settings.SelectedProfileName.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)) _settings.SelectedProfileName = "Natural";
        // Preserve edited monitor values even when their named template is removed.
        foreach (DisplayProfile manual in _settings.ManualMonitorProfiles.Values.Concat(_settings.MonitorProfiles.Values))
            if (manual.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)) manual.Name = "Ручная настройка";
    }
}
