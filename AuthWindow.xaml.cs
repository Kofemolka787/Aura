using System;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ColorForge.Models;
using ColorForge.Services;

namespace ColorForge;

public partial class AuthWindow : Window
{
	private readonly IAuthService _authService;

	private string? _loginToken;

	private bool _busy;
    private readonly CancellationTokenSource _lifetime = new();

	public event Action<AuthSession>? AuthenticationSucceeded;

	public AuthWindow(IAuthService authService, string? startupMessage = null)
	{
		_authService = authService;
		InitializeComponent();
        Closed += (_, _) => _lifetime.Cancel();
        bool demo = authService is DemoAuthService;
        DemoLoginPanel.Visibility = DemoLicensePanel.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
        if (demo) ShowLoginMessage("ПРОТОТИП: тестовая активация, коммерческий сервер не подключён.");
		DeviceIdText.Text = "УСТРОЙСТВО · " + DeviceIdentityService.GetShortDeviceId();
		if (!string.IsNullOrWhiteSpace(startupMessage))
		{
			ShowLoginMessage(startupMessage);
		}
		Loaded += delegate
		{
			EmailTextBox.Focus();
		};
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 2)
		{
			WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
		}
		else
		{
			DragMove();
		}
	}

	private void Minimize_Click(object sender, RoutedEventArgs e)
	{
		WindowState = WindowState.Minimized;
	}

	private void Close_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void FillDemoLogin_Click(object sender, RoutedEventArgs e)
	{
		EmailTextBox.Text = DemoAuthService.DemoEmail;
		PasswordInput.Password = DemoAuthService.DemoPassword;
		HideLoginMessage();
		PasswordInput.Focus();
	}

	private void FillDemoKey_Click(object sender, RoutedEventArgs e)
	{
        if (_busy) return;
		LicenseKeyTextBox.Text = DemoAuthService.DemoLicenseKey;
		HideLicenseMessage();
		LicenseKeyTextBox.Focus();
		LicenseKeyTextBox.CaretIndex = LicenseKeyTextBox.Text.Length;
	}

	private async void Login_Click(object sender, RoutedEventArgs e)
	{
		if (_busy)
		{
			return;
		}
		string email = EmailTextBox.Text.Trim();
		string password = PasswordInput.Password;
		if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(password))
		{
			ShowLoginMessage("Введи корректный email и пароль.");
			return;
		}

		SetBusy(true, LoginButton, "Проверяем аккаунт…");
		try
		{
			LoginResult result = await _authService.LoginAsync(email, password, _lifetime.Token);
			if (result.Success && result.Session != null) { PasswordInput.Clear(); AuthenticationSucceeded?.Invoke(result.Session); return; }
            if (!result.Success || string.IsNullOrWhiteSpace(result.LoginToken))
			{
				ShowLoginMessage(result.Message);
				return;
			}
			PasswordInput.Clear();
            _loginToken = result.LoginToken;
			VerifiedEmailText.Text = result.Email ?? email;
			ShowLicenseStep();
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
		{
			AppLogService.Error("Account login failed.", exception);
			ShowLoginMessage("Не удалось связаться с сервером: " + exception.Message);
		}
		finally
		{
			SetBusy(false, LoginButton, "Войти в аккаунт");
		}
	}

	private async void Activate_Click(object sender, RoutedEventArgs e)
	{
		if (_busy)
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(_loginToken))
		{
			ShowLoginStep("Сессия входа устарела. Войди ещё раз.");
			return;
		}
		string key = LicenseKeyTextBox.Text.Trim();
		if (key.Length < 10)
		{
			ShowLicenseMessage("Введи ключ подписки полностью.");
			return;
		}

		SetBusy(true, ActivateButton, "Активируем подписку…");
		try
		{
			ActivationResult result = await _authService.ActivateAsync(_loginToken, key, DeviceIdentityService.GetDeviceId(), _lifetime.Token);
			if (!result.Success || result.Session == null)
			{
				ShowLicenseMessage(result.Message);
				return;
			}

			LicenseMessageBorder.Visibility = Visibility.Visible;
			LicenseMessageBorder.Background = new SolidColorBrush(Color.FromRgb(20, 45, 34));
			LicenseMessageBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 116, 82));
			LicenseMessageText.Foreground = new SolidColorBrush(Color.FromRgb(116, 232, 165));
			LicenseMessageText.Text = result.Message;
			await Task.Delay(360, _lifetime.Token);
			AuthenticationSucceeded?.Invoke(result.Session);
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
		{
			AppLogService.Error("License activation failed.", exception);
			ShowLicenseMessage("Не удалось активировать ключ: " + exception.Message);
		}
		finally
		{
			SetBusy(false, ActivateButton, "Активировать на этом ПК");
		}
	}

	private void BackToLogin_Click(object sender, RoutedEventArgs e)
	{
        if (_busy) return;
		ShowLoginStep(null);
	}

	private void ShowLicenseStep()
	{
		HideLoginMessage();
		LoginPanel.Visibility = Visibility.Collapsed;
		LicensePanel.Visibility = Visibility.Visible;
		StepOneBadge.Background = new SolidColorBrush(Color.FromRgb(42, 105, 75));
		StepOneBadge.Child = new System.Windows.Controls.TextBlock
		{
			Text = "✓",
			FontWeight = FontWeights.Bold,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		StepLine.Fill = new SolidColorBrush(Color.FromRgb(105, 79, 139));
		StepTwoBadge.Background = new SolidColorBrush(Color.FromRgb(141, 56, 255));
		if (StepTwoBadge.Child is System.Windows.Controls.TextBlock text)
		{
			text.Foreground = Brushes.White;
		}
		LicenseKeyTextBox.Focus();
	}

	private void ShowLoginStep(string? message)
	{
		_loginToken = null;
		LicensePanel.Visibility = Visibility.Collapsed;
		LoginPanel.Visibility = Visibility.Visible;
		StepOneBadge.Background = new SolidColorBrush(Color.FromRgb(141, 56, 255));
		StepOneBadge.Child = new System.Windows.Controls.TextBlock
		{
			Text = "1",
			FontWeight = FontWeights.Bold,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		StepLine.Fill = new SolidColorBrush(Color.FromRgb(54, 49, 60));
		StepTwoBadge.Background = new SolidColorBrush(Color.FromRgb(38, 35, 43));
		if (StepTwoBadge.Child is System.Windows.Controls.TextBlock text)
		{
			text.Foreground = new SolidColorBrush(Color.FromRgb(142, 134, 152));
		}
		if (!string.IsNullOrWhiteSpace(message))
		{
			ShowLoginMessage(message);
		}
		EmailTextBox.Focus();
	}

	private void SetBusy(bool busy, System.Windows.Controls.Button button, string text)
	{
		_busy = busy;
		button.IsEnabled = !busy;
		button.Content = text;
		EmailTextBox.IsEnabled = !busy;
		PasswordInput.IsEnabled = !busy;
		LicenseKeyTextBox.IsEnabled = !busy;
	}

	private void ShowLoginMessage(string message)
	{
		LoginMessageText.Text = message;
		LoginMessageBorder.Visibility = Visibility.Visible;
	}

	private void HideLoginMessage()
	{
		LoginMessageBorder.Visibility = Visibility.Collapsed;
	}

	private void ShowLicenseMessage(string message)
	{
		LicenseMessageBorder.Background = new SolidColorBrush(Color.FromRgb(51, 27, 31));
		LicenseMessageBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(113, 64, 74));
		LicenseMessageText.Foreground = new SolidColorBrush(Color.FromRgb(255, 154, 168));
		LicenseMessageText.Text = message;
		LicenseMessageBorder.Visibility = Visibility.Visible;
	}

	private void HideLicenseMessage()
	{
		LicenseMessageBorder.Visibility = Visibility.Collapsed;
	}
}
