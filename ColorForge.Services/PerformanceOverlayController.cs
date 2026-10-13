using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ColorForge.Models;
using ColorForge.Windows;

namespace ColorForge.Services;

public sealed class PerformanceOverlayController : IDisposable
{
    private readonly HardwareMonitoringService _hardware;
    private readonly PerformanceOverlayWindow _window = new();
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _captureGate = new(1);
    private readonly LiveFrameCaptureService _live = new();
    private OverlaySettings _settings = new();
    private string? _device, _process, _capturing, _failedProcess;
    private string _profile = "";
    private bool _paused, _stopping;
    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask, _refreshTask, _hardwareTask;
    private long _captureVersion;
    private LiveFrameSnapshot _frames = Empty();
    private HardwareSnapshot? _hardwareSnapshot;
    private DateTime _hardwareReadAt;
    public PerformanceOverlayController(HardwareMonitoringService hardware)
    {
        _hardware = hardware;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher.CurrentDispatcher);
        _timer.Start();
    }
    private static LiveFrameSnapshot Empty(string status = "FPS — · выбери запущенную игру") => new(null, null, Array.Empty<double>(), status);
    public void Update(OverlaySettings settings, string? device, string profile, string? process)
    {
        if (_stopping) return;
        bool contextChanged = _process != process || _settings.Enabled != settings.Enabled
            || _settings.ShowFps != settings.ShowFps || _settings.ShowFrameGraph != settings.ShowFrameGraph;
        _settings = settings.Clone(); _device = device; _profile = profile; _process = process;
        if (contextChanged) { _failedProcess = null; _frames = Empty(); }
        if (!_settings.Enabled) _window.Hide();
        Refresh();
    }
    public async Task PauseFramesAsync()
    {
        _paused = true; _frames = Empty("Замер · ожидание кадров");
        await ChangeCaptureAsync();
    }
    public void SetBenchmarkFrames(LiveFrameSnapshot? frames)
    {
        if (_paused && frames != null) _frames = frames;
    }
    public void ResumeFrames() { _paused = false; _frames = Empty(); Refresh(); }
    private void Refresh()
    {
        if (_stopping) return;
        if (_refreshTask is not { IsCompleted: false }) _refreshTask = RefreshCaptureAsync();
        if (!_settings.Enabled) { _window.Hide(); return; }
        if ((_settings.ShowCpu || _settings.ShowGpu || _settings.ShowVram) && _hardwareTask is not { IsCompleted: false }
            && DateTime.UtcNow - _hardwareReadAt >= TimeSpan.FromSeconds(1))
        { _hardwareReadAt = DateTime.UtcNow; _hardwareTask = ReadHardwareAsync(); }
        try { _window.Update(_settings, _device, _profile, _hardwareSnapshot, OverlayFreshness.Frames(_frames, DateTime.UtcNow)); }
        catch (Exception error) { AppLogService.Error("Overlay rendering failed.", error); _window.Hide(); }
    }
    private async Task ReadHardwareAsync()
    {
        try { _hardwareSnapshot = await _hardware.ReadSnapshotAsync(); }
        catch (Exception error) { _hardwareSnapshot = null; AppLogService.Error("Overlay sensor query failed.", error); }
    }
    private async Task RefreshCaptureAsync()
    {
        try
        {
            await ChangeCaptureAsync();
            if (!_paused && _captureTask == null && _failedProcess == null) _frames = Empty();
        }
        catch (Exception error) { AppLogService.Error("Overlay capture transition failed.", error); _frames = Empty("FPS — · ошибка захвата"); }
    }
    private async Task ChangeCaptureAsync()
    {
        await _captureGate.WaitAsync();
        try
        {
            string? wanted = !_stopping && !_paused && _settings.Enabled && (_settings.ShowFps || _settings.ShowFrameGraph) ? _process : null;
            if (_captureTask != null && _capturing == wanted && !_captureTask.IsCompleted) return;
            if (_captureTask != null)
            {
                _captureCancellation!.Cancel();
                try { await _captureTask; }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    AppLogService.Error("Live frame capture failed.", e);
                    if (_capturing == wanted) { _failedProcess = wanted; _frames = Empty("FPS — · " + e.Message); }
                }
                _captureCancellation.Dispose(); _captureCancellation = null; _captureTask = null; _capturing = null;
            }
            // Re-read desired state after cleanup: the game may have changed while awaiting it.
            wanted = !_stopping && !_paused && _settings.Enabled && (_settings.ShowFps || _settings.ShowFrameGraph) ? _process : null;
            if (wanted == null || wanted == _failedProcess) return;
            _capturing = wanted; _captureCancellation = new();
            long version = ++_captureVersion;
            string target = wanted;
            Progress<LiveFrameSnapshot> progress = new(snapshot => { if (!_stopping && !_paused && _captureVersion == version && _capturing == target && _process == target) _frames = snapshot; });
            _captureTask = _live.RunAsync(target, progress, _captureCancellation.Token);
        }
        finally { _captureGate.Release(); }
    }
    public async Task StopAsync()
    {
        _stopping = true; _timer.Stop(); _window.Hide(); _captureCancellation?.Cancel();
        if (_refreshTask != null) await _refreshTask;
        if (_hardwareTask != null) await _hardwareTask;
        await ChangeCaptureAsync();
    }
    public void Dispose() { _timer.Stop(); _window.Close(); }
}
