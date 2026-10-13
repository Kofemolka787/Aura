using System;
using System.IO;
using System.Text.Json;
using ColorForge.Models;
using NvAPIWrapper.DRS;
using NvAPIWrapper.DRS.SettingValues;
using NvAPIWrapper.Native.DRS;

namespace ColorForge.Services;

public sealed class NvidiaGameProfileService
{
    private sealed record Original(string Executable, string ProfileName, bool CreatedProfile, string SettingKind, uint Value);
    private static string SnapshotPath(string executable) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AURA", "DriverRecovery", AuthProtocol.Hash(executable.ToUpperInvariant()) + ".dat");
    private static Original? Load(string executable) => File.Exists(SnapshotPath(executable))
        ? JsonSerializer.Deserialize<Original>(LicenseStore.UnprotectBytes(File.ReadAllBytes(SnapshotPath(executable)))) : null;
    private static void Save(Original original) => AtomicFile.Write(SnapshotPath(original.Executable),
        LicenseStore.ProtectBytes(JsonSerializer.SerializeToUtf8Bytes(original)));

    public string ApplyMaximumPerformance(string executablePath)
    {
        using var gate = HardwareOperations.Enter();
        string executable = NormalizeExecutable(executablePath);
        using DriverSettingsSession session = DriverSettingsSession.CreateAndLoad();
        DriverSettingsProfile? profile = session.FindApplicationProfile(executable);
        Original? original = Load(executable);
        if (original != null)
        {
            if (profile == null || profile.Name != original.ProfileName || !IsMaximum(profile.GetSetting(KnownSettingId.PreferredPerformanceState)))
                throw new InvalidOperationException("Профиль NVIDIA изменён вне AURA. Сначала восстанови сохранённое состояние.");
            return "NVIDIA: сохранённый режим уже применён.";
        }
        if (profile == null)
        {
            string name = "AURA · " + Path.GetFileNameWithoutExtension(executable) + " · " + Guid.NewGuid().ToString("N")[..8];
            original = new(executable, name, true, "Absent", 0);
            Save(original); // Recovery is durable before creating or modifying a driver profile.
            profile = DriverSettingsProfile.CreateProfile(session, name);
            ProfileApplication.CreateApplication(profile, executable, name);
        }
        else
        {
            ProfileSetting? setting = profile.GetSetting(KnownSettingId.PreferredPerformanceState);
            string kind = setting == null || setting.SettingLocation != DRSSettingLocation.CurrentProfile ? "Absent"
                : setting.IsCurrentValuePredefined ? "Predefined" : "Custom";
            original = new(executable, profile.Name, false, kind, setting == null ? 0 : Convert.ToUInt32(setting.CurrentValue));
            Save(original);
        }
        profile.SetSetting(KnownSettingId.PreferredPerformanceState, (uint)PreferredPerformanceState.PreferMaximum);
        session.Save();
        using DriverSettingsSession check = DriverSettingsSession.CreateAndLoad();
        if (!IsMaximum(check.FindApplicationProfile(executable)?.GetSetting(KnownSettingId.PreferredPerformanceState)))
            throw new InvalidOperationException("Драйвер не подтвердил настройку; снимок восстановления сохранён.");
        return $"NVIDIA: максимальная производительность включена для {Path.GetFileName(executable)}";
    }

    public string RestoreDriverDefault(string executablePath)
    {
        using var gate = HardwareOperations.Enter();
        string executable = NormalizeExecutable(executablePath);
        Original? original = Load(executable);
        if (original == null) return "AURA не меняла этот профиль: снимка для восстановления нет.";
        using DriverSettingsSession session = DriverSettingsSession.CreateAndLoad();
        DriverSettingsProfile? profile = session.FindProfileByName(original.ProfileName);
        if (profile == null)
        {
            if (!original.CreatedProfile) throw new InvalidOperationException("Исходный профиль NVIDIA удалён. Снимок сохранён.");
            File.Delete(SnapshotPath(executable)); return "Созданный AURA профиль уже удалён.";
        }
        if (original.SettingKind == "Custom") profile.SetSetting(KnownSettingId.PreferredPerformanceState, original.Value);
        else if (original.SettingKind == "Predefined") profile.RestoreSettingToDefault(KnownSettingId.PreferredPerformanceState);
        else if (profile.GetSetting(KnownSettingId.PreferredPerformanceState)?.SettingLocation == DRSSettingLocation.CurrentProfile)
            profile.DeleteSetting(KnownSettingId.PreferredPerformanceState);
        if (original.CreatedProfile)
        {
            if (profile.GetApplicationByName(executable) != null) profile.DeleteApplicationByName(executable);
            if (!profile.IsPredefined && profile.NumberOfApplications == 0 && profile.NumberOfSettings == 0) profile.Delete();
        }
        session.Save();
        using DriverSettingsSession check = DriverSettingsSession.CreateAndLoad();
        ProfileSetting? restored = check.FindProfileByName(original.ProfileName)?.GetSetting(KnownSettingId.PreferredPerformanceState);
        bool verified = original.SettingKind switch {
            "Custom" => restored?.SettingLocation == DRSSettingLocation.CurrentProfile && !restored.IsCurrentValuePredefined && Convert.ToUInt32(restored.CurrentValue) == original.Value,
            "Predefined" => restored != null && restored.IsCurrentValuePredefined && Convert.ToUInt32(restored.CurrentValue) == original.Value,
            _ => restored == null || restored.SettingLocation != DRSSettingLocation.CurrentProfile
        };
        if (!verified) throw new InvalidOperationException("Восстановление NVIDIA не подтверждено; снимок сохранён.");
        File.Delete(SnapshotPath(executable));
        return $"NVIDIA: исходный режим питания {Path.GetFileName(executable)} восстановлен.";
    }
    private static bool IsMaximum(ProfileSetting? setting) => setting?.SettingLocation == DRSSettingLocation.CurrentProfile
        && Convert.ToUInt32(setting.CurrentValue) == (uint)PreferredPerformanceState.PreferMaximum;
    private static string NormalizeExecutable(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Выбери .exe игры.", nameof(value));
        string executable = value.Trim();
        return executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable : executable + ".exe";
    }
}
