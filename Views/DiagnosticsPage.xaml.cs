using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ColorForge.Models;
using ColorForge.Services;

namespace ColorForge.Views;

public partial class DiagnosticsPage : UserControl, IDisposable
{
    private readonly BenchmarkService _benchmark = new BenchmarkService();
    private readonly SmartAuditService _audit = new SmartAuditService();
    private readonly StutterDoctorService _stutter = new StutterDoctorService();
    private readonly DispatcherTimer _telemetryTimer;
    private HardwareMonitoringService? _hardware;
    private Func<DisplayMode?>? _displayModeProvider;
    private CancellationTokenSource? _benchmarkCancellation;
    private BenchmarkResult? _lastBenchmark;
    private IReadOnlyList<string> _allProcesses = Array.Empty<string>();

    public DiagnosticsPage()
    {
        InitializeComponent();
        _telemetryTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, TelemetryTimer_Tick, Dispatcher);
        Loaded += (_, _) =>
        {
            RefreshProcesses();
            _telemetryTimer.Start();
        };
        Unloaded += (_, _) => _telemetryTimer.Stop();
    }

    public void Initialize(HardwareMonitoringService hardware, Func<DisplayMode?> displayModeProvider)
    {
        _hardware = hardware;
        _displayModeProvider = displayModeProvider;
        UpdateTelemetry();
    }

    private void TelemetryTimer_Tick(object? sender, EventArgs e) => UpdateTelemetry();

    private void UpdateTelemetry()
    {
        if (_hardware == null)
            return;
        HardwareSnapshot snapshot = _hardware.ReadSnapshot();
        CpuTemperatureText.Text = Format(snapshot.CpuTemperatureC, "°C");
        CpuDetailText.Text = $"{snapshot.CpuLoadPercent:0}% · {snapshot.CpuClockMhz:0} MHz";
        GpuTemperatureText.Text = Format(snapshot.GpuTemperatureC, "°C");
        GpuDetailText.Text = $"{snapshot.GpuLoadPercent:0}% · {snapshot.GpuName}";
        GpuClockText.Text = Format(snapshot.GpuCoreClockMhz, "MHz");
        GpuPowerText.Text = snapshot.GpuPowerW.HasValue ? $"{snapshot.GpuPowerW:0} W" : "мощность —";
        VramText.Text = snapshot.GpuMemoryUsedMb.HasValue
            ? $"{snapshot.GpuMemoryUsedMb:0} / {snapshot.GpuMemoryTotalMb:0} MB"
            : "— / — MB";
    }

    private static string Format(double? value, string suffix) => value.HasValue ? $"{value:0} {suffix}" : $"— {suffix}";

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e) => RefreshProcesses();

    private void ProcessSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ProcessSearchHint.Visibility = string.IsNullOrWhiteSpace(ProcessSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyProcessFilter();
    }

    private void RefreshProcesses()
    {
        string? selected = ProcessCombo.SelectedItem?.ToString();
        _allProcesses = _benchmark.GetCandidateProcesses();
        ApplyProcessFilter(selected);
    }

    private void ApplyProcessFilter(string? preferredSelection = null)
    {
        if (ProcessCombo == null || ProcessSearchCountText == null)
            return;

        string query = ProcessSearchBox?.Text.Trim() ?? string.Empty;
        string[] matches = _allProcesses
            .Where(process => string.IsNullOrEmpty(query)
                || process.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string? currentSelection = preferredSelection ?? ProcessCombo.SelectedItem?.ToString();
        ProcessCombo.ItemsSource = matches;
        ProcessCombo.SelectedItem = matches.FirstOrDefault(process =>
            process.Equals(currentSelection, StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();

        ProcessSearchCountText.Text = matches.Length switch
        {
            0 => "не найдено",
            1 => "1 процесс",
            _ => $"{matches.Length} процессов"
        };
    }

    private async void StartBenchmark_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessCombo.SelectedItem is not string process)
        {
            BenchmarkStatusText.Text = "Сначала выбери запущенную игру.";
            return;
        }
        int duration = (DurationCombo.SelectedItem as ComboBoxItem)?.Tag is string tag && int.TryParse(tag, out int value) ? value : 30;
        _benchmarkCancellation?.Dispose();
        _benchmarkCancellation = new CancellationTokenSource();
        StartBenchmarkButton.IsEnabled = false;
        CancelBenchmarkButton.IsEnabled = true;
        BenchmarkProgress.Value = 0;

        try
        {
            Progress<BenchmarkProgress> progress = new Progress<BenchmarkProgress>(p =>
            {
                BenchmarkProgress.Value = p.Percent;
                BenchmarkStatusText.Text = p.Message;
            });
            _lastBenchmark = await _benchmark.RunAsync(process, duration, progress, _benchmarkCancellation.Token);
            ShowBenchmark(_lastBenchmark);
            BenchmarkStatusText.Text = "Тест завершён · CSV и JSON сохранены в LocalAppData\\AURA\\Benchmarks";
        }
        catch (OperationCanceledException)
        {
            BenchmarkStatusText.Text = "Тест отменён.";
        }
        catch (Exception ex)
        {
            AppLogService.Error("Benchmark Lab failed.", ex);
            BenchmarkStatusText.Text = ex.Message;
        }
        finally
        {
            StartBenchmarkButton.IsEnabled = true;
            CancelBenchmarkButton.IsEnabled = false;
        }
    }

    private void CancelBenchmark_Click(object sender, RoutedEventArgs e) => _benchmarkCancellation?.Cancel();

    private void ShowBenchmark(BenchmarkResult result)
    {
        BenchmarkEmptyPanel.Visibility = Visibility.Collapsed;
        BenchmarkResultPanel.Visibility = Visibility.Visible;
        BenchmarkProcessText.Text = result.ProcessName;
        AverageFpsText.Text = result.AverageFps.ToString("0");
        LowFpsText.Text = result.OnePercentLowFps.ToString("0");
        P95Text.Text = result.P95FrameTimeMs.ToString("0.0") + " ms";
        StutterCountText.Text = result.StutterCount.ToString();
    }

    private async void RunAudit_Click(object sender, RoutedEventArgs e)
    {
        if (_hardware == null)
            return;
        AuditItems.ItemsSource = new[] { new AuditItem { Title = "Проверка…", Detail = "Собираем данные Windows и датчиков", Severity = AuditSeverity.Info } };
        try
        {
            AuditItems.ItemsSource = await _audit.RunAsync(_hardware.ReadSnapshot(), _displayModeProvider?.Invoke());
        }
        catch (Exception ex)
        {
            AppLogService.Error("Smart Audit failed.", ex);
            AuditItems.ItemsSource = new[] { new AuditItem { Title = "Проверка не завершена", Detail = ex.Message, Severity = AuditSeverity.Caution } };
        }
    }

    private async void RunStutterScan_Click(object sender, RoutedEventArgs e)
    {
        StutterItems.ItemsSource = new[] { new AuditItem { Title = "Сканирование…", Detail = "Читаем системный журнал за 7 дней", Severity = AuditSeverity.Info } };
        try
        {
            StutterItems.ItemsSource = await _stutter.ScanAsync(_lastBenchmark, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLogService.Error("Stutter Doctor failed.", ex);
            StutterItems.ItemsSource = new[] { new AuditItem { Title = "Сканирование не завершено", Detail = ex.Message, Severity = AuditSeverity.Caution } };
        }
    }

    public void Dispose()
    {
        _telemetryTimer.Stop();
        _benchmarkCancellation?.Cancel();
        _benchmarkCancellation?.Dispose();
    }
}
