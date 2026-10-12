using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ColorForge.Models;
using ColorForge.Services;

namespace ColorForge.Views;

public partial class OverclockPage : UserControl, IDisposable
{
    private readonly DispatcherTimer _telemetryTimer;
    private HardwareMonitoringService? _hardware;
    private NvidiaOverclockService? _gpuTune;
    private CpuAutoTuneService? _cpuTune;
    private readonly PawnIoInstallerService _pawnIoInstaller = new PawnIoInstallerService();
    private AppSettings? _settings;
    private SettingsStore? _store;
    private AutoTuneTarget _target = AutoTuneTarget.GraphicsCard;
    private CancellationTokenSource? _tuneCancellation;
    private bool _installingPawnIo;
    private bool _initializing = true;

    public OverclockPage()
    {
        InitializeComponent();
        _telemetryTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateTelemetry(), Dispatcher);
        Loaded += (_, _) => { _telemetryTimer.Start(); UpdateSupport(); };
        Unloaded += (_, _) => _telemetryTimer.Stop();
        SetSelectedTab();
    }

    public void Initialize(HardwareMonitoringService hardware, AppSettings settings, SettingsStore store)
    {
        _hardware = hardware;
        _settings = settings;
        _store = store;
        _gpuTune = new NvidiaOverclockService(hardware);
        _cpuTune = new CpuAutoTuneService(hardware);
        _initializing = false;
        UpdateSupport();
        UpdateTelemetry();
    }

    public void ApplySavedProfilesAtStartup()
    {
        if (_settings == null || _gpuTune == null || _cpuTune == null)
            return;

        string? warning = null;
        try
        {
            if (_settings.Overclock.ApplyLearnedGpuOnStart && _settings.Overclock.LearnedGpuCoreOffsetMhz > 0)
                _gpuTune.ApplyOffset(_settings.Overclock.LearnedGpuCoreOffsetMhz);
        }
        catch (Exception ex)
        {
            AppLogService.Error("GPU startup profile could not be applied.", ex);
            warning = "GPU: " + ex.Message;
        }

        try
        {
            if (_settings.Overclock.ApplyCpuProfileOnStart && !_settings.Overclock.CpuProfileId.Equals("stock", StringComparison.OrdinalIgnoreCase))
                _cpuTune.ApplySavedProfile(_settings.Overclock.CpuProfileId);
        }
        catch (Exception ex)
        {
            AppLogService.Error("CPU startup profile could not be applied.", ex);
            warning = warning == null ? "CPU: " + ex.Message : warning + " · CPU: " + ex.Message;
        }

        if (warning != null)
            TuneProgressText.Text = "Автоприменение пропущено · " + warning;
    }

    private void GpuTab_Click(object sender, RoutedEventArgs e)
    {
        if (_tuneCancellation != null)
            return;
        _target = AutoTuneTarget.GraphicsCard;
        SetSelectedTab();
        UpdateSupport();
        UpdateTelemetry();
    }

    private void CpuTab_Click(object sender, RoutedEventArgs e)
    {
        if (_tuneCancellation != null)
            return;
        _target = AutoTuneTarget.Processor;
        SetSelectedTab();
        UpdateSupport();
        UpdateTelemetry();
    }

    private void SetSelectedTab()
    {
        bool gpu = _target == AutoTuneTarget.GraphicsCard;
        GpuTabButton.Background = new SolidColorBrush(gpu ? Color.FromRgb(91, 48, 133) : Color.FromRgb(41, 38, 47));
        GpuTabButton.BorderBrush = new SolidColorBrush(gpu ? Color.FromRgb(150, 82, 216) : Color.FromRgb(58, 54, 66));
        CpuTabButton.Background = new SolidColorBrush(!gpu ? Color.FromRgb(91, 48, 133) : Color.FromRgb(41, 38, 47));
        CpuTabButton.BorderBrush = new SolidColorBrush(!gpu ? Color.FromRgb(150, 82, 216) : Color.FromRgb(58, 54, 66));
        TuneDescriptionText.Text = gpu
            ? "AURA определяет доступный P0-диапазон NVIDIA через NVAPI, повышает только частоту ядра шагами по 15 MHz и сохраняет последнее устойчивое значение с запасом."
            : "AURA сама выбирает совместимый адаптер: AMD PBO/SMU либо Intel Turbo Power. Память, ручное напряжение и фиксированный множитель не изменяются.";
        ResultEmptyPanel.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;
    }

    private void UpdateSupport()
    {
        if (_settings == null || _gpuTune == null || _cpuTune == null)
            return;
        _initializing = true;
        if (_target == AutoTuneTarget.GraphicsCard)
        {
            GpuTuneSupport support = _gpuTune.ProbeSupport();
            HardwareNameText.Text = support.GpuName;
            SupportTitleText.Text = support.IsSupported ? "Готово к NVAPI Auto Tune" : "Разгон GPU недоступен";
            SupportDetailText.Text = support.Detail;
            StartTuneButton.IsEnabled = support.IsSupported;
            ApplyOnStartCheck.IsChecked = _settings.Overclock.ApplyLearnedGpuOnStart;
            OcProfileText.Text = _settings.Overclock.LearnedGpuCoreOffsetMhz == 0 ? "Stock" : $"+{_settings.Overclock.LearnedGpuCoreOffsetMhz}";
        }
        else
        {
            CpuTuneSupport support = _cpuTune.ProbeSupport();
            HardwareNameText.Text = support.ProcessorName;
            SupportTitleText.Text = support.IsSupported
                ? support.Backend == CpuTuneBackend.AmdPbo ? "Готово к AMD PBO Auto Tune" : "Готово к Intel Turbo Tune"
                : !_pawnIoInstaller.IsInstalled ? "PawnIO установится при запуске теста" : "Разгон CPU недоступен";
            SupportDetailText.Text = !_pawnIoInstaller.IsInstalled
                ? $"Официальный PawnIO {PawnIoInstallerService.BundledVersion} встроен в AURA. Нажми общую кнопку — Windows один раз запросит UAC, затем AURA повторит проверку CPU и начнёт тест."
                : support.Detail;
            StartTuneButton.IsEnabled = support.IsSupported || !_pawnIoInstaller.IsInstalled;
            ApplyOnStartCheck.IsChecked = _settings.Overclock.ApplyCpuProfileOnStart;
            OcProfileText.Text = ProfileLabel(_settings.Overclock.CpuProfileId);
        }
        SupportTitleText.Foreground = new SolidColorBrush(StartTuneButton.IsEnabled ? Color.FromRgb(101, 223, 154) : Color.FromRgb(255, 169, 92));
        _initializing = false;
    }

    private void UpdateTelemetry()
    {
        if (_hardware == null)
            return;
        bool gpu = _target == AutoTuneTarget.GraphicsCard;
        HardwareSnapshot snapshot = _hardware.ReadSnapshot(gpu ? _gpuTune?.ActiveGpuName : null);
        if (_tuneCancellation == null)
            HardwareNameText.Text = gpu ? snapshot.GpuName : snapshot.CpuName;
        OcTemperatureText.Text = Value(gpu ? snapshot.GpuTemperatureC : snapshot.CpuTemperatureC, "°C");
        OcLoadText.Text = Value(gpu ? snapshot.GpuLoadPercent : snapshot.CpuLoadPercent, "%");
        OcCoreClockText.Text = Value(gpu ? snapshot.GpuCoreClockMhz : snapshot.CpuClockMhz, "MHz");
        OcMemoryClockText.Text = gpu ? Value(snapshot.GpuMemoryClockMhz, "MHz") : "не меняется";
        OcPowerText.Text = Value(gpu ? snapshot.GpuPowerW : snapshot.CpuPowerW, "W");
    }

    private static string Value(double? value, string suffix) => value.HasValue ? $"{value:0} {suffix}" : $"— {suffix}";

    private async void StartTune_Click(object sender, RoutedEventArgs e)
    {
        if (_gpuTune == null || _cpuTune == null || _hardware == null || _settings == null || _store == null)
            return;

        string[] conflicts = FindConflictingProcesses();
        if (conflicts.Length > 0)
        {
            MessageBox.Show(
                "Перед тестом закрой: " + string.Join(", ", conflicts) + ".\n\nAURA не запускает аппаратный тест рядом с игрой или античитом.",
                "AURA · Закрой приложения",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        bool needsPawnIo = _target == AutoTuneTarget.Processor && !_pawnIoInstaller.IsInstalled;
        CpuTuneSupport? cpuSupport = _target == AutoTuneTarget.Processor && !needsPawnIo ? _cpuTune.ProbeSupport() : null;
        MessageBoxResult confirmation = MessageBox.Show(
            _target == AutoTuneTarget.GraphicsCard
                ? "AURA будет временно менять доступное смещение ядра NVIDIA и нагружать GPU. Возможны краткое зависание изображения или перезапуск драйвера. Память, напряжение и power limit не изменяются.\n\nНачать?"
                : needsPawnIo
                    ? $"AURA сначала установит встроенный официальный PawnIO {PawnIoInstallerService.BundledVersion} в подписанном ограниченном режиме. Windows покажет UAC. Затем AURA определит совместимый AMD PBO или Intel Turbo-адаптер и запустит нагрузочный тест. Если Windows потребует перезагрузку, тест продолжится после неё повторным нажатием.\n\nПродолжить?"
                    : CpuConfirmationText(cpuSupport),
            "AURA · Smart Auto Overclock",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        _tuneCancellation = new CancellationTokenSource();
        _installingPawnIo = needsPawnIo;
        SetBusy(true);
        ResultEmptyPanel.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;
        try
        {
            if (needsPawnIo)
            {
                TuneProgress.Value = 2;
                TuneProgressText.Text = $"Проверка PawnIO {PawnIoInstallerService.BundledVersion} · ожидание UAC…";
                PawnIoInstallResult install = await _pawnIoInstaller.EnsureInstalledAsync(CancellationToken.None);
                if (install.RebootRequired)
                {
                    TuneProgress.Value = 5;
                    TuneProgressText.Text = "PawnIO установлен · нужна перезагрузка Windows, затем нажми эту же кнопку ещё раз.";
                    MessageBox.Show(
                        "Официальный PawnIO установлен. Windows должна перезагрузиться, чтобы драйвер стал доступен. После перезагрузки открой AURA и снова нажми «Проверить и запустить авторазгон».",
                        "AURA · Нужна перезагрузка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                cpuSupport = _cpuTune.ProbeSupport();
                if (!cpuSupport.IsSupported)
                    throw new InvalidOperationException(cpuSupport.Detail);

                _installingPawnIo = false;
                CancelTuneButton.Visibility = Visibility.Visible;
            }

            Progress<AutoTuneProgress> progress = new Progress<AutoTuneProgress>(p =>
            {
                TuneProgress.Value = p.Percent;
                TuneProgressText.Text = $"{p.Stage} · {p.Detail}";
                UpdateProgressTelemetry(p.Snapshot);
            });
            AutoTuneResult result = _target == AutoTuneTarget.GraphicsCard
                ? await _gpuTune.TuneAsync(progress, _tuneCancellation.Token)
                : await _cpuTune.TuneAsync(progress, _tuneCancellation.Token);

            if (result.Target == AutoTuneTarget.GraphicsCard)
            {
                _settings.Overclock.LearnedGpuCoreOffsetMhz = result.GpuCoreOffsetMhz;
                _settings.Overclock.ApplyLearnedGpuOnStart = result.GpuCoreOffsetMhz > 0;
            }
            else
            {
                _settings.Overclock.CpuProfileId = result.CpuProfileId;
                _settings.Overclock.CpuBackendId = result.CpuBackendId;
                _settings.Overclock.CpuPptWatts = result.CpuPptWatts;
                _settings.Overclock.CpuTdcAmps = result.CpuTdcAmps;
                _settings.Overclock.CpuEdcAmps = result.CpuEdcAmps;
                _settings.Overclock.ApplyCpuProfileOnStart = !result.CpuProfileId.Equals("stock", StringComparison.OrdinalIgnoreCase);
            }
            _initializing = true;
            ApplyOnStartCheck.IsChecked = result.Target == AutoTuneTarget.GraphicsCard
                ? _settings.Overclock.ApplyLearnedGpuOnStart
                : _settings.Overclock.ApplyCpuProfileOnStart;
            _initializing = false;
            _store.Save(_settings);
            ShowResult(result);
            TuneProgressText.Text = "Готово · профиль сохранён и будет применяться при запуске AURA.";
        }
        catch (OperationCanceledException)
        {
            AppLogService.Info($"Auto tune cancelled for {_target}; stock values requested.");
            TuneProgressText.Text = "Тест остановлен · возвращён Stock.";
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Auto tune failed for {_target}.", ex);
            TuneProgressText.Text = "Ошибка · " + ex.Message;
            MessageBox.Show(ex.Message, "AURA · Smart Tune", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _installingPawnIo = false;
            _tuneCancellation.Dispose();
            _tuneCancellation = null;
            SetBusy(false);
            UpdateSupport();
        }
    }

    private void UpdateProgressTelemetry(HardwareSnapshot snapshot)
    {
        bool gpu = _target == AutoTuneTarget.GraphicsCard;
        OcTemperatureText.Text = Value(gpu ? snapshot.GpuTemperatureC : snapshot.CpuTemperatureC, "°C");
        OcLoadText.Text = Value(gpu ? snapshot.GpuLoadPercent : snapshot.CpuLoadPercent, "%");
        OcCoreClockText.Text = Value(gpu ? snapshot.GpuCoreClockMhz : snapshot.CpuClockMhz, "MHz");
        OcPowerText.Text = Value(gpu ? snapshot.GpuPowerW : snapshot.CpuPowerW, "W");
    }

    private void ShowResult(AutoTuneResult result)
    {
        ResultEmptyPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ResultTitleText.Text = result.Title;
        ResultDetailText.Text = result.Detail;
        ResultTemperatureText.Text = result.PeakTemperatureC > 0 ? $"{result.PeakTemperatureC:0} °C" : "датчик —";
        ResultGainText.Text = $"≈ {result.ImprovementPercent:0.0}%";
        OcProfileText.Text = result.Target == AutoTuneTarget.GraphicsCard
            ? (result.GpuCoreOffsetMhz == 0 ? "Stock" : $"+{result.GpuCoreOffsetMhz}")
            : ProfileLabel(result.CpuProfileId);
    }

    private void CancelTune_Click(object sender, RoutedEventArgs e)
    {
        if (!_installingPawnIo)
            _tuneCancellation?.Cancel();
    }

    private void ForgetLimits_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null || _store == null || _gpuTune == null || _cpuTune == null)
            return;
        try
        {
            if (_target == AutoTuneTarget.GraphicsCard)
            {
                _gpuTune.Reset();
                _settings.Overclock.LearnedGpuCoreOffsetMhz = 0;
                _settings.Overclock.ApplyLearnedGpuOnStart = false;
            }
            else
            {
                _cpuTune.ResetToStock();
                _settings.Overclock.CpuProfileId = "stock";
                _settings.Overclock.CpuBackendId = "none";
                _settings.Overclock.CpuPptWatts = 0;
                _settings.Overclock.CpuTdcAmps = 0;
                _settings.Overclock.CpuEdcAmps = 0;
                _settings.Overclock.ApplyCpuProfileOnStart = false;
            }
            _store.Save(_settings);
            TuneProgress.Value = 0;
            TuneProgressText.Text = "Найденные пределы удалены · активен Stock.";
            UpdateSupport();
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Could not forget {_target} learned limits.", ex);
            TuneProgressText.Text = ex.Message;
        }
    }

    private void ApplyOnStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings == null || _store == null)
            return;
        bool enabled = ApplyOnStartCheck.IsChecked == true;
        if (_target == AutoTuneTarget.GraphicsCard)
            _settings.Overclock.ApplyLearnedGpuOnStart = enabled && _settings.Overclock.LearnedGpuCoreOffsetMhz > 0;
        else
            _settings.Overclock.ApplyCpuProfileOnStart = enabled && !_settings.Overclock.CpuProfileId.Equals("stock", StringComparison.OrdinalIgnoreCase);
        _store.Save(_settings);
        UpdateSupport();
    }

    private void SetBusy(bool busy)
    {
        StartTuneButton.IsEnabled = !busy;
        GpuTabButton.IsEnabled = !busy;
        CpuTabButton.IsEnabled = !busy;
        CancelTuneButton.Visibility = busy && !_installingPawnIo ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string[] FindConflictingProcesses()
    {
        string[] names = { "cs2", "dota2", "RustClient", "FACEIT", "FACEITClient", "faceitservice", "EasyAntiCheat", "EasyAntiCheat_EOS", "vgc" };
        return Process.GetProcesses()
            .Select(process =>
            {
                try { return process.ProcessName; }
                catch { return string.Empty; }
                finally { process.Dispose(); }
            })
            .Where(name => names.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string CpuConfirmationText(CpuTuneSupport? support) => support?.Backend switch
    {
        CpuTuneBackend.AmdPbo =>
            "AURA временно изменит поддерживаемые PBO-лимиты AMD и нагрузит все потоки. Ручное напряжение, множитель и память не изменяются. После перезагрузки параметры вернутся к значениям BIOS.\n\nНачать?",
        CpuTuneBackend.IntelTurboPower =>
            "AURA временно увеличит доступные Intel PL1/PL2 в пределах, объявленных процессором, и нагрузит все потоки. Это оптимизация штатного Turbo, а не разблокировка множителя. Напряжение и память не изменяются.\n\nНачать?",
        _ => "Для этого процессора не найден совместимый безопасный аппаратный адаптер."
    };

    private static string ProfileLabel(string id) => id.ToLowerInvariant() switch
    {
        "low" => "Low",
        "medium" => "Medium",
        "ultra" => "Ultra",
        _ => "Stock"
    };

    public void Dispose()
    {
        _telemetryTimer.Stop();
        _tuneCancellation?.Cancel();
        _tuneCancellation?.Dispose();
    }
}
