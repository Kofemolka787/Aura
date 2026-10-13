using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ColorForge.Models;
using ColorForge.Services;

namespace ColorForge.Windows;

public sealed class PerformanceOverlayWindow : Window
{
    private readonly StackPanel _content = new();
    private string? _device;
    private OverlaySettings _settings = new();
    public PerformanceOverlayWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize; IsHitTestVisible = false;
        Width = 330; Height = 270;
        Content = new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(235, 12, 10, 18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(95, 64, 132)), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = _content };
        SourceInitialized += (_, _) => { OverlayWindowStyle.MakeClickThrough(new WindowInteropHelper(this).Handle); Position(); };
    }
    public void Update(OverlaySettings settings, string? device, string profile, HardwareSnapshot? hardware, LiveFrameSnapshot frames)
    {
        _settings = settings; _device = device; Opacity = settings.Opacity;
        if (!settings.Enabled) { Hide(); return; }
        _content.Children.Clear();
        void Line(string text, Color? color = null) => _content.Children.Add(new TextBlock { Text = text,
            Foreground = new SolidColorBrush(color ?? Colors.White), FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Margin = new Thickness(0, 0, 0, 5), TextTrimming = TextTrimming.CharacterEllipsis });
        static string Value(double? n, string suffix) => n.HasValue && double.IsFinite(n.Value) ? n.Value.ToString("0") + suffix : "—" + suffix;
        TelemetrySample? sensors = hardware == null ? null : TelemetrySample.FromSnapshot(hardware);
        if (settings.ShowProfile) Line("AURA · " + profile, Color.FromRgb(200, 156, 247));
        if (settings.ShowFps)
        {
            Line("FPS " + Value(frames.Fps, "") + "   1% low " + Value(frames.LowFps, ""), Color.FromRgb(101, 226, 160));
            Line(frames.Status, Color.FromRgb(160, 150, 175));
        }
        if (settings.ShowCpu) Line("CPU " + Value(sensors?.CpuTemperatureC, "°C") + "  " + Value(sensors?.CpuLoadPercent, "%"));
        if (settings.ShowGpu)
        {
            Line(hardware?.GpuName ?? "GPU · ожидание датчиков", Color.FromRgb(160, 150, 175));
            Line("GPU " + Value(sensors?.GpuTemperatureC, "°C") + "  " + Value(sensors?.GpuLoadPercent, "%") + "  " + Value(sensors?.GpuCoreClockMhz, " MHz"));
        }
        if (settings.ShowVram) Line("VRAM " + Value(sensors?.GpuMemoryUsedMb, " MB") + " / " + Value(hardware?.GpuMemoryTotalMb, " MB"));
        if ((hardware?.TelemetryValid != true || DateTime.UtcNow - hardware.TimestampUtc > TimeSpan.FromSeconds(3))
            && (settings.ShowCpu || settings.ShowGpu || settings.ShowVram)) Line("Датчики: нет свежих достоверных данных", Colors.Salmon);
        if (settings.ShowFrameGraph)
        {
            Canvas graph = new() { Width = 302, Height = 45, ClipToBounds = true };
            double[] values = frames.FrameTimesMs;
            double maximum = Math.Max(1, values.Length == 0 ? 1 : values.Max());
            PointCollection points = new();
            int stride = Math.Max(1, (int)Math.Ceiling(values.Length / 150d));
            for (int i = 0; i < values.Length; i += stride)
                points.Add(new System.Windows.Point(i / (double)Math.Max(1, values.Length - 1) * 302, 45 * (1 - values.Skip(i).Take(stride).Max() / maximum)));
            graph.Children.Add(new Polyline { Points = points, Stroke = Brushes.MediumSeaGreen, StrokeThickness = 1 });
            _content.Children.Add(graph);
            if (values.Length > 0) Line($"Пик {maximum:0.0} ms", Color.FromRgb(160, 150, 175));
        }
        _content.LayoutTransform = Transform.Identity;
        _content.Measure(new System.Windows.Size(302, double.PositiveInfinity));
        Height = (_content.DesiredSize.Height + 26) * settings.Scale; Width = 330 * settings.Scale;
        _content.LayoutTransform = new ScaleTransform(settings.Scale, settings.Scale);
        if (!IsVisible) Show(); Position();
    }
    private void Position()
    {
        nint window = new WindowInteropHelper(this).Handle;
        if (window == 0) return;
        Screen? screen = Screen.AllScreens.FirstOrDefault(x => x.DeviceName.Equals(_device, StringComparison.OrdinalIgnoreCase));
        if (screen == null) { Hide(); return; }
        double dpi = GetDpiForWindow(window) / 96d;
        int width = (int)Math.Ceiling(Width * dpi), height = (int)Math.Ceiling(Height * dpi);
        var bounds = screen.Bounds;
        bool right = _settings.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
        bool bottom = _settings.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;
        int x = right ? bounds.Right - width - 16 : bounds.Left + 16;
        int y = bottom ? bounds.Bottom - height - 16 : bounds.Top + 16;
        SetWindowPos(window, new IntPtr(-1), x, y, width, height, 0x0010 | 0x0200);
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

internal static class OverlayWindowStyle
{
    internal static void MakeClickThrough(nint window)
    {
        long style = GetWindowLongPtr(window, -20).ToInt64();
        SetWindowLongPtr(window, -20, new IntPtr(style | 0x20 | 0x80 | 0x8000000));
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
