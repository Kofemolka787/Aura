using System;
using System.IO;
using System.Text;

namespace ColorForge.Services;

public static class AppLogService
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static bool _initialized;

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AURA",
        "Logs");

    public static string LogPath => Path.Combine(DirectoryPath, "aura.log");

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
                return;

            try { Directory.CreateDirectory(DirectoryPath); RotateIfNeeded(); }
            catch { return; }
            _initialized = true;
        }

        Info("Application logging initialized.");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warning(string message) => Write("WARN", message);

    public static void Error(string context, Exception exception) =>
        Write("ERROR", $"{context}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded();
                string normalized = message
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n');
                string line = $"{DateTimeOffset.Now:O} [{level}] {normalized}{Environment.NewLine}";
                File.AppendAllText(LogPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Logging must never crash AURA or mask the original exception.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaximumLogBytes)
            return;

        string previous = Path.Combine(DirectoryPath, "aura.previous.log");
        File.Move(LogPath, previous, overwrite: true);
    }
}
