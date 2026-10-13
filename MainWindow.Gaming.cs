using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ColorForge.Models;
using ColorForge.Services;
using ColorForge.Windows;

namespace ColorForge;

public partial class MainWindow
{
    private GameBinding? _selectedGame;
    private MonitorTarget? _sessionMonitor;
    private string? _suppressedGame;
    private bool _sessionColorApplied;
    private readonly GamePresentationSession _presentation;
    private readonly PerformanceOverlayController _overlay;
    private GlobalHotkeyService? _hotkeys;

    private void InitializeGaming()
    {
        RefreshGameCards();
        DiagnosticsView.ConfigureCaptureCoordinator(_overlay.PauseFramesAsync, _overlay.ResumeFrames, _overlay.SetBenchmarkFrames);
        DiagnosticsView.SelectedProcessChanged += UpdateOverlay;
        SourceInitialized += (_, _) =>
        {
            _hotkeys = new(this); _hotkeys.Invoked += InvokeHotkey; RegisterHotkeys();
        };
        UpdateOverlay();
    }
    private void RefreshGameCards()
    {
        GameCards.ItemsSource = _settings.Games.ToArray();
        if (_selectedGame != null && !_settings.Games.Contains(_selectedGame)) _selectedGame = null;
        SelectedGameText.Text = _selectedGame == null ? "Выбери карточку игры" : "Выбрано: " + _selectedGame.Title;
        UpdateGamesSummary();
    }
    private void GameCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameBinding game }) { _selectedGame = game; RefreshGameCards(); }
    }
    private void EditGame_Click(object sender, RoutedEventArgs e)
    {
        GameBinding? game = (sender as Button)?.Tag as GameBinding ?? _selectedGame;
        if (game == null) { SetStatus("Выбери карточку игры", false); return; }
        _selectedGame = game;
        GameProfileWindow dialog = new(game, _settings, _monitors) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            int index = _settings.Games.IndexOf(game);
            if (index >= 0) { _settings.Games[index] = dialog.Result; _selectedGame = dialog.Result; }
            _suppressedGame = null; RefreshGameCards(); SaveSettings();
        }
    }
    private void ApplyGameSession(GameBinding? binding)
    {
        if (_shutdownPrepared || _preparingShutdown) return;
        if (binding?.ProcessName == _suppressedGame) return;
        _suppressedGame = null;
        try
        {
            RestoreGameSession();
            if (binding == null) { SetStatus("Общие настройки восстановлены", true); return; }
            MonitorTarget? target = string.IsNullOrEmpty(binding.MonitorDeviceName) ? _selectedMonitor
                : _monitors.FirstOrDefault(x => x.DeviceName.Equals(binding.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (target == null) { SetStatus(binding.Title + ": выбранный монитор недоступен; профиль не применён", false); return; }
            DisplayProfile? profile = _settings.Profiles.FirstOrDefault(x => x.Name.Equals(binding.ProfileName, StringComparison.OrdinalIgnoreCase));
            if (binding.ApplyColor && profile == null) { SetStatus("Цветовой профиль игры не найден", false); return; }
            _sessionMonitor = target;
            _presentation.Activate(binding); _activeGame = binding.ProcessName;
            if (binding.ApplyColor && _settings.EffectsEnabled && profile != null)
            {
                DisplayProfile manual = target.DeviceName == _selectedMonitor?.DeviceName ? _settings.Current.Clone() : _gameProfiles.Manual(target.DeviceName);
                DisplayProfile color = _gameProfiles.Activate(target.DeviceName, binding, manual);
                // Mark before the write: partial native failure still requires restoration.
                _sessionColorApplied = true;
                _engine.ApplyTemporary(color, target);
                if (target.DeviceName == _selectedMonitor?.DeviceName) LoadProfileIntoControls(color);
            }
            RefreshPresentation();
            SetStatus("Активна игра: " + binding.Title + (string.IsNullOrEmpty(_engine.LastWarning) ? "" : " · " + _engine.LastWarning), true);
            SaveSettings();
        }
        catch (Exception error) { AppLogService.Error("Game session transition failed.", error); SetStatus("Профиль игры: " + error.Message, false); }
    }
    private void RestoreGameSession(bool restoreColor = true)
    {
        if (_sessionMonitor != null && _sessionColorApplied)
        {
            DisplayProfile manual = _gameProfiles.Manual(_sessionMonitor.DeviceName);
            // Restore native values captured before the game, including the formerly active monitor.
            _engine.RestoreTemporaryProfiles();
            _gameProfiles.Activate(_sessionMonitor.DeviceName, null);
            if (_sessionMonitor.DeviceName == _selectedMonitor?.DeviceName && restoreColor) LoadProfileIntoControls(manual);
        }
        _sessionColorApplied = false; _sessionMonitor = null; _activeGame = null;
        _presentation.Activate(null); RefreshPresentation();
    }
    private void SuspendGameSession(bool restoreColor = true)
    {
        _suppressedGame = _presentation.Binding?.ProcessName ?? _activeGame;
        RestoreGameSession(restoreColor);
    }
    private void RefreshPresentation()
    {
        _crosshair.SetMonitor(_sessionMonitor?.DeviceName ?? _selectedMonitor?.DeviceName);
        _crosshair.UpdateSettings(_presentation.Crosshair);
        UpdateOverlay();
    }
    private void UpdateOverlay()
    {
        if (_shutdownPrepared || _preparingShutdown) return;
        string? process = _presentation.Binding?.ProcessName;
        _overlay.Update(_presentation.Overlay, _sessionMonitor?.DeviceName ?? _selectedMonitor?.DeviceName,
            _presentation.Binding?.Title ?? _settings.Current.Name,
            process == null ? DiagnosticsView.SelectedProcess : process + ".exe");
    }
    private void OverlaySettings_Click(object sender, RoutedEventArgs e)
    {
        OverlaySettingsWindow dialog = new(_settings.Overlay) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _settings.Overlay = dialog.Result; UpdateOverlay(); SaveSettings();
    }
    private void HotkeysSettings_Click(object sender, RoutedEventArgs e)
    {
        HotkeysWindow dialog = new(_settings.Hotkeys) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _settings.Hotkeys = dialog.Result; RegisterHotkeys(); SaveSettings();
    }
    private void RegisterHotkeys()
    {
        if (_hotkeys == null) return;
        var failures = _hotkeys.Configure(_settings.Hotkeys);
        HotkeyStatusText.Text = failures.Count == 0 ? (_settings.Hotkeys.Any(x => x.Enabled) ? "Сочетания зарегистрированы · работают в фоне" : "Горячие клавиши отключены")
            : "Не удалось зарегистрировать: " + string.Join("; ", failures);
    }
    private void InvokeHotkey(HotkeyAction action)
    {
        if (_preparingShutdown || _shutdownPrepared || OwnedWindows.Cast<Window>().Any(x => x.IsVisible)) return;
        try
        {
            switch (action)
            {
                case HotkeyAction.ToggleCrosshair:
                    _presentation.Crosshair.Enabled = !_presentation.Crosshair.Enabled;
                    RefreshPresentation(); LoadCrosshairControls(); SaveSettings(); break;
                case HotkeyAction.ToggleOverlay:
                    _presentation.Overlay.Enabled = !_presentation.Overlay.Enabled;
                    UpdateOverlay(); SaveSettings(); break;
                case HotkeyAction.ToggleBenchmark:
                    DiagnosticsView.ToggleBenchmark(_presentation.Binding?.ProcessName); break;
                case HotkeyAction.NextProfile:
                    int index = _settings.Profiles.FindIndex(x => x.Name == _settings.Current.Name);
                    if (_settings.Profiles.Count > 0) ApplyProfile(_settings.Profiles[(index + 1) % _settings.Profiles.Count], true);
                    break;
                case HotkeyAction.RestoreColors:
                    SuspendGameSession();
                    foreach (MonitorTarget monitor in _monitors) _engine.Reset(monitor);
                    _settings.EffectsEnabled = false;
                    bool previous = _initializing; _initializing = true;
                    EffectsEnabledCheck.IsChecked = false; _initializing = previous;
                    SetStatus("Исходные цвета восстановлены; эффекты выключены", true); SaveSettings(); break;
            }
        }
        catch (Exception error) { AppLogService.Error("Hotkey action failed.", error); SetStatus(error.Message, false); }
    }
}
