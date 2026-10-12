using System;
using System.IO;
using NvAPIWrapper.DRS;
using NvAPIWrapper.DRS.SettingValues;

namespace ColorForge.Services;

public sealed class NvidiaGameProfileService
{
    public string ApplyMaximumPerformance(string executablePath)
    {
        string executable = NormalizeExecutable(executablePath);
        using DriverSettingsSession session = DriverSettingsSession.CreateAndLoad();
        DriverSettingsProfile? profile = session.FindApplicationProfile(executable);
        if (profile == null)
        {
            string profileName = "AURA · " + Path.GetFileNameWithoutExtension(executable);
            profile = session.FindProfileByName(profileName) ?? DriverSettingsProfile.CreateProfile(session, profileName);
            if (profile.GetApplicationByName(executable) == null)
                ProfileApplication.CreateApplication(profile, executable, profileName);
        }

        profile.SetSetting(KnownSettingId.PreferredPerformanceState, (uint)PreferredPerformanceState.PreferMaximum);
        session.Save();
        return $"NVIDIA: максимальная производительность включена для {Path.GetFileName(executable)}";
    }

    public string RestoreDriverDefault(string executablePath)
    {
        string executable = NormalizeExecutable(executablePath);
        using DriverSettingsSession session = DriverSettingsSession.CreateAndLoad();
        DriverSettingsProfile? profile = session.FindApplicationProfile(executable);
        if (profile == null)
            return $"Для {Path.GetFileName(executable)} отдельный NVIDIA-профиль не найден.";

        profile.RestoreSettingToDefault(KnownSettingId.PreferredPerformanceState);
        session.Save();
        return $"NVIDIA: режим питания {Path.GetFileName(executable)} возвращён к настройке драйвера.";
    }

    private static string NormalizeExecutable(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Выбери .exe игры.", nameof(value));
        string executable = value.Trim();
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            executable += ".exe";
        return executable;
    }
}
