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

	private readonly Action _signOut;

	private readonly ProfileMonitor _monitor;

	private readonly WindowsTweaksService _windowsTweaks = new WindowsTweaksService();

	private readonly NvidiaGameProfileService _nvidiaGameProfiles = new NvidiaGameProfileService();

	private readonly HardwareMonitoringService _hardware = new HardwareMonitoringService();

	private readonly CrosshairWindow _crosshair;

	private readonly NotifyIcon _tray;

	private readonly SliderField[] _colorFields;

	private readonly System.Windows.Controls.Button[] _sceneButtons;

	private readonly List<TweakOptionViewModel> _tweakOptions = new List<TweakOptionViewModel>();

	private List<MonitorTarget> _monitors = new List<MonitorTarget>();

	private MonitorTarget? _selectedMonitor;

	private bool _initializing = true;

	private bool _closing;

	private string? _activeGame;


	public MainWindow(AppSettings settings, SettingsStore store, ColorEngine engine, AuthSession authSession, Action signOut)
	{
		_settings = settings;
		_store = store;
		_engine = engine;
		_authSession = authSession;
		_signOut = signOut;
		InitializeComponent();
		AccountNameText.Text = authSession.Email;
		SubscriptionText.Text = $"активна · {authSession.DaysRemaining} дн.";
		SettingsAccountEmailText.Text = authSession.Email;
		SettingsSubscriptionText.Text = $"{authSession.PlanName} · до {authSession.ExpiresAtUtc.ToLocalTime():dd.MM.yyyy}";
		_colorFields = new SliderField[8] { SaturationField, VibranceField, BrightnessField, GammaField, ContrastField, TemperatureField, HueField, ShadowsField };
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
		ColorBlindCombo.ItemsSource = Enum.GetNames<ColorBlindMode>();
		CrosshairShapeCombo.ItemsSource = Enum.GetNames<CrosshairShape>();
		GameGrid.ItemsSource = _settings.Games;
		UpdateGamesSummary();
		LoadMonitors();
		DiagnosticsView.Initialize(_hardware, GetSelectedDisplayMode);
		OverclockView.Initialize(_hardware, _settings, _store);
		_crosshair = new CrosshairWindow();
		_crosshair.SetMonitor(_selectedMonitor?.DeviceName);
		_monitor = new ProfileMonitor(() => _settings.Games);
		_monitor.ActiveGameChanged += delegate(GameBinding? binding)
		{
			base.Dispatcher.Invoke(delegate
			{
				OnActiveGameChanged(binding);
			});
		};
		_tray = BuildTrayIcon();
		LoadProfileIntoControls(((object)_selectedMonitor != null && _settings.MonitorProfiles.TryGetValue(_selectedMonitor.DeviceName, out DisplayProfile value)) ? value : (_settings.SelectedProfile ?? _settings.Current));
		LoadDisplayModes();
		LoadCrosshairControls();
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
		if (_settings.EffectsEnabled)
		{
			ApplyCurrent();
		}
		_crosshair.UpdateSettings(_settings.Crosshair);
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
			DisplayProfile value;
			DisplayProfile profile = (_settings.MonitorProfiles.TryGetValue(monitorTarget.DeviceName, out value) ? value : (_settings.SelectedProfile ?? _settings.Current));
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
			_settings.MonitorProfiles[_selectedMonitor.DeviceName] = _settings.Current.Clone();
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
			ApplyCurrent();
		}
	}

	private void ColorBlindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_initializing)
		{
			ReadColorControls();
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
		current.Hue = (int)HueField.Value;
		current.Shadows = (int)ShadowsField.Value;
		current.ColorBlind = (Enum.TryParse<ColorBlindMode>(ColorBlindCombo.SelectedItem?.ToString(), out var result) ? result : ColorBlindMode.Off);
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
		HueField.Value = profile.Hue;
		ShadowsField.Value = profile.Shadows;
		ColorBlindCombo.SelectedItem = profile.ColorBlind.ToString();
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
		_engine.Apply(_settings.Current, _selectedMonitor);
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
			_engine.Reset(_selectedMonitor);
		}
		DisplayProfile displayProfile = _settings.Profiles.FirstOrDefault((DisplayProfile x) => x.Name == "Natural") ?? new DisplayProfile();
		LoadProfileIntoControls(displayProfile);
		_settings.SelectedProfileName = displayProfile.Name;
		StoreCurrentForMonitor();
		SetStatus("Выбранный монитор сброшен", ok: true);
		SaveSettings();
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
		if (binding == null)
		{
			if (_activeGame != null)
			{
				_activeGame = null;
				DisplayProfile selectedProfile = _settings.SelectedProfile;
				if (selectedProfile != null)
				{
					ApplyProfile(selectedProfile, select: false);
				}
			}
		}
		else
		{
			_activeGame = binding.ProcessName;
			DisplayProfile displayProfile = _settings.Profiles.FirstOrDefault((DisplayProfile x) => x.Name == binding.ProfileName);
			if (displayProfile != null)
			{
				ApplyProfile(displayProfile, select: false);
				SetStatus(binding.ProcessName + " · " + displayProfile.Name, ok: true);
			}
		}
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
		System.Drawing.Color color = System.Drawing.Color.FromArgb(_settings.Crosshair.Argb);
		SolidColorBrush solidColorBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
		CrosshairPreviewHorizontal.Fill = solidColorBrush;
		CrosshairPreviewVertical.Fill = solidColorBrush;
		CrosshairPreviewDot.Fill = solidColorBrush;
		CrosshairPreviewCircle.Stroke = solidColorBrush;
		CrosshairColorSwatch.Background = solidColorBrush;
		CrosshairPreviewHorizontal.Height = Math.Max(1, _settings.Crosshair.Thickness);
		CrosshairPreviewVertical.Width = Math.Max(1, _settings.Crosshair.Thickness);
		Ellipse crosshairPreviewDot = CrosshairPreviewDot;
		double width = (CrosshairPreviewDot.Height = Math.Max(3, _settings.Crosshair.Thickness + 1));
		crosshairPreviewDot.Width = width;
		CrosshairPreviewHorizontal.Width = Math.Min(90, _settings.Crosshair.Size * 2 + _settings.Crosshair.Gap * 2 + 16);
		CrosshairPreviewVertical.Height = Math.Min(90, _settings.Crosshair.Size * 2 + _settings.Crosshair.Gap * 2 + 16);
		Ellipse crosshairPreviewCircle = CrosshairPreviewCircle;
		width = (CrosshairPreviewCircle.Height = Math.Min(72, Math.Max(12, _settings.Crosshair.Size * 2)));
		crosshairPreviewCircle.Width = width;
		System.Windows.Shapes.Rectangle crosshairPreviewHorizontal = CrosshairPreviewHorizontal;
		CrosshairShape shape = _settings.Crosshair.Shape;
		bool flag = ((shape == CrosshairShape.Cross || shape == CrosshairShape.TShape) ? true : false);
		crosshairPreviewHorizontal.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		crosshairPreviewHorizontal = CrosshairPreviewVertical;
		shape = _settings.Crosshair.Shape;
		flag = ((shape == CrosshairShape.Cross || shape == CrosshairShape.TShape) ? true : false);
		crosshairPreviewHorizontal.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		CrosshairPreviewDot.Visibility = ((_settings.Crosshair.Shape != CrosshairShape.Dot) ? Visibility.Collapsed : Visibility.Visible);
		CrosshairPreviewCircle.Visibility = ((_settings.Crosshair.Shape != CrosshairShape.Circle) ? Visibility.Collapsed : Visibility.Visible);
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
		if ((object)_selectedMonitor != null)
		{
			bool flag = DisplayModeService.Apply(_selectedMonitor.DeviceName, mode, _settings.StretchScaling);
			SetStatus(flag ? $"Разрешение · {mode}{(_settings.StretchScaling ? " · во весь экран" : string.Empty)}" : "Windows отклонила этот режим", flag);
			if (flag)
			{
				CustomWidthBox.Text = mode.Width.ToString();
				CustomHeightBox.Text = mode.Height.ToString();
				CustomHzBox.Text = mode.RefreshRate.ToString();
				DisplayModeCombo.SelectedItem = mode;
				ScalingStatusText.Text = (_settings.StretchScaling ? "Полноэкранное растягивание применено к выбранному монитору." : "Используется масштабирование драйвера по умолчанию.");
				ScalingStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(101, 223, 154));
			}
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
		if ((object)_selectedMonitor != null)
		{
			DisplayMode currentMode = DisplayModeService.GetCurrentMode(_selectedMonitor.DeviceName);
			if ((object)currentMode == null)
			{
				SetStatus("Не удалось прочитать текущий режим монитора", ok: false);
				return;
			}
			bool flag = DisplayModeService.Apply(_selectedMonitor.DeviceName, currentMode, _settings.StretchScaling);
			ScalingStatusText.Text = ((!flag) ? "Драйвер видеокарты отклонил настройку масштабирования." : (_settings.StretchScaling ? "Растягивание во весь экран включено." : "Вернули масштабирование драйвера по умолчанию."));
			ScalingStatusText.Foreground = new SolidColorBrush(flag ? System.Windows.Media.Color.FromRgb(101, 223, 154) : System.Windows.Media.Color.FromRgb(byte.MaxValue, 124, 124));
			SetStatus(ScalingStatusText.Text, flag);
		}
	}

	private void ApplyStretched_Click(object sender, RoutedEventArgs e)
	{
		if ((object)_selectedMonitor != null)
		{
			DisplayMode displayMode = DisplayModeService.FindClosest(_selectedMonitor.DeviceName, 1440, 1080, 180);
			if ((object)displayMode == null)
			{
				SetStatus("1440×1080 не зарегистрировано в Windows", ok: false);
			}
			else
			{
				ApplyDisplayMode(displayMode);
			}
		}
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
		ApplyOnStartCheck.IsChecked = _settings.EffectsEnabled;
		EffectsEnabledCheck.IsChecked = _settings.EffectsEnabled;
		StretchScalingCheck.IsChecked = _settings.StretchScaling;
		LoadTweakControls();
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
		WindowsTweakDefinition[] dangerous = _windowsTweaks.Catalog
			.Where(item => item.Risk == WindowsTweakRisk.Danger && preferences.EnabledTweakIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
			.ToArray();
		string dangerText = dangerous.Length == 0
			? string.Empty
			: "\n\nОПАСНЫЕ ПУНКТЫ:\n• " + string.Join("\n• ", dangerous.Select(item => item.Title)) + "\n\nОни могут отключить функции безопасности или устройства.";
		MessageBoxResult confirmation = System.Windows.MessageBox.Show(
			$"AURA применит выбранные пункты: {preferences.EnabledTweakIds.Count}. Перед изменениями будет создан снимок параметров и предпринята попытка создать точку восстановления.{dangerText}\n\nПродолжить?",
			"AURA · Твики Windows",
			MessageBoxButton.YesNo,
			MessageBoxImage.Warning);
		if (confirmation != MessageBoxResult.Yes)
			return;

		_settings.Tweaks = preferences;
		SaveSettings();
		await RunTweaksOperationAsync(() => _windowsTweaks.ApplyAsync(_settings.Tweaks));
	}

	private async void RestoreTweaks_Click(object sender, RoutedEventArgs e)
	{
		MessageBoxResult confirmation = System.Windows.MessageBox.Show(
			"Вернуть стандартные параметры служб и повторно зарегистрировать встроенные приложения Windows?",
			"AURA · Откат твиков",
			MessageBoxButton.YesNo,
			MessageBoxImage.Question);
		if (confirmation != MessageBoxResult.Yes)
			return;

		await RunTweaksOperationAsync(() => _windowsTweaks.RestoreDefaultsAsync());
	}

	private async Task RunTweaksOperationAsync(Func<Task<WindowsTweaksResult>> operation)
	{
		SetTweaksBusy(true, "ОЖИДАНИЕ UAC…", true);
		try
		{
			WindowsTweaksResult result = await operation();
			SetTweaksBusy(false, result.Success ? "ГОТОВО · НУЖНА ПЕРЕЗАГРУЗКА" : "ОПЕРАЦИЯ НЕ ВЫПОЛНЕНА", result.Success);
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

	private void OpenStartupApps_Click(object sender, RoutedEventArgs e)
	{
		Process.Start(new ProcessStartInfo("ms-settings:startupapps")
		{
			UseShellExecute = true
		});
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

	private void Logout_Click(object sender, RoutedEventArgs e)
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

		_closing = true;
		_signOut();
		Close();
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
			bool flag = ((sender == EffectsEnabledCheck) ? (EffectsEnabledCheck.IsChecked == true) : (ApplyOnStartCheck.IsChecked == true));
			bool initializing = _initializing;
			_initializing = true;
			EffectsEnabledCheck.IsChecked = flag;
			ApplyOnStartCheck.IsChecked = flag;
			_initializing = initializing;
			_settings.EffectsEnabled = flag;
			if (flag)
			{
				ApplyCurrent();
			}
			else if ((object)_selectedMonitor != null)
			{
				_engine.Reset(_selectedMonitor);
				SetStatus("Эффекты выключены", ok: true);
			}
			SaveSettings();
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

	private void MainWindow_Closing(object? sender, CancelEventArgs e)
	{
		if (!_closing && _settings.MinimizeToTray)
		{
			e.Cancel = true;
			Hide();
		}
		else
		{
			SaveSettings();
		}
	}

	private void MainWindow_Closed(object? sender, EventArgs e)
	{
		_monitor.Dispose();
		_crosshair.Close();
		DiagnosticsView.Dispose();
		OverclockView.Dispose();
		_hardware.Dispose();
		_tray.Visible = false;
		_tray.Dispose();
	}

	private void SetStatus(string text, bool ok)
	{
		StatusText.Text = text;
		StatusDot.Fill = (ok ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(98, 214, 155)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(byte.MaxValue, 124, 124)));
	}

	private void SaveSettings()
	{
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
