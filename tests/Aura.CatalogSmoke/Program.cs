using System;
using System.Collections.Generic;
using System.Linq;
using ColorForge.Models;
using ColorForge.Services;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

WindowsTweaksAudit audit = WindowsTweaksService.AuditCatalog();
Require(audit.Success, string.Join(Environment.NewLine, audit.Errors));
Require(audit.DefinitionCount == 82, $"Expected 82 tweaks, got {audit.DefinitionCount}.");
Require(audit.SafeCount + audit.CautionCount + audit.DangerCount == 82, "Risk counters do not cover the full catalog.");

string[] expectedIds =
{
    "disable_telemetry", "disable_advertising_id", "disable_error_reporting", "disable_cortana",
    "disable_location_services", "disable_diagnostic_services", "disable_clipboard_history",
    "disable_text_input_personalization", "disable_consumer_features", "disable_copilot",
    "disable_feedback_requests", "disable_suggested_actions", "disable_finish_setup",
    "disable_app_launch_tracking", "disable_online_speech", "disable_activity_history",
    "disable_tailored_experiences", "disable_recall", "disable_telemetry_autologger",
    "disable_remote_assistance", "disable_nvidia_telemetry", "disable_xbox_dvr", "disable_amd_overlay",
    "disable_gamebar_presence_writer", "disable_xbox_hotkeys", "game_task_priority", "markc_mouse_fix",
    "fast_keyboard_repeat", "msi_mode_gpu", "allow_high_resolution_timer", "disable_auto_hdr",
    "fast_startup", "remove_startup_delay", "high_performance_plan", "never_spin_down_disks",
    "performance_over_appearance", "disable_background_apps", "disable_power_throttling",
    "reduce_cpu_idle_drops", "disable_ntfs_last_access", "disable_network_throttling",
    "disable_hibernation", "disable_storage_sense", "disable_aero_shake", "enable_long_paths",
    "classic_context_menu", "alt_tab_windows_only", "disable_click_to_do", "hide_onedrive_ads",
    "disable_edge_sidebar", "skip_lock_screen", "disable_search_highlights", "end_task_taskbar",
    "disable_bing_start", "disable_start_recommendations", "hide_search_icon", "hide_task_view_icon",
    "disable_sticky_keys", "acrylic_blur_login", "disable_interface_transparency",
    "disable_file_explorer_home", "disable_file_explorer_gallery", "remove_consumer_apps",
    "remove_xbox_components", "remove_widgets", "remove_microsoft_store", "disable_store_auto_updates",
    "disable_onedrive_autostart", "disable_edge_background", "disable_windows_ink", "disable_sysmain",
    "disable_search_indexing", "disable_print_spooler", "manual_windows_updates",
    "disable_core_isolation_vbs", "disable_usb_device_power_saving", "disable_usb_selective_suspend",
    "disable_driver_auto_install", "optimize_task_scheduler", "disable_autoplay",
    "disable_auto_reboot_bsod", "disable_reserved_storage"
};

HashSet<string> actualIds = new(WindowsTweakCatalog.All.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
Require(expectedIds.All(actualIds.Contains), "One or more required screenshot tweaks are missing.");
Require(actualIds.Count == expectedIds.Length, "The catalog contains an unexpected tweak ID.");

IReadOnlyList<string> low = WindowsTweakCatalog.ProfileIds(WindowsTweakProfile.Low);
IReadOnlyList<string> medium = WindowsTweakCatalog.ProfileIds(WindowsTweakProfile.Medium);
IReadOnlyList<string> ultra = WindowsTweakCatalog.ProfileIds(WindowsTweakProfile.Ultra);
Require(low.Count > 0 && medium.Count > low.Count && ultra.Count > medium.Count, "Performance profiles are not cumulative.");
Require(low.All(medium.Contains) && medium.All(ultra.Contains), "Low/Medium/Ultra profile nesting is broken.");
Require(WindowsTweakCatalog.All.Where(x => x.Risk == WindowsTweakRisk.Danger).All(x => x.Profiles == WindowsTweakProfile.None), "A dangerous tweak was enabled by a preset.");

WindowsTweaksService service = new();
Require(service.ValidateSelection(new[] { "fast_startup", "disable_hibernation" }).Count == 1, "Fast-startup/hibernation conflict was not detected.");
Require(service.ValidateSelection(new[] { "missing_tweak" }).Count == 1, "Unknown tweak ID was not rejected.");

string applyScript = WindowsTweaksService.BuildApplyScript(WindowsTweakCatalog.All);
string restoreScript = WindowsTweaksService.BuildRestoreScript();
Require(applyScript.Contains("Checkpoint-Computer", StringComparison.Ordinal), "Restore point creation is missing.");
Require(applyScript.Contains("Save-AuraBackups", StringComparison.Ordinal), "Backup persistence is missing.");
Require(restoreScript.Contains("Restore-AuraBackups", StringComparison.Ordinal), "Backup restore is missing.");
Require(restoreScript.Contains("$script:AuraSkipBackupSave=$true", StringComparison.Ordinal), "Successful restore would recreate backup files.");
Require(!restoreScript.Contains("SCHEME_BALANCED", StringComparison.Ordinal), "Restore must not reset an unrelated power plan.");
Require(!restoreScript.Contains("Restore-BuiltInApps", StringComparison.Ordinal), "Restore must not register unrelated AppX packages.");

WindowsTweakPreferences preferences = new();
WindowsTweaksService.InitializePreferences(preferences, migrateLegacy: false);
Require(preferences.CatalogVersion == WindowsTweakCatalog.Version, "Catalog version was not initialized.");
Require(preferences.EnabledTweakIds.SequenceEqual(low.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase), "Fresh-install selection differs from Low Performance.");

Console.WriteLine($"PASS: {audit.DefinitionCount} tweaks ({audit.SafeCount} safe, {audit.CautionCount} caution, {audit.DangerCount} danger)");
Console.WriteLine($"PASS: profiles Low={low.Count}, Medium={medium.Count}, Ultra={ultra.Count}; dangerous preset entries=0");
Console.WriteLine("PASS: validation, apply script, restore script, and fresh-install defaults");
