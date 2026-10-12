using System;
using System.Collections.Generic;
using System.Linq;

namespace ColorForge.Services;

public enum WindowsTweakRisk
{
    Safe,
    Caution,
    Danger
}

[Flags]
public enum WindowsTweakProfile
{
    None = 0,
    Low = 1,
    Medium = 2,
    Ultra = 4,
    All = Low | Medium | Ultra
}

public sealed record WindowsTweakDefinition(
    string Id,
    string Title,
    string Description,
    string Category,
    WindowsTweakRisk Risk,
    WindowsTweakProfile Profiles,
    string ApplyScript,
    string RestoreScript = "",
    bool RequiresWindows11 = false);

public static class WindowsTweakCatalog
{
    public const int Version = 1;

    public const string Privacy = "ПРИВАТНОСТЬ И ТЕЛЕМЕТРИЯ";
    public const string Gaming = "ИГРЫ, ВВОД И ВИДЕОКАРТА";
    public const string Performance = "ПРОИЗВОДИТЕЛЬНОСТЬ И ПИТАНИЕ";
    public const string Interface = "ИНТЕРФЕЙС И ПРОВОДНИК";
    public const string Apps = "ПРИЛОЖЕНИЯ И КОМПОНЕНТЫ";
    public const string Services = "СЛУЖБЫ, УСТРОЙСТВА И СИСТЕМА";

    private const WindowsTweakProfile Lmu = WindowsTweakProfile.All;
    private const WindowsTweakProfile Mu = WindowsTweakProfile.Medium | WindowsTweakProfile.Ultra;
    private const WindowsTweakProfile U = WindowsTweakProfile.Ultra;
    private const WindowsTweakProfile Manual = WindowsTweakProfile.None;

    public static IReadOnlyList<WindowsTweakDefinition> All { get; } = Build();

    public static IReadOnlyList<string> ValidateCatalog()
    {
        List<string> errors = new();
        foreach (IGrouping<string, WindowsTweakDefinition> duplicate in All.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"Повтор ID: {duplicate.Key}");
        foreach (WindowsTweakDefinition item in All)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title))
                errors.Add("Найдена запись без ID или заголовка.");
            if (string.IsNullOrWhiteSpace(item.ApplyScript))
                errors.Add($"{item.Id}: отсутствует команда применения.");
        }
        if (All.Count != 82)
            errors.Add($"Ожидалось 82 твика, найдено {All.Count}.");
        return errors;
    }

    public static IReadOnlyList<string> ProfileIds(WindowsTweakProfile profile) => All
        .Where(item => (item.Profiles & profile) != 0)
        .Select(item => item.Id)
        .ToArray();

    private static IReadOnlyList<WindowsTweakDefinition> Build() => new[]
    {
        D("disable_telemetry", "Отключить телеметрию", "Минимальный уровень диагностических данных Windows.", Privacy, WindowsTweakRisk.Safe, Lmu,
            A(RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "MaxTelemetryAllowed", 0))),
        D("disable_advertising_id", "Отключить рекламный ID", "Запрещает приложениям использовать персональный рекламный идентификатор.", Privacy, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)),
        D("disable_error_reporting", "Отключить отчёты об ошибках", "Отключает отправку Windows Error Reporting; локальные журналы остаются.", Privacy, WindowsTweakRisk.Caution, U,
            A(RD(@"HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1), SD("WerSvc"))),
        D("disable_cortana", "Отключить Cortana", "Отключает Cortana политикой, не ломая обычный поиск Windows.", Privacy, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0)),
        D("disable_location_services", "Отключить службы геолокации", "Запрещает системный доступ к местоположению.", Privacy, WindowsTweakRisk.Caution, U,
            A(RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1), RD(@"HKLM:\SYSTEM\CurrentControlSet\Services\lfsvc\Service\Configuration", "Status", 0))),
        D("disable_diagnostic_services", "Отключить диагностические службы", "Останавливает DiagTrack и dmwappushservice.", Privacy, WindowsTweakRisk.Caution, U,
            A(SD("DiagTrack"), SD("dmwappushservice"))),
        D("disable_clipboard_history", "Отключить историю буфера обмена", "Отключает облачную и локальную историю Win+V.", Privacy, WindowsTweakRisk.Caution, U,
            A(RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "AllowClipboardHistory", 0), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "AllowCrossDeviceClipboard", 0))),
        D("disable_text_input_personalization", "Отключить персонализацию ввода", "Запрещает сбор рукописного ввода, словаря и контактов для подсказок.", Privacy, WindowsTweakRisk.Safe, Lmu,
            A(RD(@"HKCU:\Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1), RD(@"HKCU:\Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1), RD(@"HKCU:\Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0))),
        D("disable_consumer_features", "Отключить потребительские функции", "Запрещает автоматические рекомендации и установку рекламных приложений.", Privacy, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1)),
        D("disable_copilot", "Отключить Microsoft Copilot", "Отключает Copilot политиками без удаления пакета.", Privacy, WindowsTweakRisk.Safe, Lmu,
            A(RD(@"HKCU:\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1))),
        D("disable_feedback_requests", "Отключить запросы отзывов", "Убирает периодические запросы Windows Feedback.", Privacy, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKCU:\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0)),
        D("disable_suggested_actions", "Отключить предлагаемые действия", "Отключает Suggested Actions для выделенного текста и буфера обмена.", Privacy, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\SmartActionPlatform\SmartClipboard", "Disabled", 1), requiresWindows11: true),
        D("disable_finish_setup", "Отключить экран «Завершим настройку»", "Убирает SCOOBE и напоминания завершить настройку устройства.", Privacy, WindowsTweakRisk.Safe, Lmu,
            A(RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\OOBE", "DisablePrivacyExperience", 1))),
        D("disable_app_launch_tracking", "Отключить отслеживание запуска приложений", "Не даёт меню Пуск вести статистику запуска программ.", Privacy, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", 0)),
        D("disable_online_speech", "Отключить облачное распознавание речи", "Запрещает онлайн-службу распознавания речи Microsoft.", Privacy, WindowsTweakRisk.Caution, U,
            RD(@"HKCU:\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0)),
        D("disable_activity_history", "Отключить журнал действий", "Отключает Timeline и публикацию активности пользователя.", Privacy, WindowsTweakRisk.Safe, Lmu,
            A(RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0))),
        D("disable_tailored_experiences", "Отключить персонализированные предложения", "Не использует диагностические данные для советов и рекламы.", Privacy, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)),
        D("disable_recall", "Отключить Windows Recall", "Запрещает сохранение снимков Recall политиками Windows AI.", Privacy, WindowsTweakRisk.Safe, Lmu,
            A(RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", 0), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1)), requiresWindows11: true),
        D("disable_telemetry_autologger", "Отключить Telemetry AutoLogger", "Отключает автосеанс Diagtrack-Listener при загрузке.", Privacy, WindowsTweakRisk.Caution, U,
            RD(@"HKLM:\SYSTEM\CurrentControlSet\Control\WMI\AutoLogger\Diagtrack-Listener", "Start", 0)),
        D("disable_remote_assistance", "Отключить удалённого помощника", "Запрещает приглашения Windows Remote Assistance; RDP не затрагивается.", Privacy, WindowsTweakRisk.Safe, U,
            A(RD(@"HKLM:\SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 0), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services", "fAllowToGetHelp", 0))),

        D("disable_nvidia_telemetry", "Отключить телеметрию NVIDIA", "Останавливает телеметрическую службу и задачи NVIDIA, если они установлены.", Gaming, WindowsTweakRisk.Caution, U,
            A(SD("NvTelemetryContainer"), TD(@"\NvTmMon_{*}"), TD(@"\NvTmRep_{*}"), TD(@"\NvTmRepOnLogon_{*}"))),
        D("disable_xbox_dvr", "Отключить Xbox DVR / Game Bar", "Отключает фоновую запись клипов, не удаляя приложение Xbox.", Gaming, WindowsTweakRisk.Safe, Mu,
            A(RD(@"HKCU:\System\GameConfigStore", "GameDVR_Enabled", 0), RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0))),
        D("disable_amd_overlay", "Отключить AMD Overlay", "Выключает внутриигровой оверлей Radeon Software для текущего пользователя.", Gaming, WindowsTweakRisk.Safe, Mu,
            A(RD(@"HKCU:\Software\AMD\CN", "OverlayEnabled", 0), RD(@"HKCU:\Software\AMD\DVR", "RecordEnable", 0))),
        D("disable_gamebar_presence_writer", "Отключить GameBar Presence Writer", "Отключает шаблон фоновой службы захвата Game Bar.", Gaming, WindowsTweakRisk.Caution, U,
            RD(@"HKLM:\SYSTEM\CurrentControlSet\Services\BcastDVRUserService", "Start", 4)),
        D("disable_xbox_hotkeys", "Отключить горячие клавиши Xbox Game Bar", "Отключает вызов панели кнопкой Xbox и сочетаниями Game Bar.", Gaming, WindowsTweakRisk.Safe, Mu,
            A(RD(@"HKCU:\Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0), RD(@"HKCU:\Software\Microsoft\GameBar", "ShowStartupPanel", 0))),
        D("game_task_priority", "Приоритет игры в MMCSS", "Задаёт высокий приоритет задач Games в Multimedia Class Scheduler.", Gaming, WindowsTweakRisk.Safe, Mu,
            A(RS(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category", "High"), RS(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "SFIO Priority", "High"), RD(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 8), RD(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", 6))),
        D("markc_mouse_fix", "MarkC MouseFix — кривая 1:1", "Отключает ускорение указателя и ставит нейтральную кривую мыши.", Gaming, WindowsTweakRisk.Caution, Manual,
            A(RS(@"HKCU:\Control Panel\Mouse", "MouseSpeed", "0"), RS(@"HKCU:\Control Panel\Mouse", "MouseThreshold1", "0"), RS(@"HKCU:\Control Panel\Mouse", "MouseThreshold2", "0"), RS(@"HKCU:\Control Panel\Mouse", "MouseSensitivity", "10"), RB(@"HKCU:\Control Panel\Mouse", "SmoothMouseXCurve", "00,00,00,00,00,00,00,00,15,6e,00,00,00,00,00,00,00,40,01,00,00,00,00,00,29,dc,03,00,00,00,00,00,00,00,28,00,00,00,00,00"), RB(@"HKCU:\Control Panel\Mouse", "SmoothMouseYCurve", "00,00,00,00,00,00,00,00,fd,11,01,00,00,00,00,00,00,24,04,00,00,00,00,00,00,fc,12,00,00,00,00,00,00,c0,bb,01,00,00,00,00"))),
        D("fast_keyboard_repeat", "Быстрый автоповтор клавиатуры", "Минимальная задержка и максимальная скорость повтора клавиш.", Gaming, WindowsTweakRisk.Safe, Mu,
            A(RS(@"HKCU:\Control Panel\Keyboard", "KeyboardDelay", "0"), RS(@"HKCU:\Control Panel\Keyboard", "KeyboardSpeed", "31"))),
        D("msi_mode_gpu", "MSI Mode для видеокарты", "Включает Message Signaled Interrupts для обнаруженных GPU. Нужна проверка стабильности.", Gaming, WindowsTweakRisk.Danger, Manual,
            "Enable-GpuMsiMode"),
        D("allow_high_resolution_timer", "Разрешить high-resolution timer requests", "Разрешает приложениям запрашивать глобальное высокое разрешение таймера.", Gaming, WindowsTweakRisk.Safe, Mu,
            RD(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1)),
        D("disable_auto_hdr", "Отключить Auto HDR", "Отключает автоматическое HDR-преобразование SDR-игр.", Gaming, WindowsTweakRisk.Caution, U,
            "Set-AutoHdrToken $false", "Set-AutoHdrToken $true", requiresWindows11: true),

        D("fast_startup", "Быстрый запуск Windows", "Включает гибридное завершение и Hiberboot.", Performance, WindowsTweakRisk.Safe, Lmu,
            A("powercfg.exe /hibernate on | Out-Null", RD(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1)),
            "powercfg.exe /hibernate on | Out-Null"),
        D("remove_startup_delay", "Убрать задержку автозапуска", "Запускает программы сразу после входа в Windows.", Performance, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0)),
        D("high_performance_plan", "План питания «Высокая производительность»", "Активирует встроенный план High performance, если он доступен.", Performance, WindowsTweakRisk.Caution, Mu,
            "Enable-HighPerformancePlan", "powercfg.exe /setactive SCHEME_BALANCED | Out-Null"),
        D("never_spin_down_disks", "Не отключать жёсткие диски", "Ставит тайм-аут отключения дисков в 0 для сети и батареи.", Performance, WindowsTweakRisk.Caution, U,
            A("powercfg.exe /change disk-timeout-ac 0 | Out-Null", "powercfg.exe /change disk-timeout-dc 0 | Out-Null"),
            A("powercfg.exe /change disk-timeout-ac 20 | Out-Null", "powercfg.exe /change disk-timeout-dc 10 | Out-Null")),
        D("performance_over_appearance", "Производительность вместо оформления", "Отключает большую часть анимаций и визуальных эффектов.", Performance, WindowsTweakRisk.Safe, Mu,
            A(RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2), RS(@"HKCU:\Control Panel\Desktop", "MenuShowDelay", "100"), RS(@"HKCU:\Control Panel\Desktop\WindowMetrics", "MinAnimate", "0"))),
        D("disable_background_apps", "Отключить фоновые приложения", "Запрещает UWP-приложениям работать в фоне.", Performance, WindowsTweakRisk.Caution, Mu,
            A(RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2))),
        D("disable_power_throttling", "Отключить Power Throttling", "Не даёт Windows снижать производительность фоновых процессов.", Performance, WindowsTweakRisk.Caution, U,
            RD(@"HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1)),
        D("reduce_cpu_idle_drops", "Уменьшить просадки частоты CPU", "Ставит минимальное состояние процессора от сети на 100%; повышает нагрев и расход.", Performance, WindowsTweakRisk.Danger, Manual,
            "powercfg.exe /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 100 | Out-Null; powercfg.exe /setactive SCHEME_CURRENT | Out-Null",
            "powercfg.exe /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 5 | Out-Null; powercfg.exe /setactive SCHEME_CURRENT | Out-Null"),
        D("disable_ntfs_last_access", "Отключить NTFS Last Access Timestamp", "Сокращает лишние обновления меток последнего доступа на NTFS.", Performance, WindowsTweakRisk.Safe, U,
            "fsutil.exe behavior set DisableLastAccess 1 | Out-Null", "fsutil.exe behavior set DisableLastAccess 2 | Out-Null"),
        D("disable_network_throttling", "Отключить Network Throttling", "Убирает мультимедийное ограничение пропускной способности сети.", Performance, WindowsTweakRisk.Safe, Mu,
            RD(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", uint.MaxValue)),
        D("disable_hibernation", "Отключить гибернацию", "Освобождает hiberfil.sys, но отключает гибернацию и быстрый запуск.", Performance, WindowsTweakRisk.Caution, Manual,
            "powercfg.exe /hibernate off | Out-Null", "powercfg.exe /hibernate on | Out-Null"),
        D("disable_storage_sense", "Отключить Контроль памяти", "Запрещает автоматическое удаление временных файлов Storage Sense.", Performance, WindowsTweakRisk.Safe, U,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 0)),

        D("disable_aero_shake", "Отключить Aero Shake", "Не сворачивает остальные окна при встряхивании заголовка.", Interface, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisallowShaking", 1)),
        D("enable_long_paths", "Включить длинные пути (260+ символов)", "Разрешает Win32-приложениям использовать длинные пути.", Interface, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1)),
        D("classic_context_menu", "Классическое контекстное меню Windows 11", "Возвращает полное контекстное меню без второго клика.", Interface, WindowsTweakRisk.Safe, U,
            "Enable-ClassicContextMenu", "Disable-ClassicContextMenu", requiresWindows11: true),
        D("alt_tab_windows_only", "Alt+Tab: только окна", "Не показывает вкладки Microsoft Edge в переключателе Alt+Tab.", Interface, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "MultiTaskingAltTabFilter", 3)),
        D("disable_click_to_do", "Отключить Click to Do", "Отключает системные AI-действия Click to Do.", Interface, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1), requiresWindows11: true),
        D("hide_onedrive_ads", "Скрыть рекламу OneDrive в Проводнике", "Отключает уведомления поставщика синхронизации.", Interface, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSyncProviderNotifications", 0)),
        D("disable_edge_sidebar", "Отключить Edge Sidebar / Copilot Panel", "Отключает боковую панель Edge и кнопку Copilot политиками браузера.", Interface, WindowsTweakRisk.Safe, Lmu,
            A(RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "Microsoft365CopilotChatIconEnabled", 0))),
        D("skip_lock_screen", "Пропускать экран блокировки", "Переходит сразу к экрану входа вместо lock screen.", Interface, WindowsTweakRisk.Caution, U,
            RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen", 1)),
        D("disable_search_highlights", "Отключить подсветку поиска", "Убирает Search Highlights и веб-контент из панели поиска.", Interface, WindowsTweakRisk.Safe, Lmu,
            A(RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "EnableDynamicContentInWSB", 0))),
        D("end_task_taskbar", "«Завершить задачу» в меню панели задач", "Включает команду завершения процесса по правому клику на значке.", Interface, WindowsTweakRisk.Safe, Mu,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", 1), requiresWindows11: true),
        D("disable_bing_start", "Отключить Bing в меню Пуск", "Оставляет в поиске Пуска только локальные результаты.", Interface, WindowsTweakRisk.Safe, Lmu,
            RD(@"HKCU:\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1)),
        D("disable_start_recommendations", "Отключить рекомендации меню Пуск", "Убирает рекомендации, советы и недавние элементы из Пуска.", Interface, WindowsTweakRisk.Safe, Lmu,
            A(RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 0), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection", 1)), requiresWindows11: true),
        D("hide_search_icon", "Скрыть поиск на панели задач", "Убирает значок или поле поиска с панели задач.", Interface, WindowsTweakRisk.Safe, Manual,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0)),
        D("hide_task_view_icon", "Скрыть кнопку «Представление задач»", "Убирает Task View с панели задач; Win+Tab продолжает работать.", Interface, WindowsTweakRisk.Safe, Manual,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton", 0)),
        D("disable_sticky_keys", "Отключить горячую активацию залипания клавиш", "Отключает вызов Sticky Keys пятью нажатиями Shift.", Interface, WindowsTweakRisk.Safe, Mu,
            RS(@"HKCU:\Control Panel\Accessibility\StickyKeys", "Flags", "506")),
        D("acrylic_blur_login", "Acrylic Blur на экране входа", "Разрешает акриловое размытие фона при входе.", Interface, WindowsTweakRisk.Safe, Manual,
            RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "DisableAcrylicBackgroundOnLogon", 0)),
        D("disable_interface_transparency", "Отключить прозрачность интерфейса", "Отключает системные эффекты прозрачности Windows.", Interface, WindowsTweakRisk.Safe, Mu,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0)),
        D("disable_file_explorer_home", "Открывать «Этот компьютер» вместо Home", "Меняет стартовую страницу Проводника на «Этот компьютер».", Interface, WindowsTweakRisk.Safe, Mu,
            RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 1), requiresWindows11: true),
        D("disable_file_explorer_gallery", "Скрыть Gallery в Проводнике", "Убирает раздел Gallery из дерева навигации Windows 11.", Interface, WindowsTweakRisk.Caution, Manual,
            RD(@"HKLM:\SOFTWARE\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}", "System.IsPinnedToNameSpaceTree", 0), requiresWindows11: true),

        D("remove_consumer_apps", "Удалить рекламные приложения", "Удаляет новости, советы, Clipchamp, Solitaire и другие необязательные пакеты.", Apps, WindowsTweakRisk.Caution, Manual,
            "Remove-AppList @('Clipchamp.Clipchamp','Microsoft.BingNews','Microsoft.BingWeather','Microsoft.GetHelp','Microsoft.Getstarted','Microsoft.MicrosoftOfficeHub','Microsoft.MicrosoftSolitaireCollection','Microsoft.MixedReality.Portal','Microsoft.People','Microsoft.PowerAutomateDesktop','Microsoft.Todos','Microsoft.WindowsFeedbackHub','Microsoft.YourPhone','Microsoft.ZuneMusic','Microsoft.ZuneVideo')",
            "Restore-BuiltInApps"),
        D("remove_xbox_components", "Удалить Xbox-компоненты", "Удаляет Xbox/Game Pass пакеты и отключает связанные службы.", Apps, WindowsTweakRisk.Danger, Manual,
            A("Remove-AppList @('Microsoft.GamingApp','Microsoft.Xbox.TCUI','Microsoft.XboxApp','Microsoft.XboxGameOverlay','Microsoft.XboxGamingOverlay','Microsoft.XboxIdentityProvider','Microsoft.XboxSpeechToTextOverlay')", SD("XboxGipSvc"), SD("XblAuthManager"), SD("XblGameSave"), SD("XboxNetApiSvc")),
            "Restore-BuiltInApps"),
        D("remove_widgets", "Удалить виджеты", "Отключает виджеты политикой и удаляет Web Experience Pack.", Apps, WindowsTweakRisk.Caution, Manual,
            A(RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0), RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", 0), "Remove-AppList @('MicrosoftWindows.Client.WebExperience')"),
            "Restore-BuiltInApps", requiresWindows11: true),
        D("remove_microsoft_store", "Удалить Microsoft Store", "Удаляет Store и Store Purchase App. Восстановление может потребовать повторной регистрации пакетов.", Apps, WindowsTweakRisk.Danger, Manual,
            "Remove-AppList @('Microsoft.WindowsStore','Microsoft.StorePurchaseApp')", "Restore-BuiltInApps"),
        D("disable_store_auto_updates", "Отключить автообновления Microsoft Store", "Отключает автоматическое обновление приложений Store политикой.", Apps, WindowsTweakRisk.Caution, U,
            RD(@"HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore", "AutoDownload", 2)),
        D("disable_onedrive_autostart", "Отключить автозапуск OneDrive", "Убирает OneDrive из автозагрузки текущего пользователя.", Apps, WindowsTweakRisk.Safe, Mu,
            RR(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Run", "OneDrive")),
        D("disable_edge_background", "Отключить фоновый режим Edge", "Запрещает Edge и Startup Boost работать после закрытия окна.", Apps, WindowsTweakRisk.Safe, Mu,
            A(RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0))),
        D("disable_windows_ink", "Отключить Windows Ink", "Отключает Windows Ink Workspace; не включай при использовании пера.", Apps, WindowsTweakRisk.Caution, Manual,
            RD(@"HKLM:\SOFTWARE\Policies\Microsoft\WindowsInkWorkspace", "AllowWindowsInkWorkspace", 0)),

        D("disable_sysmain", "Отключить SysMain", "Отключает службу предзагрузки; полезность зависит от накопителя и объёма RAM.", Services, WindowsTweakRisk.Caution, U,
            SD("SysMain")),
        D("disable_search_indexing", "Отключить Windows Search Indexing", "Отключает индексатор WSearch; поиск файлов станет медленнее.", Services, WindowsTweakRisk.Caution, U,
            SD("WSearch")),
        D("disable_print_spooler", "Отключить диспетчер печати", "Отключает Print Spooler; печать и виртуальные принтеры перестанут работать.", Services, WindowsTweakRisk.Danger, Manual,
            SD("Spooler")),
        D("manual_windows_updates", "Только ручные обновления Windows", "Переводит Windows Update в режим уведомления, не отключая службу и Defender.", Services, WindowsTweakRisk.Caution, Manual,
            A(RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "AUOptions", 2), RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1))),
        D("disable_core_isolation_vbs", "Отключить Core Isolation / VBS", "Отключает HVCI/VBS и гипервизор. Ломает Hyper-V, WSL2, Sandbox и часть защит Windows.", Services, WindowsTweakRisk.Danger, Manual,
            A(RD(@"HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0), RD(@"HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0), "bcdedit.exe /set hypervisorlaunchtype off | Out-Null"),
            "bcdedit.exe /deletevalue hypervisorlaunchtype 2>$null | Out-Null"),
        D("disable_usb_device_power_saving", "Отключить энергосбережение USB-устройств", "Отключает selective power management для перечисленных USB-устройств.", Services, WindowsTweakRisk.Danger, Manual,
            "Disable-UsbDevicePowerSaving"),
        D("disable_usb_selective_suspend", "Отключить USB Selective Suspend", "Отключает выборочное приостановление USB в активном плане питания.", Services, WindowsTweakRisk.Caution, Manual,
            A("powercfg.exe /setacvalueindex SCHEME_CURRENT SUB_USB USBSELECTIVE 0 | Out-Null", "powercfg.exe /setdcvalueindex SCHEME_CURRENT SUB_USB USBSELECTIVE 0 | Out-Null", "powercfg.exe /setactive SCHEME_CURRENT | Out-Null"),
            A("powercfg.exe /setacvalueindex SCHEME_CURRENT SUB_USB USBSELECTIVE 1 | Out-Null", "powercfg.exe /setdcvalueindex SCHEME_CURRENT SUB_USB USBSELECTIVE 1 | Out-Null", "powercfg.exe /setactive SCHEME_CURRENT | Out-Null")),
        D("disable_driver_auto_install", "Отключить автоустановку драйверов", "Исключает драйверы из Windows Update; обновлять их придётся вручную.", Services, WindowsTweakRisk.Caution, Manual,
            A(RD(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1), RD(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig", 0))),
        D("optimize_task_scheduler", "Оптимизировать Планировщик заданий", "Отключает только известные задачи CEIP и Application Experience.", Services, WindowsTweakRisk.Caution, U,
            A(TD(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"), TD(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"), TD(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"))),
        D("disable_autoplay", "Отключить AutoPlay", "Запрещает автоматический запуск содержимого съёмных носителей.", Services, WindowsTweakRisk.Safe, Lmu,
            A(RD(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun", 255), RD(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun", 255))),
        D("disable_auto_reboot_bsod", "Отключить автоперезагрузку после BSOD", "Оставляет код STOP на экране для диагностики вместо мгновенной перезагрузки.", Services, WindowsTweakRisk.Safe, Manual,
            RD(@"HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl", "AutoReboot", 0)),
        D("disable_reserved_storage", "Отключить зарезервированное хранилище", "Освобождает Reserved Storage; крупным обновлениям может не хватить места.", Services, WindowsTweakRisk.Caution, Manual,
            "dism.exe /Online /Set-ReservedStorageState /State:Disabled | Out-Null", "dism.exe /Online /Set-ReservedStorageState /State:Enabled | Out-Null")
    };

    private static WindowsTweakDefinition D(
        string id,
        string title,
        string description,
        string category,
        WindowsTweakRisk risk,
        WindowsTweakProfile profiles,
        string apply,
        string restore = "",
        bool requiresWindows11 = false) =>
        new(id, title, description, category, risk, profiles, apply, restore, requiresWindows11);

    private static string A(params string[] commands) => string.Join(Environment.NewLine, commands.Where(x => !string.IsNullOrWhiteSpace(x)));
    private static string Q(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string RD(string path, string name, uint value) => $"Set-DwordBacked '{Q(path)}' '{Q(name)}' {value}";
    private static string RS(string path, string name, string value) => $"Set-StringBacked '{Q(path)}' '{Q(name)}' '{Q(value)}'";
    private static string RB(string path, string name, string bytes) => $"Set-BinaryBacked '{Q(path)}' '{Q(name)}' ([byte[]]({string.Join(',', bytes.Split(',').Select(x => "0x" + x.Trim()))}))";
    private static string RR(string path, string name) => $"Remove-RegValueBacked '{Q(path)}' '{Q(name)}'";
    private static string SD(string name) => $"Disable-ServiceBacked '{Q(name)}'";
    private static string TD(string taskPath) => $"Disable-TaskBacked '{Q(taskPath)}'";
}
