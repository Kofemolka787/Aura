using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ColorForge.Models;
using ColorForge.Services;

namespace ColorForge;

public partial class App : Application
{
	private Mutex? _singleInstance;

	private ColorEngine? _engine;

	private IAuthService _authService = null!;
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private bool _validatingSession;

	private readonly LicenseStore _licenseStore = new LicenseStore();


	protected override async void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		AppLogService.Initialize();
        try { _authService = AuthClientConfiguration.Load().CreateService(); }
        catch (Exception exception) {
            AppLogService.Error("Invalid embedded auth configuration.", exception);
            MessageBox.Show("Конфигурация авторизации этой сборки повреждена: " + exception.Message, "AURA", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-3); return;
        }
		string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
		AppLogService.Info($"AURA {version} starting on {Environment.OSVersion}; 64-bit process: {Environment.Is64BitProcess}.");
		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
		{
			if (args.ExceptionObject is Exception exception)
				AppLogService.Error("Unhandled AppDomain exception.", exception);
			else
				AppLogService.Warning("Unhandled non-Exception AppDomain failure.");
		};
		TaskScheduler.UnobservedTaskException += (_, args) =>
		{
			AppLogService.Error("Unobserved task exception.", args.Exception);
			args.SetObserved();
		};
		_singleInstance = new Mutex(initiallyOwned: true, "Aura.DisplayLab.SingleInstance", out var createdNew);
		if (!createdNew)
		{
			MessageBox.Show("AURA уже запущена.", "AURA", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			Shutdown();
			return;
		}
		base.DispatcherUnhandledException += delegate(object _, DispatcherUnhandledExceptionEventArgs args)
		{
			AppLogService.Error("Unhandled UI exception.", args.Exception);
			args.Handled = true;
			MessageBox.Show(
				$"AURA столкнулась с критической ошибкой. Перед выходом она остановит фоновые операции; при ошибке аппаратного возврата окно останется открытым.\n\nДиагностический журнал:\n{AppLogService.LogPath}",
				"AURA",
				MessageBoxButton.OK,
				MessageBoxImage.Hand);
            if (MainWindow is MainWindow main) main.RequestSafeClose();
            else Shutdown(-1);
		};

		WindowsTweaksAudit tweakAudit = WindowsTweaksService.AuditCatalog();
		if (!tweakAudit.Success)
		{
			string detail = string.Join(Environment.NewLine, tweakAudit.Errors);
			AppLogService.Warning("Windows tweak catalog validation failed: " + detail);
			MessageBox.Show("Проверка каталога твиков не пройдена. AURA будет закрыта.\n\n" + detail, "AURA", MessageBoxButton.OK, MessageBoxImage.Error);
			Shutdown(-2);
			return;
		}
		AppLogService.Info($"Windows tweak catalog validated: {tweakAudit.DefinitionCount} definitions.");

		AuthSession? session = _licenseStore.Load();
		if (session != null)
		{
			try
			{
				SessionValidationResult validation = await _authService.ValidateSessionAsync(session);
				if (validation.Success)
				{
					_licenseStore.Save(session);
                    await OpenMainWindowAsync(session, null);
					return;
				}
				_licenseStore.Clear();
				ShowAuthWindow(validation.Message);
				return;
			}
			catch (Exception exception)
			{
				AppLogService.Error("Subscription validation failed.", exception);
				ShowAuthWindow("Не удалось проверить подписку: " + exception.Message);
				return;
			}
		}

		ShowAuthWindow(null);
	}

	private void ShowAuthWindow(string? message)
	{
		AuthWindow authWindow = new AuthWindow(_authService, message);
		MainWindow = authWindow;
		authWindow.AuthenticationSucceeded += async delegate(AuthSession session)
		{
            authWindow.IsEnabled = false;
			try
			{
				_licenseStore.Save(session);
				await OpenMainWindowAsync(session, authWindow);
			}
			catch (Exception exception)
			{
				AppLogService.Error("Could not persist activation session.", exception);
				MessageBox.Show("Не удалось открыть AURA: " + exception.Message, "AURA", MessageBoxButton.OK, MessageBoxImage.Error);
			}
            finally { if (authWindow.IsLoaded) authWindow.IsEnabled = true; }
		};
		authWindow.Show();
	}

	private async Task OpenMainWindowAsync(AuthSession session, AuthWindow? authWindow)
	{
		SettingsStore settingsStore = new SettingsStore();
		AppSettings settings = settingsStore.Load();
        HardwareMonitoringService hardware = await Task.Run(() => new HardwareMonitoringService());
        if (authWindow != null && !authWindow.IsLoaded) { hardware.Dispose(); return; }
		_engine = new ColorEngine();
        MainWindow mainWindow;
        try { mainWindow = new MainWindow(settings, settingsStore, _engine, session, SignOutAsync, hardware); }
        catch { hardware.Dispose(); DisposeEngine(); throw; }
		MainWindow = mainWindow;
		mainWindow.Show();
        _sessionTimer.Stop();
        _sessionTimer.Tick -= ValidateActiveSession;
        _sessionTimer.Tick += ValidateActiveSession;
        _sessionTimer.Start();
		authWindow?.Close();
	}

	private async Task SignOutAsync()
	{
		_sessionTimer.Stop();
        if (MainWindow is MainWindow main) {
            try { await _authService.RevokeSessionAsync(main.ActiveSession); }
            catch (Exception exception) { AppLogService.Warning("Remote logout unconfirmed: " + exception.GetType().Name); }
        }
        _licenseStore.Clear();
        DisposeEngine();
		_engine = null;
		ShowAuthWindow("Ты вышел из аккаунта. Для продолжения войди снова.");
	}

    private async void ValidateActiveSession(object? sender, EventArgs args)
    {
        if (_validatingSession || MainWindow is not MainWindow main) return;
        _validatingSession = true;
        try {
            var result = await _authService.ValidateSessionAsync(main.ActiveSession);
            main.UpdateSubscriptionStatus(result.Message, result.Success);
            if (result.Success) _licenseStore.Save(main.ActiveSession);
            else await main.SignOutSafelyAsync();
        }
        catch (Exception exception) {
            AppLogService.Error("Periodic license validation failed.", exception);
            main.UpdateSubscriptionStatus("Проверка лицензии не пройдена.", false);
            await main.SignOutSafelyAsync();
        }
        finally { _validatingSession = false; }
    }
    private void DisposeEngine()
    {
        try { _engine?.Dispose(); }
        catch (Exception exception) { AppLogService.Error("Display baseline restoration failed; recovery snapshot retained.", exception); }
        _engine = null;
    }

	protected override void OnExit(ExitEventArgs e)
	{
		AppLogService.Info($"AURA exiting with code {e.ApplicationExitCode}.");
		_sessionTimer.Stop();
        DisposeEngine();
        (_authService as IDisposable)?.Dispose();
		_singleInstance?.Dispose();
		base.OnExit(e);
	}

}
