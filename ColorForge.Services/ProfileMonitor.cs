using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed class ProfileMonitor : IDisposable
{
    private readonly Timer _timer;
    private readonly object _gate = new();
    private GameBinding[] _bindings = Array.Empty<GameBinding>();
    private string? _activeKey;
    private bool _running;
    private int _checking;
    private bool _disposed;
    public event Action<GameBinding?>? ActiveGameChanged;
    public ProfileMonitor() => _timer = new(Check, null, Timeout.Infinite, Timeout.Infinite);
    public void UpdateBindings(IEnumerable<GameBinding> bindings)
    {
        lock (_gate) _bindings = bindings.Select(x => new GameBinding { ProcessName = x.ProcessName, ProfileName = x.ProfileName, Enabled = x.Enabled }).ToArray();
    }
    public void Start() { lock (_gate) { _running = true; _timer.Change(0, 1000); } }
    public void Stop()
    {
        bool changed;
        lock (_gate) { _running = false; _timer.Change(Timeout.Infinite, Timeout.Infinite); changed = _activeKey != null; _activeKey = null; }
        if (changed) ActiveGameChanged?.Invoke(null);
    }
    private void Check(object? state)
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            GameBinding[] bindings;
            lock (_gate) { if (!_running || _disposed) return; bindings = _bindings; }
            nint window = GetForegroundWindow();
            GetWindowThreadProcessId(window, out uint pid);
            string foreground = string.Empty;
            if (pid != 0)
            {
                try { using Process process = Process.GetProcessById((int)pid); foreground = process.ProcessName; }
                catch (ArgumentException) { }
            }
            GameBinding? active = bindings.FirstOrDefault(x => x.Enabled && x.ProcessName.Equals(foreground, StringComparison.OrdinalIgnoreCase));
            string? key = active == null ? null : active.ProcessName + "|" + active.ProfileName;
            lock (_gate)
            {
                if (!_running || _disposed || string.Equals(key, _activeKey, StringComparison.OrdinalIgnoreCase)) return;
                _activeKey = key;
            }
            ActiveGameChanged?.Invoke(active);
        }
        catch (Exception exception) { AppLogService.Error("Game profile monitoring failed.", exception); }
        finally { Volatile.Write(ref _checking, 0); }
    }
    public void Dispose() { lock (_gate) { _disposed = true; _running = false; _timer.Dispose(); } }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
