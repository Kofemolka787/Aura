using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using ColorForge.Models;

namespace ColorForge.Windows;

public sealed class CrosshairWindow : Window
{
	private CrosshairSettings _settings = new CrosshairSettings();

	private string? _deviceName;

	private const int GWL_EXSTYLE = -20;

	private const long WS_EX_TRANSPARENT = 32L;

	private const long WS_EX_TOOLWINDOW = 128L;

	private const long WS_EX_NOACTIVATE = 134217728L;

	public CrosshairWindow()
	{
		base.WindowStyle = WindowStyle.None;
		base.AllowsTransparency = true;
		base.Background = System.Windows.Media.Brushes.Transparent;
		base.Topmost = true;
		base.ShowInTaskbar = false;
		base.ShowActivated = false;
		base.ResizeMode = ResizeMode.NoResize;
		base.IsHitTestVisible = false;
		base.SourceInitialized += delegate
		{
			MakeClickThrough();
		};
		PositionOverVirtualDesktop();
	}

	public void UpdateSettings(CrosshairSettings settings)
	{
		_settings = settings;
		PositionOverVirtualDesktop();
		InvalidateVisual();
		if (settings.Enabled && !base.IsVisible)
		{
			Show();
		}
		if (!settings.Enabled && base.IsVisible)
		{
			Hide();
		}
	}

	public void SetMonitor(string? deviceName)
	{
		_deviceName = deviceName;
		InvalidateVisual();
	}

	private void PositionOverVirtualDesktop()
	{
		base.Left = SystemParameters.VirtualScreenLeft;
		base.Top = SystemParameters.VirtualScreenTop;
		base.Width = SystemParameters.VirtualScreenWidth;
		base.Height = SystemParameters.VirtualScreenHeight;
	}

	protected override void OnRender(DrawingContext dc)
	{
		base.OnRender(dc);
		if (_settings.Enabled)
		{
			Rectangle rectangle = (Screen.AllScreens.FirstOrDefault((Screen screen) => screen.DeviceName.Equals(_deviceName, StringComparison.OrdinalIgnoreCase)) ?? Screen.PrimaryScreen)?.Bounds ?? new Rectangle(0, 0, (int)base.Width, (int)base.Height);
			double x = (double)rectangle.Left - SystemParameters.VirtualScreenLeft + (double)rectangle.Width / 2.0;
			double y = (double)rectangle.Top - SystemParameters.VirtualScreenTop + (double)rectangle.Height / 2.0;
			System.Windows.Point c = new System.Windows.Point(x, y);
			System.Drawing.Color color = System.Drawing.Color.FromArgb(_settings.Argb);
			System.Windows.Media.Color color2 = System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B);
			DrawShape(dc, c, Colors.Black, _settings.Thickness + _settings.Outline * 2, _settings.Size + _settings.Outline, _settings.Gap);
			DrawShape(dc, c, color2, _settings.Thickness, _settings.Size, _settings.Gap);
		}
	}

	private void DrawShape(DrawingContext dc, System.Windows.Point c, System.Windows.Media.Color color, int thickness, int size, int gap)
	{
		System.Windows.Media.Pen pen = new System.Windows.Media.Pen(new SolidColorBrush(color), Math.Max(1, thickness));
		pen.Freeze();
		switch (_settings.Shape)
		{
		case CrosshairShape.Dot:
			dc.DrawEllipse(new SolidColorBrush(color), null, c, (double)size / 2.0, (double)size / 2.0);
			break;
		case CrosshairShape.Circle:
			dc.DrawEllipse(null, pen, c, size, size);
			break;
		case CrosshairShape.TShape:
			dc.DrawLine(pen, new System.Windows.Point(c.X - (double)gap - (double)size, c.Y), new System.Windows.Point(c.X - (double)gap, c.Y));
			dc.DrawLine(pen, new System.Windows.Point(c.X + (double)gap, c.Y), new System.Windows.Point(c.X + (double)gap + (double)size, c.Y));
			dc.DrawLine(pen, new System.Windows.Point(c.X, c.Y + (double)gap), new System.Windows.Point(c.X, c.Y + (double)gap + (double)size));
			break;
		default:
			dc.DrawLine(pen, new System.Windows.Point(c.X - (double)gap - (double)size, c.Y), new System.Windows.Point(c.X - (double)gap, c.Y));
			dc.DrawLine(pen, new System.Windows.Point(c.X + (double)gap, c.Y), new System.Windows.Point(c.X + (double)gap + (double)size, c.Y));
			dc.DrawLine(pen, new System.Windows.Point(c.X, c.Y - (double)gap - (double)size), new System.Windows.Point(c.X, c.Y - (double)gap));
			dc.DrawLine(pen, new System.Windows.Point(c.X, c.Y + (double)gap), new System.Windows.Point(c.X, c.Y + (double)gap + (double)size));
			break;
		}
	}

	private void MakeClickThrough()
	{
		nint handle = new WindowInteropHelper(this).Handle;
		long num = ((IntPtr)GetWindowLongPtr(handle, -20)).ToInt64();
		SetWindowLongPtr(handle, -20, new IntPtr(num | 0x20 | 0x80 | 0x8000000));
	}

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
	private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
