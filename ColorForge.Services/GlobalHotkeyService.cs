using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyAction> _registered = new();
    public event Action<HotkeyAction>? Invoked;
    public GlobalHotkeyService(Window window)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            ?? throw new InvalidOperationException("Окно горячих клавиш ещё не создано.");
        _source.AddHook(Hook);
    }
    public IReadOnlyList<string> Configure(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterAll();
        List<string> errors = new();
        foreach (HotkeyBinding binding in bindings)
        {
            if (!binding.Enabled) continue;
            int id = 0xA100 + (int)binding.Action;
            uint key = HotkeyBinding.VirtualKey(binding.Key);
            if (key == 0 || binding.Modifiers == 0 || _registered.ContainsKey(id))
            { errors.Add(binding.Title + ": некорректное сочетание"); continue; }
            if (RegisterHotKey(_source.Handle, id, binding.Modifiers | 0x4000, key)) _registered.Add(id, binding.Action);
            else errors.Add(binding.Gesture + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        return errors;
    }
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && _registered.TryGetValue(wParam.ToInt32(), out HotkeyAction action))
        { handled = true; Invoked?.Invoke(action); }
        return IntPtr.Zero;
    }
    private void UnregisterAll()
    {
        foreach (int id in _registered.Keys) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }
    public void Dispose() { UnregisterAll(); _source.RemoveHook(Hook); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint window, int id);
}
