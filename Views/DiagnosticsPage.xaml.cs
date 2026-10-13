using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
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
    private TaskCompletionSource? _benchmarkCompletion;
    private TaskCompletionSource? _auditCompletion, _stutterCompletion;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _stopping;
    public async Task StopAsync()
    {
        _stopping = true; _telemetryTimer.Stop(); _lifetime.Cancel();
        _benchmarkCancellation?.Cancel();
        var tasks = new[] { _benchmarkCompletion?.Task, _auditCompletion?.Task, _stutterCompletion?.Task }.Where(x => x != null).Cast<Task>();
        await Task.WhenAll(tasks);
    }
    private BenchmarkResult? _lastBenchmark;
    private IReadOnlyList<string> _allProcesses = Array.Empty<string>();
    private bool _updatingStreams;
    private bool _readingTelemetry;
    private IReadOnlyList<AuditItem> _lastAudit = Array.Empty<AuditItem>();
    private IReadOnlyList<AuditItem> _lastStutter = Array.Empty<AuditItem>();

    public DiagnosticsPage()
    {
        InitializeComponent();
        _telemetryTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, TelemetryTimer_Tick, Dispatcher);
        Loaded += (_, _) =>
        {
            RefreshProcesses();
            RefreshHistory();
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

    private async void UpdateTelemetry()
    {
        if (_hardware == null || _readingTelemetry || !IsVisible || _stopping)
            return;
        _readingTelemetry = true;
        HardwareSnapshot snapshot;
        try { snapshot = await _hardware.ReadSnapshotAsync(); }
        finally { _readingTelemetry = false; }
        if (!IsVisible || _stopping) return;
        TelemetrySourceText.Text = $"LibreHardwareMonitor · {snapshot.TimestampUtc.ToLocalTime():HH:mm:ss} · " + (snapshot.TelemetryValid ? "прочитано" : "нет достоверных данных");
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
        if (_benchmarkCompletion != null || _stopping) return;
        if (ProcessCombo.SelectedItem is not string process)
        {
            BenchmarkStatusText.Text = "Сначала выбери запущенную игру.";
            return;
        }
        int duration = (DurationCombo.SelectedItem as ComboBoxItem)?.Tag is string tag && int.TryParse(tag, out int value) ? value : 30;
        _benchmarkCancellation?.Dispose();
        _benchmarkCancellation = new CancellationTokenSource();
        _benchmarkCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            RefreshHistory();
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
            _benchmarkCompletion?.TrySetResult();
            _benchmarkCompletion = null;
        }
    }

    private void CancelBenchmark_Click(object sender, RoutedEventArgs e) => _benchmarkCancellation?.Cancel();

    private void ShowBenchmark(BenchmarkResult result)
    {
        _lastBenchmark = result;
        _updatingStreams = true;
        FrameStreamCombo.ItemsSource = result.Streams;
        FrameStreamCombo.SelectedItem = result.Streams.FirstOrDefault(x => x.Id == result.StreamId);
        _updatingStreams = false;
        BenchmarkEmptyPanel.Visibility = Visibility.Collapsed;
        BenchmarkResultPanel.Visibility = Visibility.Visible;
        BenchmarkProcessText.Text = result.ProcessName;
        AverageFpsText.Text = result.AverageFps.ToString("0");
        LowFpsText.Text = result.OnePercentLowFps.ToString("0");
        P95Text.Text = result.P95FrameTimeMs.ToString("0.0") + " ms";
        StutterCountText.Text = result.StutterCount.ToString();
        DrawGraph();
    }

    private void RefreshHistory()
    {
        var history = new BenchmarkHistoryStore(_benchmark.OutputDirectory).Load();
        HistoryCombo.ItemsSource = history;
        CompareBeforeCombo.ItemsSource = history;
        CompareAfterCombo.ItemsSource = history;
        if (history.Count > 0) CompareAfterCombo.SelectedIndex = 0;
        if (history.Count > 1) CompareBeforeCombo.SelectedIndex = 1;
    }

    private void History_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryCombo.SelectedItem is BenchmarkResult result) ShowBenchmark(result);
    }

    private void FrameStream_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingStreams || _lastBenchmark == null || FrameStreamCombo.SelectedItem is not FrameStreamChoice choice) return;
        try { ShowBenchmark(BenchmarkService.Parse(_lastBenchmark.CsvPath, _lastBenchmark.ProcessName, choice.Id, _lastBenchmark.RecordedAt)); }
        catch (Exception exception) { BenchmarkStatusText.Text = exception.Message; }
    }

    private void FrameGraph_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();
    private void DrawGraph()
    {
        if (_lastBenchmark?.FrameTimesMs == null || _lastBenchmark.FrameTimesMs.Length == 0 || FrameGraph.ActualWidth <= 0) return;
        double[] frames = _lastBenchmark.FrameTimesMs;
        double maximum = Math.Max(1, frames.Max());
        int stride = Math.Max(1, (int)Math.Ceiling(frames.Length / 1000d));
        PointCollection points = new();
        for (int index = 0; index < frames.Length; index += stride)
        {
            double peak = frames.Skip(index).Take(stride).Max();
            points.Add(new Point(index / (double)Math.Max(1, frames.Length - 1) * FrameGraph.ActualWidth,
                FrameGraph.ActualHeight * (1 - peak / maximum)));
        }
        FrameLine.Points = points;
        FrameGraphScale.Text = $"Максимум {maximum:0.0} ms · {_lastBenchmark.FrameCount} кадров · не показано {_lastBenchmark.DroppedFrameCount} · сгенерировано {_lastBenchmark.GeneratedFrameCount} · неверных строк {_lastBenchmark.InvalidFrameCount} · {_lastBenchmark.Metric}";
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        ComparisonText.Text = CompareBeforeCombo.SelectedItem is BenchmarkResult before && CompareAfterCombo.SelectedItem is BenchmarkResult after
            ? BenchmarkHistoryStore.Compare(before, after) : "Выбери два замера одной игры.";
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.SaveFileDialog dialog = new() { Filter = "Диагностический отчёт JSON|*.json", FileName = "AURA-diagnostics.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var report = new
            {
                CreatedAtUtc = DateTime.UtcNow, Version = typeof(DiagnosticsPage).Assembly.GetName().Version?.ToString(),
                Windows = Environment.OSVersion.VersionString, Hardware = SafeHardware(_hardware?.ReadSnapshot()),
                Audit = _lastAudit, SystemEvents = _lastStutter,
                Benchmarks = new BenchmarkHistoryStore(_benchmark.OutputDirectory).Load().Select(x => new
                { x.RecordedAt, x.ProcessName, x.FrameCount, x.AverageFps, x.OnePercentLowFps, x.P95FrameTimeMs, x.P99FrameTimeMs, x.StutterCount, x.Metric })
            };
            AtomicFile.Write(dialog.FileName, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }));
            ComparisonText.Text = "Отчёт сохранён. Данные входа и лицензионные токены не включены.";
        }
        catch (Exception exception) { ComparisonText.Text = "Экспорт: " + exception.Message; }
    }

    private async void RunAudit_Click(object sender, RoutedEventArgs e)
    {
        if (_hardware == null || _auditCompletion != null || _stopping) return;
        _auditCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AuditItems.ItemsSource = new[] { new AuditItem { Title = "Проверка…", Detail = "Собираем данные Windows и датчиков", Severity = AuditSeverity.Info } };
        try
        {
            _lastAudit = await _audit.RunAsync(await _hardware.ReadSnapshotAsync(), _displayModeProvider?.Invoke(), _lifetime.Token);
            AuditItems.ItemsSource = _lastAudit;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogService.Error("Smart Audit failed.", ex);
            AuditItems.ItemsSource = new[] { new AuditItem { Title = "Проверка не завершена", Detail = ex.Message, Severity = AuditSeverity.Caution } };
        }
        finally { _auditCompletion?.TrySetResult(); _auditCompletion = null; }
    }

    private async void RunStutterScan_Click(object sender, RoutedEventArgs e)
    {
        if (_stutterCompletion != null || _stopping) return;
        _stutterCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StutterItems.ItemsSource = new[] { new AuditItem { Title = "Сканирование…", Detail = "Читаем системный журнал за 7 дней", Severity = AuditSeverity.Info } };
        try
        {
            _lastStutter = await _stutter.ScanAsync(_lastBenchmark, _lifetime.Token);
            StutterItems.ItemsSource = _lastStutter;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogService.Error("Stutter Doctor failed.", ex);
            StutterItems.ItemsSource = new[] { new AuditItem { Title = "Сканирование не завершено", Detail = ex.Message, Severity = AuditSeverity.Caution } };
        }
        finally { _stutterCompletion?.TrySetResult(); _stutterCompletion = null; }
    }

    private static object? SafeHardware(HardwareSnapshot? s) => s == null ? null : new {
        s.TimestampUtc, s.TelemetryValid, s.CpuName, s.GpuName, s.CpuTemperatureC, s.GpuTemperatureC,
        s.CpuLoadPercent, s.GpuLoadPercent, s.CpuClockMhz, s.GpuCoreClockMhz, s.GpuPowerW, s.GpuMemoryUsedMb
    };

    public void Dispose()
    {
        _stopping = true; _lifetime.Cancel();
        _telemetryTimer.Stop();
        _benchmarkCancellation?.Cancel();
        _benchmarkCancellation?.Dispose();
    }
}
