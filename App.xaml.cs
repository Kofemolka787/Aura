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

	private readonly IAuthService _authService = new DemoAuthService();

	private readonly LicenseStore _licenseStore = new LicenseStore();


	protected override async void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		AppLogService.Initialize();
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
				$"AURA столкнулась с критической ошибкой и будет закрыта.\n\nДиагностический журнал:\n{AppLogService.LogPath}",
				"AURA",
				MessageBoxButton.OK,
				MessageBoxImage.Hand);
			Shutdown(-1);
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
					OpenMainWindow(session, null);
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
		authWindow.AuthenticationSucceeded += delegate(AuthSession session)
		{
			try
			{
				_licenseStore.Save(session);
				OpenMainWindow(session, authWindow);
			}
			catch (Exception exception)
			{
				AppLogService.Error("Could not persist activation session.", exception);
				MessageBox.Show("Не удалось безопасно сохранить активацию: " + exception.Message, "AURA", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		};
		authWindow.Show();
	}

	private void OpenMainWindow(AuthSession session, AuthWindow? authWindow)
	{
		SettingsStore settingsStore = new SettingsStore();
		AppSettings settings = settingsStore.Load();
		_engine = new ColorEngine();
		MainWindow mainWindow = new MainWindow(settings, settingsStore, _engine, session, SignOut);
		MainWindow = mainWindow;
		mainWindow.Show();
		authWindow?.Close();
	}

	private void SignOut()
	{
		_licenseStore.Clear();
		_engine?.Dispose();
		_engine = null;
		ShowAuthWindow("Ты вышел из аккаунта. Для продолжения войди снова.");
	}

	protected override void OnExit(ExitEventArgs e)
	{
		AppLogService.Info($"AURA exiting with code {e.ApplicationExitCode}.");
		_engine?.Dispose();
		_singleInstance?.Dispose();
		base.OnExit(e);
	}

}
