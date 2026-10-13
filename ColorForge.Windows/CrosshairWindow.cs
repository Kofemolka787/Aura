using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using ColorForge.Models;

namespace ColorForge.Windows;

public sealed class CrosshairWindow : Window
{
    private CrosshairSettings _settings = new();
    private string? _deviceName;
    public CrosshairWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize; IsHitTestVisible = false;
        Width = Height = 1;
        SourceInitialized += (_, _) => { MakeClickThrough(); PositionOnSelectedMonitor(); };
        SizeChanged += (_, _) => InvalidateVisual();
    }
    public void UpdateSettings(CrosshairSettings settings)
    {
        _settings = settings;
        if (settings.Enabled && !IsVisible) Show();
        if (!settings.Enabled && IsVisible) Hide();
        PositionOnSelectedMonitor();
        InvalidateVisual();
    }
    public void SetMonitor(string? deviceName) { _deviceName = deviceName; PositionOnSelectedMonitor(); InvalidateVisual(); }
    private void PositionOnSelectedMonitor()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        Screen? screen = Screen.AllScreens.FirstOrDefault(x => x.DeviceName.Equals(_deviceName, StringComparison.OrdinalIgnoreCase)) ?? Screen.PrimaryScreen;
        if (screen == null) return;
        var bounds = screen.Bounds;
        // HWND bounds are physical pixels; drawing uses this window's local WPF coordinates.
        if (!SetWindowPos(handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010 | 0x0200))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    protected override void OnRender(System.Windows.Media.DrawingContext context)
    {
        base.OnRender(context);
        if (_settings.Enabled) CrosshairRenderer.Draw(context, new System.Windows.Point(ActualWidth / 2, ActualHeight / 2), _settings);
    }
    private void MakeClickThrough()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        long style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr(style | 0x20 | 0x80 | 0x8000000));
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
