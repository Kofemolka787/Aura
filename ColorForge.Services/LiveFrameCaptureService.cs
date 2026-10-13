using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ColorForge.Services;

public sealed class LiveFrameCaptureService
{
    private readonly IExternalToolRunner _runner;
    private readonly Func<string> _tool;
    private readonly string _directory;
    public LiveFrameCaptureService(IExternalToolRunner? runner = null, Func<string>? tool = null, string? directory = null)
    {
        _runner = runner ?? new ExternalToolRunner();
        _tool = tool ?? new BenchmarkService().GetToolPath;
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AURA", "LiveFrames");
    }
    public async Task RunAsync(string process, IProgress<LiveFrameSnapshot> progress, CancellationToken token)
    {
        string tool = _tool(); Directory.CreateDirectory(_directory);
        while (!token.IsCancellationRequested)
        {
            string session = "AURA-Live-" + Guid.NewGuid().ToString("N");
            string path = Path.Combine(_directory, session + ".csv");
            Task<ToolResult>? capture = null;
            LiveCsvReader? input = null;
            using CancellationTokenSource owned = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                capture = _runner.RunAsync(tool, new[] { "--process_name", process, "--output_file", path,
                    "--timed", "180", "--terminate_after_timed", "--no_console_stats", "--v2_metrics", "--session_name", session },
                    TimeSpan.FromSeconds(210), owned.Token, Path.GetDirectoryName(tool));
                input = new(path, process);
                while (!capture.IsCompleted)
                {
                    token.ThrowIfCancellationRequested();
                    progress.Report(input.Read(DateTime.UtcNow));
                    await Task.WhenAny(capture, Task.Delay(250, token)).ConfigureAwait(false);
                }
                ToolResult result = await capture.ConfigureAwait(false);
                if (result.ExitCode != 0) throw new InvalidOperationException("Живые FPS недоступны: PresentMon завершился с кодом " + result.ExitCode);
            }
            finally
            {
                input?.Dispose(); owned.Cancel();
                if (capture != null)
                {
                    try { await capture.ConfigureAwait(false); }
                    catch (Exception e) { AppLogService.Error("Live PresentMon stopped.", e); }
                    try { await _runner.RunAsync(tool, new[] { "--session_name", session, "--terminate_existing_session" }, TimeSpan.FromSeconds(10), CancellationToken.None, Path.GetDirectoryName(tool)).ConfigureAwait(false); }
                    catch (Exception e) { AppLogService.Error("Owned live session cleanup failed.", e); }
                }
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception e) { AppLogService.Error("Live CSV cleanup failed.", e); }
            }
        }
        token.ThrowIfCancellationRequested();
    }
}
