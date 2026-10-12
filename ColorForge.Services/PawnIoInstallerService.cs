using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ColorForge.Services;

public sealed record PawnIoInstallResult(bool Installed, bool RebootRequired, string Version);

public sealed class PawnIoInstallerService
{
    private const string InstallerHash = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";
    public const string BundledVersion = "2.2.0";
    public const string BundledModulesVersion = "0.2.11";

    public bool IsInstalled => PawnIoBridge.IsInstalled;

    public async Task<PawnIoInstallResult> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (IsInstalled)
            return new PawnIoInstallResult(true, false, BundledVersion);

        string installer = EmbeddedToolExtractor.Extract("PawnIO_setup.exe", "PawnIO_setup.exe", InstallerHash);
        ProcessStartInfo info = new ProcessStartInfo
        {
            FileName = installer,
            Arguments = "-install -silent",
            UseShellExecute = true,
            Verb = "runas"
        };
        try
        {
            using Process process = Process.Start(info)
                ?? throw new InvalidOperationException("Не удалось запустить официальный установщик PawnIO.");
            await process.WaitForExitAsync(cancellationToken);
            bool rebootRequired = process.ExitCode == 3010;
            if (process.ExitCode != 0 && !rebootRequired)
                throw new InvalidOperationException($"Установщик PawnIO вернул код {process.ExitCode}.");
            bool installed = PawnIoBridge.IsInstalled;
            return new PawnIoInstallResult(installed, rebootRequired || !installed, BundledVersion);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Установка PawnIO отменена в окне UAC.", exception, cancellationToken);
        }
    }
}
