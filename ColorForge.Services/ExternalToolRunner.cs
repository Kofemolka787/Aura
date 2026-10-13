using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ColorForge.Services;

public sealed record ToolResult(int ExitCode, string Output, string Error);
public interface IExternalToolRunner
{
    Task<ToolResult> RunAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken token, string? workingDirectory = null);
}
public sealed class ExternalToolRunner : IExternalToolRunner
{
    private const int MaximumCharacters = 16 * 1024 * 1024;
    public async Task<ToolResult> RunAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken token, string? workingDirectory = null)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(timeout);
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (workingDirectory != null) start.WorkingDirectory = workingDirectory;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить " + Path.GetFileName(executable));
        Task<string> output = ReadAsync(process.StandardOutput, stop.Token);
        Task<string> error = ReadAsync(process.StandardError, stop.Token);
        using CancellationTokenRegistration termination = stop.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception exception) { AppLogService.Error("Tool termination failed.", exception); }
        });
        _ = output.ContinueWith(_ => stop.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = error.ContinueWith(_ => stop.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(stop.Token)).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // Observe both readers even if cancellation or a bounded-reader failure won the race.
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { }
            if (exception is OperationCanceledException && !token.IsCancellationRequested)
                throw new TimeoutException("Превышено время ожидания " + Path.GetFileName(executable), exception);
            throw;
        }
    }
    private static async Task<string> ReadAsync(StreamReader reader, CancellationToken token)
    {
        StringBuilder text = new();
        char[] buffer = new char[4096];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return text.ToString();
            if (text.Length + count > MaximumCharacters) throw new InvalidDataException("Слишком большой вывод внешней утилиты.");
            text.Append(buffer, 0, count);
        }
    }
}
