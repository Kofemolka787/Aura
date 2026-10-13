using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed record WindowsTweaksResult(bool Success, int ExitCode, string Message, bool RestartRecommended)
{
    public TweakRunReport? Report { get; init; }
}
public sealed record WindowsTweaksAudit(int DefinitionCount, int SafeCount, int CautionCount, int DangerCount, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

public sealed class WindowsTweaksService
{
    private static readonly HashSet<string> UnrestorableAppRemoval = new(StringComparer.OrdinalIgnoreCase)
    { "remove_consumer_apps", "remove_xbox_components", "remove_widgets", "remove_microsoft_store" };
    public IReadOnlyList<WindowsTweakDefinition> Catalog => WindowsTweakCatalog.All;
    public static bool HasVerifiedRestore(string id) => id != "disable_hibernation" && !UnrestorableAppRemoval.Contains(id);
    public IReadOnlyList<TweakRunReport> LoadHistory()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<TweakRunReport>();
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid == null) return Array.Empty<TweakRunReport>();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AURA", "TweaksV2", sid);
        if (!Directory.Exists(root)) return Array.Empty<TweakRunReport>();
        List<TweakRunReport> reports = new();
        try
        {
            foreach (string path in Directory.GetFiles(root, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(30))
            {
                if (new FileInfo(path).Length > 4 * 1024 * 1024) continue;
                var report = JsonSerializer.Deserialize<TweakRunReport>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (report != null) reports.Add(report);
            }
        }
        catch (Exception exception) { AppLogService.Error("Could not load tweak history.", exception); }
        return reports;
    }

    public bool HasLegacyState()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AURA", "Tweaks");
        return new[] { "registry.clixml", "services.clixml", "tasks.clixml" }.Any(x => File.Exists(Path.Combine(root, x)));
    }

    public bool HasPendingState()
    {
        if (!OperatingSystem.IsWindows()) return false;
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        return sid != null && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AURA", "TweaksV2", sid, "history.clixml"));
    }

    public static WindowsTweaksAudit AuditCatalog()
    {
        IReadOnlyList<string> errors = WindowsTweakCatalog.ValidateCatalog();
        return new WindowsTweaksAudit(
            WindowsTweakCatalog.All.Count,
            WindowsTweakCatalog.All.Count(x => x.Risk == WindowsTweakRisk.Safe),
            WindowsTweakCatalog.All.Count(x => x.Risk == WindowsTweakRisk.Caution),
            WindowsTweakCatalog.All.Count(x => x.Risk == WindowsTweakRisk.Danger),
            errors);
    }

    public static void InitializePreferences(WindowsTweakPreferences options, bool migrateLegacy)
    {
        if (options.CatalogVersion >= WindowsTweakCatalog.Version && options.EnabledTweakIds != null)
            return;

        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        if (migrateLegacy)
        {
            if (options.FastStartup) selected.Add("fast_startup");
            if (options.RemoveStartupDelay) selected.Add("remove_startup_delay");
            if (options.GamingPerformance)
            {
                selected.Add("disable_xbox_dvr");
                selected.Add("game_task_priority");
                selected.Add("disable_network_throttling");
            }
            if (options.ReduceVisualEffects) selected.Add("performance_over_appearance");
            if (options.DisableBackgroundApps) selected.Add("disable_background_apps");
            if (options.ReduceTelemetry)
            {
                selected.Add("disable_telemetry");
                selected.Add("disable_advertising_id");
                selected.Add("disable_tailored_experiences");
                selected.Add("disable_consumer_features");
            }
            if (options.RemoveCopilot) selected.Add("disable_copilot");
            if (options.RemoveConsumerApps) selected.Add("remove_consumer_apps");
            if (options.RemoveXboxApps) selected.Add("remove_xbox_components");
            if (options.DisableSysMain) selected.Add("disable_sysmain");
            if (options.DisableWindowsSearch) selected.Add("disable_search_indexing");
            if (options.DisablePrintSpooler) selected.Add("disable_print_spooler");
            if (options.RemoveMicrosoftStore) selected.Add("remove_microsoft_store");
        }
        else
        {
            selected.UnionWith(WindowsTweakCatalog.ProfileIds(WindowsTweakProfile.Low));
        }

        options.EnabledTweakIds = selected
            .Where(id => WindowsTweakCatalog.All.Any(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        options.CatalogVersion = WindowsTweakCatalog.Version;
    }

    public IReadOnlyList<string> ValidateSelection(IEnumerable<string> ids)
    {
        HashSet<string> selected = new(ids ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> known = new(Catalog.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
        List<string> errors = selected.Where(id => !known.Contains(id)).Select(id => $"Неизвестный твик: {id}").ToList();
        errors.AddRange(selected.Where(UnrestorableAppRemoval.Contains).Select(id => id + ": нужен проверенный источник MSIX для точного восстановления. Удаление недоступно."));

        if (selected.Contains("fast_startup") && selected.Contains("disable_hibernation"))
            errors.Add("«Быстрый запуск» и «Отключить гибернацию» несовместимы. Оставь один из двух пунктов.");

        return errors;
    }


    public Task<WindowsTweaksResult> ApplyAsync(WindowsTweakPreferences options, CancellationToken token = default)
    {
        string[] ids = (options.EnabledTweakIds ?? new()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        List<string> errors = ValidateSelection(ids).ToList();
        if (errors.Count > 0 || ids.Length == 0)
            return Task.FromResult(new WindowsTweaksResult(false, -1, errors.Count > 0 ? string.Join(Environment.NewLine, errors) : "Не выбран ни один твик.", false));
        return RunElevatedAsync(BuildApplyScript(Catalog.Where(x => ids.Contains(x.Id, StringComparer.OrdinalIgnoreCase)).ToArray()), token);
    }

    public Task<WindowsTweaksResult> RestoreDefaultsAsync(CancellationToken token = default) =>
        RunElevatedAsync(BuildRestoreScript(), token);

    private static string Runtime()
    {
        var assembly = typeof(WindowsTweaksService).Assembly;
        string name = assembly.GetManifestResourceNames().Single(x => x.EndsWith("Runtime.WindowsTweaks.ps1", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using StreamReader reader = new(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    internal static string BuildApplyScript(IReadOnlyList<WindowsTweakDefinition> selected)
    {
        StringBuilder script = new(Runtime());
        script.AppendLine("\n$AuraSelections = @(");
        foreach (WindowsTweakDefinition definition in selected)
        {
            script.AppendLine("[pscustomobject]@{ Id='" + Quote(definition.Id) + "'; Title='" + Quote(definition.Title)
                + "'; RequiresWindows11=$" + (definition.RequiresWindows11 ? "true" : "false") + "; Script={");
            script.AppendLine(definition.ApplyScript);
            script.AppendLine("} }");
        }
        script.AppendLine(")\nInvoke-AuraApply $AuraSelections");
        return script.ToString();
    }

    internal static string BuildRestoreScript() => Runtime() + "\nInvoke-AuraRestore\n";
    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static async Task<WindowsTweaksResult> RunElevatedAsync(string script, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Применение твиков доступно только в Windows.");
        token.ThrowIfCancellationRequested();
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Не удалось определить пользователя Windows.");
        string runId = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AURA", "Runtime");
        string scriptPath = Path.Combine(directory, "tweaks-" + runId + ".ps1");
        byte[] bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(script)).ToArray();
        AtomicFile.Write(scriptPath, bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        // Read once, verify those exact bytes, and execute the verified in-memory text.
        // The elevated process never reopens the script after verification.
        string systemModules = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
        string bootstrap = "$ErrorActionPreference='Stop'; $env:PSModulePath='" + Quote(systemModules) + "'; try { $b=[IO.File]::ReadAllBytes('" + Quote(scriptPath)
            + "'); $h=[Security.Cryptography.SHA256]::Create(); try { $actual=([BitConverter]::ToString($h.ComputeHash($b))).Replace('-','') } finally { $h.Dispose() }; "
            + "if($actual -ne '" + hash + "') { throw 'Проверка скрипта AURA не пройдена' }; "
            + "$s=[Text.Encoding]::UTF8.GetString($b).TrimStart([char]0xFEFF); & ([scriptblock]::Create($s)) -TargetSid '" + sid + "' -RunId '" + runId
            + "' } catch { Write-Error $_; exit 2 }";
        string resultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AURA", "TweaksV2", sid, runId + ".json");
        ProcessStartInfo start = new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)),
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
        };
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запущен.");
            // Once system writes begin, let the protected journal finish instead of killing the child.
            await process.WaitForExitAsync(CancellationToken.None);
            if (!File.Exists(resultPath)) return new(false, process.ExitCode, "Нет подтверждённого отчёта операции. Сохранённые снимки не удалены; проверь журнал Windows.", false);
            TweakRunReport report = JsonSerializer.Deserialize<TweakRunReport>(await File.ReadAllTextAsync(resultPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Отчёт операции повреждён.");
            bool success = process.ExitCode == 0 && report.Success;
            string message = success
                ? $"Операция завершена. Применено: {report.Actions.Count(x => x.Status == "Applied")}, восстановлено: {report.Actions.Count(x => x.Status == "Restored")}, пропущено: {report.Actions.Count(x => x.Status == "Skipped")}. Точка восстановления: {report.RestorePoint}."
                : "Операция выполнена частично: " + string.Join("; ", report.Errors);
            return new(success, process.ExitCode, message + " Журнал: " + report.LogPath, report.Remaining > 0) { Report = report };
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        { return new(false, 1223, "Повышение прав отменено; изменения не запускались.", false); }
        finally { try { File.Delete(scriptPath); } catch (Exception exception) { AppLogService.Error("Could not clean up verified tweak script.", exception); } }
    }
}
