using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using ColorForge.Controls;
using ColorForge.Models;
using ColorForge.Services;
using ColorForge.Windows;
using Microsoft.Win32;

namespace ColorForge;

public partial class MainWindow : Window
{
	private readonly AppSettings _settings;

	private readonly SettingsStore _store;

	private readonly ColorEngine _engine;

	private readonly AuthSession _authSession;

	private readonly Func<Task> _signOut;

	private readonly ProfileMonitor _monitor;
    private readonly GameProfileController _gameProfiles;
    private readonly ProfileCatalogService _profileCatalog;
    private bool _updatingProfileList;
    private bool _confirmingDisplayMode;
    private bool _refreshingDisplays;
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer;

	private readonly WindowsTweaksService _windowsTweaks = new WindowsTweaksService();

	private readonly NvidiaGameProfileService _nvidiaGameProfiles = new NvidiaGameProfileService();

	private readonly HardwareMonitoringService _hardware;

	private readonly CrosshairWindow _crosshair;

	private readonly NotifyIcon _tray;

	private readonly SliderField[] _colorFields;

	private readonly System.Windows.Controls.Button[] _sceneButtons;

	private readonly List<TweakOptionViewModel> _tweakOptions = new List<TweakOptionViewModel>();

	private List<MonitorTarget> _monitors = new List<MonitorTarget>();

	private MonitorTarget? _selectedMonitor;

	private bool _initializing = true;

	private bool _closing;
    private bool _shutdownPrepared;
    private bool _preparingShutdown;
    private TaskCompletionSource? _tweaksCompletion;

	private string? _activeGame;


	public MainWindow(AppSettings settings, SettingsStore store, ColorEngine engine, AuthSession authSession, Func<Task> signOut, HardwareMonitoringService hardware)
	{
		_settings = settings;
        _gameProfiles = new GameProfileController(settings);
        _profileCatalog = new ProfileCatalogService(settings);
		_store = store;
		_engine = engine;
		_authSession = authSession;
        _hardware = hardware;
		_signOut = signOut;
		InitializeComponent();
        _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); PersistSettings(); };
		AccountNameText.Text = authSession.Email;
		SubscriptionText.Text = authSession.AuthMode == "Prototype" ? "прототип · тестовая активация" : $"активна · {authSession.DaysRemaining} дн.";
		SettingsAccountEmailText.Text = authSession.Email;
		SettingsSubscriptionText.Text = $"{authSession.PlanName} · до {authSession.ExpiresAtUtc.ToLocalTime():dd.MM.yyyy}";
		_colorFields = new SliderField[7] { SaturationField, VibranceField, BrightnessField, GammaField, ContrastField, TemperatureField, ShadowsField };
		_sceneButtons = new System.Windows.Controls.Button[9] { ModeWinter, ModeNight, ModeDesert, ModeJungle, ModeGreen, ModeMetro, ModeCs2, ModeDota, ModeCinema };
		SliderField[] colorFields = _colorFields;
		for (int i = 0; i < colorFields.Length; i++)
		{
			colorFields[i].FieldValueChanged += ColorField_Changed;
		}
		CrosshairSizeField.FieldValueChanged += CrosshairField_Changed;
		CrosshairGapField.FieldValueChanged += CrosshairField_Changed;
		CrosshairThicknessField.FieldValueChanged += CrosshairField_Changed;
		CrosshairOutlineField.FieldValueChanged += CrosshairField_Changed;
		CrosshairShapeCombo.ItemsSource = Enum.GetNames<CrosshairShape>();
		GameGrid.ItemsSource = _settings.Games;
		UpdateGamesSummary();
		LoadMonitors();
		DiagnosticsView.Initialize(_hardware, GetSelectedDisplayMode);
		OverclockView.Initialize(_hardware, _settings, _store);
		_crosshair = new CrosshairWindow();
		_crosshair.SetMonitor(_selectedMonitor?.DeviceName);
        _monitor = new ProfileMonitor();
        _monitor.UpdateBindings(_settings.Games);
		_monitor.ActiveGameChanged += delegate(GameBinding? binding)
		{
			base.Dispatcher.BeginInvoke(new Action(() => OnActiveGameChanged(binding)));
		};
		_tray = BuildTrayIcon();
		LoadProfileIntoControls(_gameProfiles.Manual(_selectedMonitor?.DeviceName ?? string.Empty));
		LoadDisplayModes();
		LoadCrosshairControls();
        RefreshProfileList();
		LoadAppSettingsControls();
		NvidiaStatusText.Text = (_engine.NvidiaAvailable ? "NVIDIA Digital Vibrance: подключено · применяется только к выбранному монитору" : "NVIDIA не обнаружена · яркость, гамма, контраст и температура всё равно доступны");
		NvidiaStatusText.Foreground = (_engine.NvidiaAvailable ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(98, 214, 155)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(byte.MaxValue, 143, 143)));
		SettingsPathText.Text = _store.SettingsPath;
		UpdateMonitorLabels();
		base.StateChanged += MainWindow_StateChanged;
		base.Closing += MainWindow_Closing;
		base.Closed += MainWindow_Closed;
		_initializing = false;
		if (_settings.AutoSwitchGames)
		{
			_monitor.Start();
		}
        try { foreach (var monitor in _monitors) _engine.Reset(monitor); }
        catch (Exception exception) { SetStatus("Есть невосстановленный цвет: " + exception.Message, false); }
        if (_settings.EffectsEnabled && _settings.ApplyProfileOnStartup)
		{
			ApplyCurrent();
		}
		_crosshair.UpdateSettings(_settings.Crosshair);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        OverclockView.ApplySavedProfilesAtStartup();
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 2)
		{
			ToggleMaximize();
		}
		else
		{
			DragMove();
		}
	}

	private void Minimize_Click(object sender, RoutedEventArgs e)
	{
		base.WindowState = WindowState.Minimized;
	}

	private void Maximize_Click(object sender, RoutedEventArgs e)
	{
		ToggleMaximize();
	}

	private void Close_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void ToggleMaximize()
	{
		base.WindowState = ((base.WindowState != WindowState.Maximized) ? WindowState.Maximized : WindowState.Normal);
	}

	private void Nav_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { Tag: string tag })
		{
			ShowPage(tag);
		}
	}

	private void ShowPage(string page)
	{
		DisplayPage.Visibility = Visibility.Collapsed;
		GamesPage.Visibility = Visibility.Collapsed;
		CrosshairPage.Visibility = Visibility.Collapsed;
		DiagnosticsPage.Visibility = Visibility.Collapsed;
		OverclockPage.Visibility = Visibility.Collapsed;
		BiosPage.Visibility = Visibility.Collapsed;
		TweaksPage.Visibility = Visibility.Collapsed;
		SettingsPage.Visibility = Visibility.Collapsed;
		Dictionary<string, (string, string)> dictionary = new Dictionary<string, (string, string)>
		{
			["Display"] = ("Дисплей", "Один активный монитор · профили и разрешение переключаются вместе"),
			["Games"] = ("Автопрофили", "AURA меняет режим при запуске выбранной игры"),
			["Crosshair"] = ("Прицел", "Кликабельный оверлей без внедрения в игру"),
			["Diagnostics"] = ("Диагностика", "FPS, фреймтайм, датчики и поиск причин статтеров"),
			["Overclock"] = ("Smart Auto Overclock", "Автонастройка с нагрузочным тестом и аварийным откатом"),
			["Bios"] = ("BIOS Guide", "Проверенные настройки UEFI без автоматической записи в прошивку"),
			["Tweaks"] = ("Твики Windows", "Оптимизация, очистка и управляемый откат"),
			["Settings"] = ("Настройки", "Запуск, трей и безопасный сброс")
		};
		switch (page)
		{
		case "Games":
			GamesPage.Visibility = Visibility.Visible;
			break;
		case "Crosshair":
			CrosshairPage.Visibility = Visibility.Visible;
			break;
		case "Diagnostics":
			DiagnosticsPage.Visibility = Visibility.Visible;
			break;
		case "Overclock":
			OverclockPage.Visibility = Visibility.Visible;
			break;
		case "Bios":
			BiosPage.Visibility = Visibility.Visible;
			break;
		case "Tweaks":
			TweaksPage.Visibility = Visibility.Visible;
			break;
		case "Settings":
			SettingsPage.Visibility = Visibility.Visible;
			break;
		default:
			DisplayPage.Visibility = Visibility.Visible;
			page = "Display";
			break;
		}
		PageTitle.Text = dictionary[page].Item1;
		PageSubtitle.Text = dictionary[page].Item2;
		System.Windows.Controls.Button[] array = new System.Windows.Controls.Button[8] { NavDisplay, NavGames, NavCrosshair, NavDiagnostics, NavOverclock, NavTweaks, NavBios, NavSettings };
		foreach (System.Windows.Controls.Button obj in array)
		{
			bool flag = object.Equals(obj.Tag, page);
			obj.Background = (flag ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(67, 44, 89)) : System.Windows.Media.Brushes.Transparent);
			obj.BorderBrush = (flag ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(130, 80, 178)) : System.Windows.Media.Brushes.Transparent);
			obj.Foreground = (flag ? System.Windows.Media.Brushes.White : new SolidColorBrush(System.Windows.Media.Color.FromRgb(176, 169, 191)));
		}
	}

	private void LoadMonitors()
	{
		_monitors = MonitorService.GetMonitors();
		MonitorCombo.ItemsSource = _monitors;
		_selectedMonitor = _monitors.FirstOrDefault((MonitorTarget x) => x.DeviceName.Equals(_settings.SelectedMonitorDeviceName, StringComparison.OrdinalIgnoreCase)) ?? _monitors.FirstOrDefault();
		MonitorCombo.SelectedItem = _selectedMonitor;
		if ((object)_selectedMonitor != null)
		{
			_settings.SelectedMonitorDeviceName = _selectedMonitor.DeviceName;
		}
		UpdateMonitorLabels();
	}

	private DisplayMode? GetSelectedDisplayMode()
	{
		try
		{
			return _selectedMonitor == null ? null : DisplayModeService.GetCurrentMode(_selectedMonitor.DeviceName);
		}
		catch
		{
			return null;
		}
	}

	private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_initializing && MonitorCombo.SelectedItem is MonitorTarget monitorTarget && !monitorTarget.DeviceName.Equals(_selectedMonitor?.DeviceName, StringComparison.OrdinalIgnoreCase))
		{
			ReadColorControls();
			StoreCurrentForMonitor();
			_selectedMonitor = monitorTarget;
			_settings.SelectedMonitorDeviceName = monitorTarget.DeviceName;
			_crosshair.SetMonitor(monitorTarget.DeviceName);
			UpdateMonitorLabels();
			DisplayProfile profile = _gameProfiles.Manual(monitorTarget.DeviceName);
			LoadProfileIntoControls(profile);
			LoadDisplayModes();
			if (_settings.EffectsEnabled)
			{
				ApplyCurrent();
				return;
			}
			SetStatus("Выбран · " + monitorTarget.Label, ok: true);
			SaveSettings();
		}
	}

	private void StoreCurrentForMonitor()
	{
		if ((object)_selectedMonitor != null)
		{
            if (_gameProfiles.ActiveGame == null) _gameProfiles.SaveManual(_selectedMonitor.DeviceName, _settings.Current);
		}
	}

	private void UpdateSceneSelection(string profileName)
	{
		ActiveSceneText.Text = ((profileName == "Natural") ? "Натуральный" : profileName);
		System.Windows.Controls.Button[] sceneButtons = _sceneButtons;
		foreach (System.Windows.Controls.Button obj in sceneButtons)
		{
			bool flag = object.Equals(obj.Tag, profileName);
			obj.BorderBrush = (flag ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(154, 74, 233)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 45, 54)));
			obj.BorderThickness = new Thickness((!flag) ? 1 : 2);
			obj.Background = (flag ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 20, 42)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 13, 18)));
		}
	}

	private void ColorField_Changed(object? sender, EventArgs e)
	{
		if (!_initializing)
		{
            ReadColorControls();
            _gameProfiles.ChooseManual(_selectedMonitor?.DeviceName ?? string.Empty, _settings.Current);
            _activeGame = null;
            ApplyCurrent();
		}
	}


	private void ReadColorControls()
	{
		DisplayProfile current = _settings.Current;
		current.Saturation = (int)SaturationField.Value;
		current.Vibrance = (int)VibranceField.Value;
		current.Brightness = (int)BrightnessField.Value;
		current.Gamma = (int)GammaField.Value;
		current.Contrast = (int)ContrastField.Value;
		current.Temperature = (int)TemperatureField.Value;
		current.Shadows = (int)ShadowsField.Value;
	}

	private void LoadProfileIntoControls(DisplayProfile profile)
	{
		bool initializing = _initializing;
		_initializing = true;
		_settings.Current = profile.Clone();
		SaturationField.Value = profile.Saturation;
		VibranceField.Value = profile.Vibrance;
		BrightnessField.Value = profile.Brightness;
		GammaField.Value = profile.Gamma;
		ContrastField.Value = profile.Contrast;
		TemperatureField.Value = profile.Temperature;
		ShadowsField.Value = profile.Shadows;
		UpdateSceneSelection(profile.Name);
		_initializing = initializing;
	}

	private void ApplyProfile(DisplayProfile profile, bool select)
	{
		LoadProfileIntoControls(profile);
		if (select)
		{
			_settings.SelectedProfileName = profile.Name;
			_settings.Current.Name = profile.Name;
            _gameProfiles.ChooseManual(_selectedMonitor?.DeviceName ?? string.Empty, profile);
            RefreshProfileList();
		}
		ApplyCurrent();
	}

	private void ApplyCurrent()
	{
		StoreCurrentForMonitor();
		if (!_settings.EffectsEnabled || (object)_selectedMonitor == null)
		{
			SaveSettings();
			return;
		}
        try { _engine.Apply(_settings.Current, _selectedMonitor); }
        catch (Exception exception) { AppLogService.Error("Display profile apply failed.", exception); SetStatus("Не применено: " + exception.Message, false); return; }
		bool flag = string.IsNullOrWhiteSpace(_engine.LastWarning);
		SetStatus(flag ? ("Применено · " + _settings.Current.Name) : _engine.LastWarning, flag);
		SaveSettings();
	}

	private void ScenePreset_Click(object sender, RoutedEventArgs e)
	{
		if (!(sender is System.Windows.Controls.Button { Tag: var tag }))
		{
			return;
		}
		string name = tag as string;
		if (name != null)
		{
			DisplayProfile displayProfile = _settings.Profiles.FirstOrDefault((DisplayProfile x) => x.Name == name);
			if (displayProfile != null)
			{
				ApplyProfile(displayProfile, select: true);
				UpdateSceneSelection(displayProfile.Name);
			}
		}
	}

	private void Reset_Click(object sender, RoutedEventArgs e)
	{
		if ((object)_selectedMonitor != null)
		{
            try { _engine.Reset(_selectedMonitor); }
            catch (Exception exception) { AppLogService.Error("Display restore failed.", exception); SetStatus("Восстановление не завершено: " + exception.Message, false); return; }
		}
		DisplayProfile displayProfile = _settings.Profiles.FirstOrDefault((DisplayProfile x) => x.Name == "Natural") ?? new DisplayProfile();
		LoadProfileIntoControls(displayProfile);
        _gameProfiles.ChooseManual(_selectedMonitor?.DeviceName ?? string.Empty, displayProfile);
        _activeGame = null;
		_settings.SelectedProfileName = displayProfile.Name;
        RefreshProfileList();
		StoreCurrentForMonitor();
		SetStatus("Выбранный монитор сброшен", ok: true);
		SaveSettings();
	}

    private void RefreshProfileList()
    {
        _updatingProfileList = true;
        ProfileCombo.ItemsSource = _settings.Profiles.ToArray();
        ProfileCombo.SelectedItem = _settings.Profiles.FirstOrDefault(x => x.Name.Equals(_settings.SelectedProfileName, StringComparison.OrdinalIgnoreCase));
        _updatingProfileList = false;
    }
    private void ProfileCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _updatingProfileList || ProfileCombo.SelectedItem is not DisplayProfile profile) return;
        ApplyProfile(profile, true);
    }
    private void SaveProfileAs_Click(object sender, RoutedEventArgs e)
    {
        PromptWindow prompt = new("Новый профиль", "Название профиля", "Мой профиль") { Owner = this };
        if (prompt.ShowDialog() != true) return;
        try
        {
            ReadColorControls();
            DisplayProfile profile = _profileCatalog.Create(prompt.Answer, _settings.Current);
            ApplyProfile(profile, true);
            RefreshProfileList();
        }
        catch (Exception exception) { SetStatus(exception.Message, false); }
    }
    private void UpdateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not DisplayProfile profile) return;
        if (ProfileCatalogService.IsBuiltIn(profile.Name)) { SetStatus("Сохрани изменения как новый профиль", false); return; }
        ReadColorControls();
        int index = _settings.Profiles.IndexOf(profile);
        _settings.Profiles[index] = _settings.Current.Clone(profile.Name);
        ApplyProfile(_settings.Profiles[index], true);
        RefreshProfileList();
    }
    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not DisplayProfile profile) return;
        PromptWindow prompt = new("Переименовать профиль", "Новое название", profile.Name) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        try { _profileCatalog.Rename(profile, prompt.Answer); RefreshProfileList(); GameGrid.Items.Refresh(); SaveSettings(); }
        catch (Exception exception) { SetStatus(exception.Message, false); }
    }
    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not DisplayProfile profile) return;
        if (System.Windows.MessageBox.Show("Удалить профиль «" + profile.Name + "»? Игровые привязки будут переведены на Natural.", "AURA", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { _profileCatalog.Delete(profile); RefreshProfileList(); GameGrid.Items.Refresh(); SaveSettings(); }
        catch (Exception exception) { SetStatus(exception.Message, false); }
    }
    private void ImportProfiles_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Filter = "Профили JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            int count = 0;
            foreach (DisplayProfile profile in _store.ImportProfiles(dialog.FileName))
                if (!_settings.Profiles.Any(x => x.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))) { _settings.Profiles.Add(profile); count++; }
            RefreshProfileList(); SaveSettings(); SetStatus($"Импортировано {count}; существующие названия сохранены", true);
        }
        catch (Exception exception) { SetStatus("Импорт: " + exception.Message, false); }
    }
    private void ExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.SaveFileDialog dialog = new() { Filter = "Профили JSON|*.json", FileName = "AURA-profiles.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { _store.ExportProfiles(dialog.FileName, _settings.Profiles); SetStatus("Профили экспортированы", true); }
        catch (Exception exception) { SetStatus("Экспорт: " + exception.Message, false); }
    }

	private void AddGame_Click(object sender, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "Приложения (*.exe)|*.exe",
			Title = "Выбери файл игры"
		};
		if (openFileDialog.ShowDialog(this) == true)
		{
			string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(openFileDialog.FileName);
			_settings.Games.Add(new GameBinding
			{
				ProcessName = fileNameWithoutExtension,
				ProfileName = _settings.SelectedProfileName
			});
			GameGrid.Items.Refresh();
			UpdateGamesSummary();
			SaveSettings();
		}
	}

	private void DeleteGame_Click(object sender, RoutedEventArgs e)
	{
		if (GameGrid.SelectedItem is GameBinding item)
		{
			_settings.Games.Remove(item);
			GameGrid.Items.Refresh();
			UpdateGamesSummary();
			SaveSettings();
		}
	}

	private void ApplyNvidiaGameProfile_Click(object sender, RoutedEventArgs e)
	{
		if (GameGrid.SelectedItem is not GameBinding binding)
		{
			SetStatus("Сначала выбери игру в таблице", ok: false);
			return;
		}
		try
		{
			SetStatus(_nvidiaGameProfiles.ApplyMaximumPerformance(binding.ProcessName), ok: true);
		}
		catch (Exception ex)
		{
			SetStatus("NVIDIA-профиль · " + ex.Message, ok: false);
		}
	}

	private void ResetNvidiaGameProfile_Click(object sender, RoutedEventArgs e)
	{
		if (GameGrid.SelectedItem is not GameBinding binding)
		{
			SetStatus("Сначала выбери игру в таблице", ok: false);
			return;
		}
		try
		{
			SetStatus(_nvidiaGameProfiles.RestoreDriverDefault(binding.ProcessName), ok: true);
		}
		catch (Exception ex)
		{
			SetStatus("Сброс NVIDIA-профиля · " + ex.Message, ok: false);
		}
	}

	private void GameGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
	{
		base.Dispatcher.BeginInvoke(new Action(SaveSettings));
	}

	private void AutoSwitch_Changed(object sender, RoutedEventArgs e)
	{
		if (!_initializing)
		{
			_settings.AutoSwitchGames = AutoSwitchCheck.IsChecked == true;
			if (_settings.AutoSwitchGames)
			{
				_monitor.Start();
			}
			else
			{
				_monitor.Stop();
			}
			UpdateGamesSummary();
			SaveSettings();
		}
	}

	private void GamePresetCard_Click(object sender, RoutedEventArgs e)
	{
		if (!(sender is System.Windows.Controls.Button { Tag: var tag }))
		{
			return;
		}
		string process = tag as string;
		if (process == null)
		{
			return;
		}
		GameBinding binding = _settings.Games.FirstOrDefault((GameBinding x) => x.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase));
		if (binding != null)
		{
			GameGrid.SelectedItem = binding;
			GameGrid.ScrollIntoView(binding);
			DisplayProfile displayProfile = _settings.Profiles.FirstOrDefault((DisplayProfile x) => x.Name == binding.ProfileName);
			if (displayProfile != null)
			{
				ApplyProfile(displayProfile, select: true);
			}
			SetStatus(binding.ProcessName + " · " + binding.ProfileName, ok: true);
		}
	}

	private void UpdateGamesSummary()
	{
		if (GamesCountText != null && AutoSwitchStatusText != null)
		{
			GamesCountText.Text = $"{_settings.Games.Count} ПРОФИЛЯ";
			AutoSwitchStatusText.Text = (_settings.AutoSwitchGames ? "Работает в фоне" : "Остановлено");
			AutoSwitchStatusText.Foreground = new SolidColorBrush(_settings.AutoSwitchGames ? System.Windows.Media.Color.FromRgb(101, 223, 154) : System.Windows.Media.Color.FromRgb(129, 120, 136));
		}
	}

    private void OnActiveGameChanged(GameBinding? binding)
    {
        if (_selectedMonitor == null || _shutdownPrepared) return;
        DisplayProfile profile = _gameProfiles.Activate(_selectedMonitor.DeviceName, binding);
        _activeGame = _gameProfiles.ActiveGame;
        ApplyProfile(profile, false);
        SetStatus(binding == null ? "Восстановлена ручная настройка монитора" : binding.ProcessName + " · " + profile.Name, true);
    }

	private void LoadCrosshairControls()
	{
		CrosshairEnabledCheck.IsChecked = _settings.Crosshair.Enabled;
		CrosshairShapeCombo.SelectedItem = _settings.Crosshair.Shape.ToString();
		CrosshairSizeField.Value = _settings.Crosshair.Size;
		CrosshairGapField.Value = _settings.Crosshair.Gap;
		CrosshairThicknessField.Value = _settings.Crosshair.Thickness;
		CrosshairOutlineField.Value = _settings.Crosshair.Outline;
		UpdateCrosshairPreview();
	}

	private void CrosshairField_Changed(object? sender, EventArgs e)
	{
		UpdateCrosshairFromControls();
	}

	private void Crosshair_Changed(object sender, RoutedEventArgs e)
	{
		UpdateCrosshairFromControls();
	}

	private void Crosshair_Changed(object sender, SelectionChangedEventArgs e)
	{
		UpdateCrosshairFromControls();
	}

	private void UpdateCrosshairFromControls()
	{
		if (!_initializing)
		{
			_settings.Crosshair.Enabled = CrosshairEnabledCheck.IsChecked == true;
			_settings.Crosshair.Shape = (Enum.TryParse<CrosshairShape>(CrosshairShapeCombo.SelectedItem?.ToString(), out var result) ? result : CrosshairShape.Cross);
			_settings.Crosshair.Size = (int)CrosshairSizeField.Value;
			_settings.Crosshair.Gap = (int)CrosshairGapField.Value;
			_settings.Crosshair.Thickness = (int)CrosshairThicknessField.Value;
			_settings.Crosshair.Outline = (int)CrosshairOutlineField.Value;
			_crosshair.UpdateSettings(_settings.Crosshair);
			UpdateCrosshairPreview();
			SaveSettings();
		}
	}

	private void ChooseCrosshairColor_Click(object sender, RoutedEventArgs e)
	{
		using ColorDialog colorDialog = new ColorDialog
		{
			Color = System.Drawing.Color.FromArgb(_settings.Crosshair.Argb),
			FullOpen = true
		};
		if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			_settings.Crosshair.Argb = colorDialog.Color.ToArgb();
			UpdateCrosshairFromControls();
		}
	}

	private void CrosshairShapeButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { Tag: string tag })
		{
			CrosshairShapeCombo.SelectedItem = tag;
		}
	}

	private void QuickCrosshairColor_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { Tag: string tag } && System.Windows.Media.ColorConverter.ConvertFromString(tag) is System.Windows.Media.Color color)
		{
			_settings.Crosshair.Argb = (color.A << 24) | (color.R << 16) | (color.G << 8) | color.B;
			UpdateCrosshairFromControls();
		}
	}

    private void UpdateCrosshairPreview()
    {
        var color = System.Drawing.Color.FromArgb(_settings.Crosshair.Argb);
        CrosshairColorSwatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
        CrosshairPreviewControl.UpdateSettings(_settings.Crosshair);
		System.Windows.Controls.Button[] array = new System.Windows.Controls.Button[4] { ShapeCrossButton, ShapeDotButton, ShapeCircleButton, ShapeTButton };
		foreach (System.Windows.Controls.Button obj in array)
		{
			bool flag2 = object.Equals(obj.Tag?.ToString(), _settings.Crosshair.Shape.ToString());
			obj.Background = new SolidColorBrush(flag2 ? System.Windows.Media.Color.FromRgb(76, 42, 100) : System.Windows.Media.Color.FromRgb(41, 38, 47));
			obj.BorderBrush = new SolidColorBrush(flag2 ? System.Windows.Media.Color.FromRgb(154, 83, 214) : System.Windows.Media.Color.FromRgb(58, 54, 66));
		}
	}


	private void LoadDisplayModes()
	{
		if ((object)_selectedMonitor == null)
		{
			return;
		}
		try
		{
			List<DisplayMode> modes = DisplayModeService.GetModes(_selectedMonitor.DeviceName);
			DisplayModeCombo.ItemsSource = modes;
			DisplayMode current = DisplayModeService.GetCurrentMode(_selectedMonitor.DeviceName);
			DisplayModeCombo.SelectedItem = (((object)current == null) ? modes.FirstOrDefault() : (modes.FirstOrDefault((DisplayMode x) => x == current) ?? modes.FirstOrDefault()));
			if ((object)current != null)
			{
				CustomWidthBox.Text = current.Width.ToString();
				CustomHeightBox.Text = current.Height.ToString();
				CustomHzBox.Text = current.RefreshRate.ToString();
			}
		}
		catch (Exception ex)
		{
			SetStatus("Режимы монитора · " + ex.Message, ok: false);
		}
	}

	private void ApplyDisplayMode_Click(object sender, RoutedEventArgs e)
	{
		if ((object)_selectedMonitor != null && DisplayModeCombo.SelectedItem is DisplayMode mode)
		{
			ApplyDisplayMode(mode);
		}
	}

	private void QuickResolution_Click(object sender, RoutedEventArgs e)
	{
		if ((object)_selectedMonitor == null || !(sender is System.Windows.Controls.Button { Tag: string tag }))
		{
			return;
		}
		string[] array = tag.Split('x');
		if (array.Length == 2 && int.TryParse(array[0], out var result) && int.TryParse(array[1], out var result2))
		{
			int preferredHz = DisplayModeService.GetCurrentMode(_selectedMonitor.DeviceName)?.RefreshRate ?? 60;
			DisplayMode displayMode = DisplayModeService.FindClosest(_selectedMonitor.DeviceName, result, result2, preferredHz);
			if ((object)displayMode == null)
			{
				SetStatus($"{result}×{result2} не поддерживается этим монитором", ok: false);
			}
			else
			{
				ApplyDisplayMode(displayMode);
			}
		}
	}

	private void ApplyCustomMode_Click(object sender, RoutedEventArgs e)
	{
		if ((object)_selectedMonitor == null || !int.TryParse(CustomWidthBox.Text, out var result) || !int.TryParse(CustomHeightBox.Text, out var result2) || !int.TryParse(CustomHzBox.Text, out var result3))
		{
			SetStatus("Проверь ширину, высоту и частоту", ok: false);
			return;
		}
		DisplayMode displayMode = DisplayModeService.FindClosest(_selectedMonitor.DeviceName, result, result2, result3);
		if ((object)displayMode == null)
		{
			SetStatus($"Режим {result}×{result2} не зарегистрирован в Windows", ok: false);
		}
		else
		{
			ApplyDisplayMode(displayMode);
		}
	}

    private void ApplyDisplayMode(DisplayMode mode)
    {
        if (_selectedMonitor == null) return;
        string device = _selectedMonitor.DeviceName;
        var original = DisplayModeService.Capture(device);
        if (original == null) { SetStatus("Исходный режим не прочитан; изменение пропущено", false); return; }
        bool hasScaling = DisplayScalingService.TryReadScaling(device, out uint originalScaling);
        bool committed = false;
        bool attempted = false;
        bool persistAttempted = false;
        IDisposable? operation = null;
        _confirmingDisplayMode = true;
        try
        {
            operation = HardwareOperations.Enter();
            attempted = true;
            if (!DisplayModeService.Apply(device, mode, _settings.StretchScaling, false)) throw new InvalidOperationException("Windows не подтвердила новый режим.");
            bool scaling = DisplayScalingService.TryApply(device, _settings.StretchScaling, out string scalingError, false);
            DisplayModeConfirmationWindow confirmation = new(mode) { Owner = this };
            if (confirmation.ShowDialog() == true)
            {
                persistAttempted = true;
                if (!DisplayModeService.Apply(device, mode, _settings.StretchScaling, true)) throw new InvalidOperationException("Режим не удалось сохранить.");
                if (scaling) DisplayScalingService.TryApply(device, _settings.StretchScaling, out _);
                committed = true;
                SetStatus(scaling ? "Режим подтверждён · " + mode : "Режим сохранён; " + scalingError, scaling);
            }
        }
        catch (Exception exception) { AppLogService.Error("Display mode change failed.", exception); SetStatus(exception.Message, false); }
        finally
        {
            if (attempted && !committed)
            {
                bool restored = DisplayModeService.Apply(device, original.Value.Mode, false, persistAttempted, original.Value.FixedOutput);
                bool scaleRestored = !hasScaling || DisplayScalingService.TryApplyScaling(device, originalScaling, out _, persistAttempted);
                SetStatus(restored && scaleRestored ? "Прежний режим восстановлен" : "Возврат режима не подтверждён; открой параметры экрана Windows", restored && scaleRestored);
            }
            _confirmingDisplayMode = false;
            operation?.Dispose();
            LoadDisplayModes();
            _crosshair.SetMonitor(device);
        }
    }

	private void Scaling_Changed(object sender, RoutedEventArgs e)
	{
		if (!_initializing)
		{
			_settings.StretchScaling = StretchScalingCheck.IsChecked == true;
			SaveSettings();
			ApplySelectedScaling();
		}
	}

	private void ApplyScaling_Click(object sender, RoutedEventArgs e)
	{
		ApplySelectedScaling();
	}

	private void ApplySelectedScaling()
	{
        try {
        using var operation = HardwareOperations.Enter();
		if ((object)_selectedMonitor != null)
		{
			DisplayMode currentMode = DisplayModeService.GetCurrentMode(_selectedMonitor.DeviceName);
			if ((object)currentMode == null)
			{
				SetStatus("Не удалось прочитать текущий режим монитора", ok: false);
				return;
			}
            bool flag = new ScalingRecoveryService().Apply(_selectedMonitor.DeviceName, _settings.StretchScaling, out string detail);
            ScalingStatusText.Text = detail;
			ScalingStatusText.Foreground = new SolidColorBrush(flag ? System.Windows.Media.Color.FromRgb(101, 223, 154) : System.Windows.Media.Color.FromRgb(byte.MaxValue, 124, 124));
			SetStatus(ScalingStatusText.Text, flag);
		}
        } catch (Exception exception) { SetStatus("Масштабирование не применено: " + exception.Message, false); }
	}

	private void OpenHdr_Click(object sender, RoutedEventArgs e)
	{
		Process.Start(new ProcessStartInfo("ms-settings:display-advancedcolor")
		{
			UseShellExecute = true
		});
	}

	private void LoadAppSettingsControls()
	{
		AutoSwitchCheck.IsChecked = _settings.AutoSwitchGames;
		StartupCheck.IsChecked = _settings.StartWithWindows;
		TrayCheck.IsChecked = _settings.MinimizeToTray;
        ApplyOnStartCheck.IsChecked = _settings.ApplyProfileOnStartup;
		EffectsEnabledCheck.IsChecked = _settings.EffectsEnabled;
		StretchScalingCheck.IsChecked = _settings.StretchScaling;
		LoadTweakControls();
        if (_windowsTweaks.HasLegacyState()) SetStatus("Найдены старые снимки v0.10. Они сохранены; новый журнал их автоматически не восстанавливает.", false);
        else if (_windowsTweaks.HasPendingState()) SetStatus("Есть сохранённые изменения Windows. Их можно вернуть в Windows Tuner.", false);
		UpdateGamesSummary();
	}

	private void LoadTweakControls()
	{
		WindowsTweakPreferences tweaks = _settings.Tweaks ?? new WindowsTweakPreferences();
		_settings.Tweaks = tweaks;
		WindowsTweaksService.InitializePreferences(tweaks, migrateLegacy: false);
		HashSet<string> selected = new HashSet<string>(tweaks.EnabledTweakIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
		_tweakOptions.Clear();
		_tweakOptions.AddRange(_windowsTweaks.Catalog.Select(definition => new TweakOptionViewModel(definition, selected.Contains(definition.Id))));
        foreach (var action in _windowsTweaks.LoadHistory().SelectMany(x => x.Actions).GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(x => x.TimeUtc).First()))
            _tweakOptions.FirstOrDefault(x => x.Id.Equals(action.Id, StringComparison.OrdinalIgnoreCase))?.UpdateStatus(action.Status);
		RebuildTweakGroups();
		WindowsTweaksAudit audit = WindowsTweaksService.AuditCatalog();
		if (!audit.Success)
		{
			SetTweaksBusy(false, "ОШИБКА КАТАЛОГА", false);
			SetStatus(string.Join(" · ", audit.Errors), false);
		}
	}

	private void TweakSearch_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (TweakSearchHint != null)
			TweakSearchHint.Visibility = string.IsNullOrWhiteSpace(TweakSearchBox?.Text) ? Visibility.Visible : Visibility.Collapsed;
		RebuildTweakGroups();
	}

	private void TweakSelection_Changed(object sender, RoutedEventArgs e) => UpdateTweakCount();

	private void RebuildTweakGroups()
	{
		if (TweakGroupsControl == null)
			return;
		string query = TweakSearchBox?.Text.Trim() ?? string.Empty;
		TweakGroupViewModel[] groups = _tweakOptions
			.Where(item => string.IsNullOrEmpty(query)
				|| item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| item.Definition.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
			.GroupBy(item => item.Definition.Category)
			.Select(group => new TweakGroupViewModel(group.Key, group.ToArray()))
			.ToArray();
		TweakGroupsControl.ItemsSource = groups;
		UpdateTweakCount();
	}

	private void UpdateTweakCount()
	{
		if (TweakCountText == null)
			return;
		int selected = _tweakOptions.Count(item => item.IsSelected);
		int shown = _tweakOptions.Count(item => string.IsNullOrWhiteSpace(TweakSearchBox?.Text)
			|| item.Title.Contains(TweakSearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)
			|| item.Description.Contains(TweakSearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)
			|| item.Definition.Category.Contains(TweakSearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)
			|| item.Id.Contains(TweakSearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
		TweakCountText.Text = string.IsNullOrWhiteSpace(TweakSearchBox?.Text)
			? $"{selected} из {_tweakOptions.Count} выбрано"
			: $"{shown} найдено · {selected} выбрано";
	}

	private void PerformanceProfile_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not System.Windows.Controls.Button { Tag: string profile })
			return;

		WindowsTweakProfile selectedProfile = profile.ToLowerInvariant() switch
		{
			"medium" => WindowsTweakProfile.Medium,
			"ultra" => WindowsTweakProfile.Ultra,
			_ => WindowsTweakProfile.Low
		};
		HashSet<string> profileIds = new HashSet<string>(WindowsTweakCatalog.ProfileIds(selectedProfile), StringComparer.OrdinalIgnoreCase);
		foreach (TweakOptionViewModel option in _tweakOptions)
			option.IsSelected = profileIds.Contains(option.Id);
		UpdateTweakCount();

		System.Windows.Controls.Button[] buttons = { LowPerformanceButton, MediumPerformanceButton, UltraPerformanceButton };
		foreach (System.Windows.Controls.Button button in buttons)
		{
			bool active = string.Equals(button.Tag?.ToString(), profile, StringComparison.OrdinalIgnoreCase);
			button.Background = new SolidColorBrush(active ? System.Windows.Media.Color.FromRgb(38, 24, 50) : System.Windows.Media.Color.FromRgb(13, 13, 18));
			button.BorderBrush = new SolidColorBrush(active ? System.Windows.Media.Color.FromRgb(151, 82, 218) : System.Windows.Media.Color.FromRgb(48, 45, 54));
			button.BorderThickness = new Thickness(active ? 2 : 1);
		}
		SetStatus(profile + " Performance · список подготовлен, проверь его перед применением", ok: true);
	}

	private WindowsTweakPreferences ReadTweakControls()
	{
		HashSet<string> enabled = new HashSet<string>(_tweakOptions.Where(item => item.IsSelected).Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
		return new WindowsTweakPreferences
		{
			CatalogVersion = WindowsTweakCatalog.Version,
			EnabledTweakIds = enabled.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
			FastStartup = enabled.Contains("fast_startup"),
			RemoveStartupDelay = enabled.Contains("remove_startup_delay"),
			GamingPerformance = enabled.Contains("game_task_priority") || enabled.Contains("disable_xbox_dvr"),
			ReduceVisualEffects = enabled.Contains("performance_over_appearance"),
			DisableBackgroundApps = enabled.Contains("disable_background_apps"),
			ReduceTelemetry = enabled.Contains("disable_telemetry"),
			RemoveCopilot = enabled.Contains("disable_copilot"),
			RemoveConsumerApps = enabled.Contains("remove_consumer_apps"),
			RemoveXboxApps = enabled.Contains("remove_xbox_components"),
			DisableSysMain = enabled.Contains("disable_sysmain"),
			DisableWindowsSearch = enabled.Contains("disable_search_indexing"),
			DisablePrintSpooler = enabled.Contains("disable_print_spooler"),
			RemoveMicrosoftStore = enabled.Contains("remove_microsoft_store")
		};
	}

	private async void ApplyTweaks_Click(object sender, RoutedEventArgs e)
	{
		WindowsTweakPreferences preferences = ReadTweakControls();
		IReadOnlyList<string> validationErrors = _windowsTweaks.ValidateSelection(preferences.EnabledTweakIds);
		if (validationErrors.Count > 0)
		{
			System.Windows.MessageBox.Show(string.Join("\n", validationErrors), "AURA · Проверь выбор", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
        WindowsTweakDefinition[] selected = _windowsTweaks.Catalog.Where(item => preferences.EnabledTweakIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        TweakPreviewWindow preview = new(selected) { Owner = this };
        if (preview.ShowDialog() != true) return;

		_settings.Tweaks = preferences;
		SaveSettings();
		await RunTweaksOperationAsync(() => _windowsTweaks.ApplyAsync(_settings.Tweaks));
	}

	private async void RestoreTweaks_Click(object sender, RoutedEventArgs e)
	{
		MessageBoxResult confirmation = System.Windows.MessageBox.Show(
			"Восстановить сохранённые параметры только для изменений AURA? Несвязанные настройки и приложения изменяться не будут.",
			"AURA · Откат твиков",
			MessageBoxButton.YesNo,
			MessageBoxImage.Question);
		if (confirmation != MessageBoxResult.Yes)
			return;

		await RunTweaksOperationAsync(() => _windowsTweaks.RestoreDefaultsAsync());
	}

	private async Task RunTweaksOperationAsync(Func<Task<WindowsTweaksResult>> operation)
	{
        if (_tweaksCompletion != null) return;
        _tweaksCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		SetTweaksBusy(true, "ОЖИДАНИЕ UAC…", true);
		try
		{
			WindowsTweaksResult result = await operation();
            if (result.Report != null)
                foreach (TweakActionStatus action in result.Report.Actions)
                    _tweakOptions.FirstOrDefault(x => x.Id.Equals(action.Id, StringComparison.OrdinalIgnoreCase))?.UpdateStatus(action.Status);
			SetTweaksBusy(false, result.Success ? (result.RestartRecommended ? "ГОТОВО · НУЖНА ПЕРЕЗАГРУЗКА" : result.Report?.Actions.Any(x => x.Status == "Restored") == true ? "ВОССТАНОВЛЕНО" : "ГОТОВО · БЕЗ ИЗМЕНЕНИЙ") : "НЕ ВСЕ ШАГИ ВЫПОЛНЕНЫ", result.Success);
			SetStatus(result.Message, result.Success);
			if (result.Success && result.RestartRecommended)
			{
				System.Windows.MessageBox.Show(
					result.Message + "\n\nПерезагрузи компьютер, чтобы все изменения вступили в силу.",
					"AURA",
					MessageBoxButton.OK,
					MessageBoxImage.Information);
			}
		}
		catch (Exception exception)
		{
			SetTweaksBusy(false, "ОШИБКА", false);
			SetStatus("Твики Windows · " + exception.Message, false);
		}
        finally
        {
            _tweaksCompletion?.TrySetResult();
            _tweaksCompletion = null;
        }
	}

	private void SetTweaksBusy(bool busy, string text, bool ok)
	{
		ApplyTweaksButton.IsEnabled = !busy;
		RestoreTweaksButton.IsEnabled = !busy;
		TweakProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
		TweakStatusText.Text = text;
		System.Windows.Media.Color color = ok
			? System.Windows.Media.Color.FromRgb(119, 229, 170)
			: System.Windows.Media.Color.FromRgb(255, 139, 139);
		TweakStatusText.Foreground = new SolidColorBrush(color);
		TweakStatusDot.Fill = new SolidColorBrush(color);
	}

    private void TweakHistory_Click(object sender, RoutedEventArgs e) => new TweakHistoryWindow(_windowsTweaks.LoadHistory()) { Owner = this }.ShowDialog();

	private void OpenStartupApps_Click(object sender, RoutedEventArgs e)
	{
		Process.Start(new ProcessStartInfo("ms-settings:startupapps")
		{
			UseShellExecute = true
		});
	}

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        button.IsEnabled = false;
        try {
            using var check = new UpdateCheckService();
            var result = await check.CheckAsync(new Version(0, 11, 0));
            if (result.ReleasePage != null) System.Windows.Clipboard.SetText(result.ReleasePage.AbsoluteUri);
            SetStatus(result.Message, true);
        }
        catch (Exception exception) { SetStatus("Проверка обновлений: " + exception.Message, false); }
        finally { button.IsEnabled = true; }
    }

	private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
	{
		string directoryName = System.IO.Path.GetDirectoryName(_store.SettingsPath);
		if (!string.IsNullOrWhiteSpace(directoryName))
		{
			Process.Start(new ProcessStartInfo(directoryName)
			{
				UseShellExecute = true
			});
		}
	}

	private void CopySettingsPath_Click(object sender, RoutedEventArgs e)
	{
		System.Windows.Clipboard.SetText(_store.SettingsPath);
		SetStatus("Путь к настройкам скопирован", ok: true);
	}

    private async void Logout_Click(object sender, RoutedEventArgs e)
	{
		MessageBoxResult result = System.Windows.MessageBox.Show(
			"Выйти из аккаунта AURA на этом компьютере? Сама подписка и настройки останутся сохранены.",
			"AURA · Выход",
			MessageBoxButton.YesNo,
			MessageBoxImage.Question);
		if (result != MessageBoxResult.Yes)
		{
			return;
		}

        await SignOutSafelyAsync();
	}

    public AuthSession ActiveSession => _authSession;
    public void UpdateSubscriptionStatus(string message, bool success)
    {
        SubscriptionText.Text = _authSession.AuthMode == "Prototype" ? "прототип · тестовая активация" : message;
        if (!success) SetStatus(message, false);
    }
    public void RequestSafeClose() { _closing = true; Close(); }
    public async Task<bool> SignOutSafelyAsync()
    {
        if (_preparingShutdown) return false;
        _preparingShutdown = true;
        try {
            await OverclockView.StopAsync(); await DiagnosticsView.StopAsync();
            if (_tweaksCompletion != null) await _tweaksCompletion.Task;
            PersistSettings();
            _closing = true;
            await _signOut(); _shutdownPrepared = true; Close(); return true;
        }
        catch (Exception exception) { AppLogService.Error("Sign-out cleanup failed.", exception); SetStatus("Выход остановлен: " + exception.Message, false); return false; }
        finally { _preparingShutdown = false; }
    }

	private void UpdateMonitorLabels()
	{
		if ((object)_selectedMonitor != null)
		{
			if (SelectedMonitorSettingsText != null)
			{
				SelectedMonitorSettingsText.Text = _selectedMonitor.Label;
			}
			if (CrosshairMonitorText != null)
			{
				CrosshairMonitorText.Text = $"Монитор {_selectedMonitor.Index + 1}";
			}
		}
	}

	private void Startup_Changed(object sender, RoutedEventArgs e)
	{
		if (!_initializing)
		{
			_settings.StartWithWindows = StartupCheck.IsChecked == true;
			SetStartup(_settings.StartWithWindows);
			SaveSettings();
		}
	}

	private void Settings_Changed(object sender, RoutedEventArgs e)
	{
		if (!_initializing)
		{
			_settings.MinimizeToTray = TrayCheck.IsChecked == true;
			SaveSettings();
		}
	}

	private void Effects_Changed(object sender, RoutedEventArgs e)
	{
		if (!_initializing)
		{
            if (sender == ApplyOnStartCheck)
            {
                _settings.ApplyProfileOnStartup = ApplyOnStartCheck.IsChecked == true;
                SaveSettings();
                return;
            }
			bool flag = ((sender == EffectsEnabledCheck) ? (EffectsEnabledCheck.IsChecked == true) : (ApplyOnStartCheck.IsChecked == true));
			bool initializing = _initializing;
			_initializing = true;
			EffectsEnabledCheck.IsChecked = flag;
			_initializing = initializing;
			_settings.EffectsEnabled = flag;
			if (flag)
			{
				ApplyCurrent();
			}
			else if ((object)_selectedMonitor != null)
			{
                try { _engine.Reset(_selectedMonitor); SetStatus("Эффекты выключены", true); }
                catch (Exception exception) { AppLogService.Error("Effects restoration failed.", exception); SetStatus("Исходный цвет не восстановлен: " + exception.Message, false); }
			}
            PersistSettings();
		}
	}

	private void SetStartup(bool enabled)
	{
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
			if (enabled)
			{
				registryKey?.SetValue("AURA", "\"" + Environment.ProcessPath + "\"");
			}
			else
			{
				registryKey?.DeleteValue("AURA", throwOnMissingValue: false);
			}
		}
		catch (Exception ex)
		{
			SetStatus("Автозапуск · " + ex.Message, ok: false);
		}
	}

	private NotifyIcon BuildTrayIcon()
	{
		ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
		contextMenuStrip.Items.Add("Открыть AURA", null, delegate
		{
			base.Dispatcher.Invoke(RestoreWindow);
		});
		contextMenuStrip.Items.Add("Сбросить выбранный монитор", null, delegate
		{
			base.Dispatcher.Invoke(delegate
			{
				Reset_Click(this, new RoutedEventArgs());
			});
		});
		contextMenuStrip.Items.Add("Переключить прицел", null, delegate
		{
			base.Dispatcher.Invoke(delegate
			{
				_settings.Crosshair.Enabled = !_settings.Crosshair.Enabled;
				CrosshairEnabledCheck.IsChecked = _settings.Crosshair.Enabled;
				_crosshair.UpdateSettings(_settings.Crosshair);
				SaveSettings();
			});
		});
		contextMenuStrip.Items.Add((ToolStripItem)new ToolStripSeparator());
		contextMenuStrip.Items.Add("Выход", null, delegate
		{
			base.Dispatcher.Invoke(delegate
			{
				_closing = true;
				Close();
			});
		});
		NotifyIcon notifyIcon = new NotifyIcon();
		try
		{
			notifyIcon.Icon = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
				? System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath)
				: SystemIcons.Application;
		}
		catch
		{
			notifyIcon.Icon = SystemIcons.Application;
		}
		notifyIcon.Text = "AURA Display Lab";
		notifyIcon.ContextMenuStrip = contextMenuStrip;
		notifyIcon.Visible = true;
		notifyIcon.DoubleClick += delegate
		{
			base.Dispatcher.Invoke(RestoreWindow);
		};
		return notifyIcon;
	}

	private void MainWindow_StateChanged(object? sender, EventArgs e)
	{
		if (base.WindowState == WindowState.Minimized && _settings.MinimizeToTray)
		{
			Hide();
		}
	}

	private void RestoreWindow()
	{
		Show();
		base.WindowState = WindowState.Normal;
		Activate();
	}

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
	{
		if (!_closing && _settings.MinimizeToTray)
		{
			e.Cancel = true;
			Hide();
		}
		else
		{
            if (!_shutdownPrepared)
            {
                e.Cancel = true;
                if (_preparingShutdown) return;
                _preparingShutdown = true;
                try
                {
                    await OverclockView.StopAsync();
                    await DiagnosticsView.StopAsync();
                    if (_tweaksCompletion != null) await _tweaksCompletion.Task;
                    _shutdownPrepared = true;
                    _closing = true;
                    Close();
                }
                catch (Exception exception)
                {
                    AppLogService.Error("Shutdown preparation failed.", exception);
                    SetStatus("Выход остановлен: " + exception.Message, false);
                }
                finally { _preparingShutdown = false; }
                return;
            }
			PersistSettings();
		}
	}

	private void MainWindow_Closed(object? sender, EventArgs e)
	{
        _saveTimer.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        _monitor.Dispose();
		_crosshair.Close();
		DiagnosticsView.Dispose();
		OverclockView.Dispose();
		_hardware.Dispose();
		_tray.Visible = false;
		_tray.Dispose();
	}

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_confirmingDisplayMode || _refreshingDisplays || _shutdownPrepared) return;
            _refreshingDisplays = true;
            bool previous = _initializing;
            _initializing = true;
            try
            {
                string? selected = _selectedMonitor?.DeviceName;
                _monitors = MonitorService.GetMonitors();
                _selectedMonitor = _monitors.FirstOrDefault(x => x.DeviceName == selected) ?? _monitors.FirstOrDefault();
                MonitorCombo.ItemsSource = _monitors;
                MonitorCombo.SelectedItem = _selectedMonitor;
                _settings.SelectedMonitorDeviceName = _selectedMonitor?.DeviceName ?? string.Empty;
                _crosshair.SetMonitor(_selectedMonitor?.DeviceName);
                LoadDisplayModes(); UpdateMonitorLabels();
                if (_selectedMonitor != null) LoadProfileIntoControls(_gameProfiles.Manual(_selectedMonitor.DeviceName));
                SaveSettings();
                if (_selectedMonitor == null) SetStatus("Монитор отключён", false);
            }
            finally { _initializing = previous; _refreshingDisplays = false; }
        }));
    }

	private void SetStatus(string text, bool ok)
	{
		StatusText.Text = text;
		StatusDot.Fill = (ok ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(98, 214, 155)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(byte.MaxValue, 124, 124)));
	}

	private void SaveSettings()
	{
        _monitor?.UpdateBindings(_settings.Games);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void PersistSettings()
    {
        _saveTimer.Stop();
		try
		{
			_store.Save(_settings);
		}
		catch (Exception ex)
		{
			SetStatus("Ошибка сохранения · " + ex.Message, ok: false);
		}
	}

}
