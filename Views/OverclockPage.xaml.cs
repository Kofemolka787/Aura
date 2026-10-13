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
    private TaskCompletionSource? _operationCompletion;
    private Exception? _rollbackFailure;
    private TaskCompletionSource? _startupCompletion;
    private bool _startingHardware, _disposed;
    public bool IsBusy => _operationCompletion != null || _startingHardware;
    public async Task StopAsync()
    {
        _tuneCancellation?.Cancel();
        if (_startupCompletion != null) await _startupCompletion.Task;
        Task? completion = _operationCompletion?.Task;
        if (completion != null) await completion;
        if (_rollbackFailure != null) throw new InvalidOperationException("Аппаратный возврат не подтверждён. Выполни возврат исходных параметров или перезагрузи Windows.", _rollbackFailure);
    }
    private bool _installingPawnIo;
    private bool _initializing = true;
    private bool _readingTelemetry;

    public OverclockPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshTuningHistory();
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
        ExtendedTestCheck.IsChecked = settings.Overclock.ExtendedTest;
        _initializing = false;
        UpdateSupport();
        UpdateTelemetry();
    }

    public async void ApplySavedProfilesAtStartup()
    {
        if (_settings == null || _gpuTune == null || _cpuTune == null)
            return;

        _startingHardware = true;
        _startupCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SetBusy(true);
        try {
        string? warning = await Task.Run(() => {
        string? warning = null;
        try
        {
            bool canApply = RecoverStartup(_gpuTune.RecoverPendingChanges, "GPU", message => warning = message);
            if (canApply && _settings.Overclock.ApplyLearnedGpuOnStart && _settings.Overclock.LearnedGpuCoreOffsetMhz > 0)
                _gpuTune.ApplySavedProfile(_settings.Overclock);
        }
        catch (Exception ex)
        {
            AppLogService.Error("GPU startup profile could not be applied.", ex);
            if (ex is AggregateException) _rollbackFailure = ex;
            warning = "GPU: " + ex.Message;
        }

        try
        {
            bool canApply = RecoverStartup(_cpuTune.RecoverPendingChanges, "CPU", message => warning = warning == null ? message : warning + " · " + message);
            if (canApply && _settings.Overclock.ApplyCpuProfileOnStart && !_settings.Overclock.CpuProfileId.Equals("stock", StringComparison.OrdinalIgnoreCase))
                _cpuTune.ApplySavedProfile(_settings.Overclock);
        }
        catch (Exception ex)
        {
            AppLogService.Error("CPU startup profile could not be applied.", ex);
            if (ex is AggregateException) _rollbackFailure = ex;
            warning = warning == null ? "CPU: " + ex.Message : warning + " · CPU: " + ex.Message;
        }

        return warning;
        });
        if (warning != null) TuneProgressText.Text = "Автоприменение пропущено · " + warning;
        }
        finally {
            _startingHardware = false;
            try { SetBusy(false); UpdateSupport(); }
            finally { _startupCompletion?.TrySetResult(); _startupCompletion = null; }
        }
    }

    private bool RecoverStartup(Func<bool> recover, string label, Action<string> report)
    {
        try {
            if (!recover()) return true;
            report(label + ": незавершённый тест восстановлен; автоприменение пропущено.");
            return false;
        }
        catch (Exception exception) {
            _rollbackFailure = exception;
            AppLogService.Error(label + " startup recovery failed; snapshot retained.", exception);
            report(label + ": возврат не подтверждён — " + exception.Message);
            return false;
        }
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
            : "AURA проверяет доступные Intel PL1/PL2. Запись AMD PBO отключена: исходные лимиты нельзя надёжно прочитать. Память, ручное напряжение и фиксированный множитель не изменяются.";
        ResultEmptyPanel.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;
    }

    private bool _probingSupport;
    private async void UpdateSupport()
    {
        if (_settings == null || _gpuTune == null || _cpuTune == null || _probingSupport || IsBusy || _disposed) return;
        _probingSupport = true;
        var selectedTarget = _target;
        try {
        GpuTuneSupport? gpuSupport = null; CpuTuneSupport? cpuSupport = null;
        if (selectedTarget == AutoTuneTarget.GraphicsCard) gpuSupport = await Task.Run(_gpuTune.ProbeSupport);
        else cpuSupport = await Task.Run(_cpuTune.ProbeSupport);
        if (_target != selectedTarget || IsBusy || _disposed) return;
        _initializing = true;
        if (_target == AutoTuneTarget.GraphicsCard)
        {
            GpuTuneSupport support = gpuSupport!;
            HardwareNameText.Text = support.GpuName;
            SupportTitleText.Text = support.IsSupported ? "Готово к NVAPI Auto Tune" : "Разгон GPU недоступен";
            SupportDetailText.Text = support.Detail;
            StartTuneButton.IsEnabled = support.IsSupported;
            ApplyOnStartCheck.IsChecked = _settings.Overclock.ApplyLearnedGpuOnStart;
            OcProfileText.Text = _settings.Overclock.LearnedGpuCoreOffsetMhz == 0 ? "Stock" : $"+{_settings.Overclock.LearnedGpuCoreOffsetMhz}";
        }
        else
        {
            CpuTuneSupport support = cpuSupport!;
            HardwareNameText.Text = support.ProcessorName;
            SupportTitleText.Text = support.IsSupported
                ? "Готово к Intel Turbo Tune"
                : !_pawnIoInstaller.IsInstalled && support.TelemetryReady && support.ProcessorName.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "PawnIO установится при запуске теста" : "Разгон CPU недоступен";
            SupportDetailText.Text = !_pawnIoInstaller.IsInstalled && support.TelemetryReady && support.ProcessorName.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                ? $"Официальный PawnIO {PawnIoInstallerService.BundledVersion} встроен в AURA. Нажми общую кнопку — Windows один раз запросит UAC, затем AURA повторит проверку CPU и начнёт тест."
                : support.Detail;
            StartTuneButton.IsEnabled = support.IsSupported || (!_pawnIoInstaller.IsInstalled && support.TelemetryReady && support.ProcessorName.Contains("Intel", StringComparison.OrdinalIgnoreCase));
            ApplyOnStartCheck.IsChecked = _settings.Overclock.ApplyCpuProfileOnStart;
            OcProfileText.Text = ProfileLabel(_settings.Overclock.CpuProfileId);
        }
        SupportTitleText.Foreground = new SolidColorBrush(StartTuneButton.IsEnabled ? Color.FromRgb(101, 223, 154) : Color.FromRgb(255, 169, 92));
        _initializing = false;
        }
        catch (Exception exception) { AppLogService.Error("Hardware support probe failed.", exception); SupportDetailText.Text = exception.Message; StartTuneButton.IsEnabled = false; }
        finally { _initializing = false; _probingSupport = false; if (_target != selectedTarget && !_disposed) UpdateSupport(); }
    }

    private async void UpdateTelemetry()
    {
        if (_hardware == null || _readingTelemetry || !IsVisible || _disposed)
            return;
        _readingTelemetry = true;
        bool gpu = _target == AutoTuneTarget.GraphicsCard;
        HardwareSnapshot snapshot;
        try { snapshot = await _hardware.ReadSnapshotAsync(gpu ? _gpuTune?.TelemetrySelector : null); }
        finally { _readingTelemetry = false; }
        if (_disposed || !IsVisible || gpu != (_target == AutoTuneTarget.GraphicsCard)) return;
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
        if (IsBusy || _disposed || _gpuTune == null || _cpuTune == null || _hardware == null || _settings == null || _store == null)
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

        bool needsPawnIo = _target == AutoTuneTarget.Processor && !_pawnIoInstaller.IsInstalled
            && _cpuTune.ProbeSupport().ProcessorName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        CpuTuneSupport? cpuSupport = _target == AutoTuneTarget.Processor && !needsPawnIo ? _cpuTune.ProbeSupport() : null;
        MessageBoxResult confirmation = MessageBox.Show(
            _target == AutoTuneTarget.GraphicsCard
                ? "AURA будет временно менять доступное смещение ядра NVIDIA и нагружать GPU. Возможны краткое зависание изображения или перезапуск драйвера. Память, напряжение и power limit не изменяются.\n\nНачать?"
                : needsPawnIo
                    ? $"AURA сначала установит встроенный официальный PawnIO {PawnIoInstallerService.BundledVersion} в подписанном ограниченном режиме. Windows покажет UAC. Затем AURA проверит доступность Intel PL1/PL2 и запустит нагрузочный тест. Если Windows потребует перезагрузку, тест продолжится после неё повторным нажатием.\n\nПродолжить?"
                    : CpuConfirmationText(cpuSupport),
            "AURA · Smart Auto Overclock",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        _tuneCancellation = new CancellationTokenSource();
        _operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gpuTune.ExtendedTest = _cpuTune.ExtendedTest = _settings.Overclock.ExtendedTest;
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
                _settings.Overclock.LearnedGpuCoreOffsetKhz = result.GpuCoreOffsetKhz;
                _settings.Overclock.GpuDeviceId = result.GpuDeviceId;
            }
            else
            {
                _settings.Overclock.CpuProfileId = result.CpuProfileId;
                _settings.Overclock.CpuBackendId = result.CpuBackendId;
                _settings.Overclock.CpuPptWatts = result.CpuPptWatts;
                _settings.Overclock.CpuTdcAmps = result.CpuTdcAmps;
                _settings.Overclock.CpuEdcAmps = result.CpuEdcAmps;
                _settings.Overclock.CpuDeviceId = result.CpuDeviceId;
                _settings.Overclock.CpuPl1Watts = result.CpuPl1Watts;
                _settings.Overclock.CpuPl2Watts = result.CpuPl2Watts;
            }
            _initializing = true;
            ApplyOnStartCheck.IsChecked = result.Target == AutoTuneTarget.GraphicsCard
                ? _settings.Overclock.ApplyLearnedGpuOnStart
                : _settings.Overclock.ApplyCpuProfileOnStart;
            _initializing = false;
            _store.Save(_settings);
            new TuningHistoryStore().Save(result);
            RefreshTuningHistory();
            ShowResult(result);
            TuneProgressText.Text = "Готово · профиль сохранён. Автоприменение выбирается переключателем.";
        }
        catch (OperationCanceledException)
        {
            AppLogService.Info($"Auto tune cancelled for {_target}; stock values requested.");
            TuneProgressText.Text = "Тест остановлен · возврат исходных параметров подтверждён.";
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Auto tune failed for {_target}.", ex);
            if (ex is AggregateException) _rollbackFailure = ex;
            TuneProgressText.Text = "Ошибка · " + ex.Message;
            MessageBox.Show(ex.Message, "AURA · Smart Tune", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _installingPawnIo = false;
            _tuneCancellation.Dispose();
            _tuneCancellation = null;
            SetBusy(false);
            try { UpdateSupport(); }
            finally { _operationCompletion?.TrySetResult(); _operationCompletion = null; }
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

    private void RefreshTuningHistory() => TuningHistoryCombo.ItemsSource = new TuningHistoryStore().Load();
    private void TuningHistory_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TuningHistoryCombo.SelectedItem is TuningRecord record) { ShowResult(record.Result); TuneProgressText.Text = "Сохранённый результат · не применяется при просмотре."; }
    }

    private void ShowResult(AutoTuneResult result)
    {
        ResultEmptyPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ResultTitleText.Text = result.Title;
        ResultDetailText.Text = result.Detail;
        ResultTemperatureText.Text = result.PeakTemperatureC > 0 ? $"{result.PeakTemperatureC:0} °C" : "датчик —";
        ResultGainText.Text = result.Target == AutoTuneTarget.GraphicsCard ? "Измерь FPS" : $"{result.ImprovementPercent:0.0}% · CPU";
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
                _settings.Overclock.LearnedGpuCoreOffsetKhz = 0;
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
            _rollbackFailure = null;
            TuneProgressText.Text = "Найденные пределы удалены · исходные параметры восстановлены.";
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
        ForgetLimitsButton.IsEnabled = !busy;
        ApplyOnStartCheck.IsEnabled = !busy;
        ExtendedTestCheck.IsEnabled = !busy;
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

    private void ExtendedTest_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings == null || _store == null) return;
        _settings.Overclock.ExtendedTest = ExtendedTestCheck.IsChecked == true;
        _store.Save(_settings);
    }

    public void Dispose()
    {
        _disposed = true;
        _telemetryTimer.Stop();
        _tuneCancellation?.Cancel();
        if (!IsBusy) _tuneCancellation?.Dispose();
    }
}
