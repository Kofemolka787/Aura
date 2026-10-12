using System;
using System.Runtime.InteropServices;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed class ColorEngine : IDisposable
{
	private readonly NvidiaVibranceService _nvidia = new NvidiaVibranceService();

	private MonitorTarget? _activeTarget;

	public bool NvidiaAvailable => _nvidia.IsAvailable;

	public string LastWarning { get; private set; } = string.Empty;

	public void Apply(DisplayProfile profile, MonitorTarget target)
	{
		LastWarning = string.Empty;
		if ((object)_activeTarget != null && !_activeTarget.DeviceName.Equals(target.DeviceName, StringComparison.OrdinalIgnoreCase))
		{
			ResetTarget(_activeTarget);
		}
		bool flag = ApplyGamma(profile, target.DeviceName);
		int percent = Math.Clamp((int)Math.Round(50.0 + (double)(profile.Saturation - 100) * 0.45 + (double)(profile.Vibrance - 50) * 0.5), 0, 100);
		bool flag2 = _nvidia.SetPercent(percent, target.DeviceName);
		if (!flag && !flag2)
		{
			LastWarning = "Драйвер отклонил настройки выбранного монитора.";
		}
		else if (!flag)
		{
			LastWarning = "Насыщенность применена, но Windows отклонил гамма-кривую.";
		}
		else if (!flag2 && profile.Saturation != 100)
		{
			LastWarning = "Цвет и гамма применены; насыщенность требует NVIDIA Digital Vibrance.";
		}
		_activeTarget = target;
	}

	public void Reset()
	{
		if ((object)_activeTarget != null)
		{
			ResetTarget(_activeTarget);
			_activeTarget = null;
			LastWarning = string.Empty;
		}
	}

	public void Reset(MonitorTarget target)
	{
		ResetTarget(target);
		MonitorTarget? activeTarget = _activeTarget;
		if ((object)activeTarget != null && activeTarget.DeviceName.Equals(target.DeviceName, StringComparison.OrdinalIgnoreCase))
		{
			_activeTarget = null;
		}
		LastWarning = string.Empty;
	}

	private void ResetTarget(MonitorTarget target)
	{
		ApplyIdentityGamma(target.DeviceName);
		_nvidia.SetPercent(50, target.DeviceName);
	}

	private static bool ApplyGamma(DisplayProfile profile, string deviceName)
	{
		double num = Math.Clamp((double)profile.Gamma / 100.0, 0.5, 2.0);
		double num2 = Math.Clamp((double)profile.Contrast / 100.0, 0.5, 1.5);
		double num3 = Math.Clamp((double)profile.Brightness / 100.0, -0.5, 0.5);
		double num4 = Math.Clamp((double)profile.Shadows / 100.0, 0.0, 1.0) * 0.22;
		(double Red, double Green, double Blue) tuple = TemperatureScale(profile.Temperature);
		double item = tuple.Red;
		double item2 = tuple.Green;
		double item3 = tuple.Blue;
		ushort[] array = new ushort[768];
		for (int i = 0; i < 256; i++)
		{
			double num5 = (double)i / 255.0;
			double num6 = (Math.Pow(Math.Clamp(num5 + num4 * (1.0 - num5) * (1.0 - num5), 0.0, 1.0), 1.0 / num) - 0.5) * num2 + 0.5 + num3;
			array[i] = ToRamp(num6 * item);
			array[256 + i] = ToRamp(num6 * item2);
			array[512 + i] = ToRamp(num6 * item3);
		}
		return SetRamp(deviceName, array);
	}

	private static bool ApplyIdentityGamma(string deviceName)
	{
		ushort[] array = new ushort[768];
		for (int i = 0; i < 256; i++)
		{
			ushort num = (ushort)(i * 257);
			array[i] = (array[256 + i] = (array[512 + i] = num));
		}
		return SetRamp(deviceName, array);
	}

	private static bool SetRamp(string deviceName, ushort[] ramp)
	{
		nint num = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
		if (num == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			return SetDeviceGammaRamp(num, ramp);
		}
		finally
		{
			DeleteDC(num);
		}
	}

	private static ushort ToRamp(double value)
	{
		return (ushort)Math.Clamp((int)Math.Round(Math.Clamp(value, 0.0, 1.0) * 65535.0), 0, 65535);
	}

	private static (double Red, double Green, double Blue) TemperatureScale(int kelvin)
	{
		(double, double, double) tuple = KelvinToRgb(Math.Clamp(kelvin, 3000, 10000));
		(double, double, double) tuple2 = KelvinToRgb(6500);
		return (Red: Math.Clamp(tuple.Item1 / tuple2.Item1, 0.72, 1.18), Green: Math.Clamp(tuple.Item2 / tuple2.Item2, 0.72, 1.18), Blue: Math.Clamp(tuple.Item3 / tuple2.Item3, 0.72, 1.18));
	}

	private static (double Red, double Green, double Blue) KelvinToRgb(int kelvin)
	{
		double num = (double)kelvin / 100.0;
		double value = ((num <= 66.0) ? 255.0 : (329.698727446 * Math.Pow(num - 60.0, -0.1332047592)));
		double value2 = ((num <= 66.0) ? (99.4708025861 * Math.Log(num) - 161.1195681661) : (288.1221695283 * Math.Pow(num - 60.0, -0.0755148492)));
		return new ValueTuple<double, double, double>(item3: Math.Clamp((num >= 66.0) ? 255.0 : ((num <= 19.0) ? 0.0 : (138.5177312231 * Math.Log(num - 10.0) - 305.0447927307)), 0.0, 255.0), item1: Math.Clamp(value, 0.0, 255.0), item2: Math.Clamp(value2, 0.0, 255.0));
	}

	public void Dispose()
	{
		Reset();
	}

	[DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
	private static extern nint CreateDC(string driver, string? device, string? output, nint initData);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteDC(nint dc);

	[DllImport("gdi32.dll")]
	private static extern bool SetDeviceGammaRamp(nint dc, [In] ushort[] ramp);
}
