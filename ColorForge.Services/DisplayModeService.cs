using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace ColorForge.Services;

public static class DisplayModeService
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DevMode
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string dmDeviceName;

		public short dmSpecVersion;

		public short dmDriverVersion;

		public short dmSize;

		public short dmDriverExtra;

		public int dmFields;

		public int dmPositionX;

		public int dmPositionY;

		public int dmDisplayOrientation;

		public int dmDisplayFixedOutput;

		public short dmColor;

		public short dmDuplex;

		public short dmYResolution;

		public short dmTTOption;

		public short dmCollate;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string dmFormName;

		public short dmLogPixels;

		public int dmBitsPerPel;

		public int dmPelsWidth;

		public int dmPelsHeight;

		public int dmDisplayFlags;

		public int dmDisplayFrequency;

		public int dmICMMethod;

		public int dmICMIntent;

		public int dmMediaType;

		public int dmDitherType;

		public int dmReserved1;

		public int dmReserved2;

		public int dmPanningWidth;

		public int dmPanningHeight;
	}

	private const int ENUM_CURRENT_SETTINGS = -1;

	private const int CDS_UPDATEREGISTRY = 1;

	private const int DISP_CHANGE_SUCCESSFUL = 0;

	private const int DM_PELSWIDTH = 524288;

	private const int DM_PELSHEIGHT = 1048576;

	private const int DM_DISPLAYFREQUENCY = 4194304;

	private const int DM_DISPLAYFIXEDOUTPUT = 536870912;

	private const int DMDFO_DEFAULT = 0;

	private const int DMDFO_STRETCH = 1;

	public static List<DisplayMode> GetModes(string? deviceName = null)
	{
		HashSet<DisplayMode> hashSet = new HashSet<DisplayMode>();
		DevMode devMode = CreateDevMode();
		for (int i = 0; EnumDisplaySettings(deviceName, i, ref devMode); i++)
		{
			if (devMode.dmPelsWidth >= 800 && devMode.dmPelsHeight >= 600 && devMode.dmDisplayFrequency >= 50)
			{
				hashSet.Add(new DisplayMode(devMode.dmPelsWidth, devMode.dmPelsHeight, devMode.dmDisplayFrequency));
			}
			devMode = CreateDevMode();
		}
		return (from x in hashSet
			orderby x.Width descending, x.Height descending, x.RefreshRate descending
			select x).ToList();
	}

	public static DisplayMode? GetCurrentMode(string? deviceName)
	{
		DevMode devMode = CreateDevMode();
		if (!EnumDisplaySettings(deviceName, -1, ref devMode))
		{
			return null;
		}
		return new DisplayMode(devMode.dmPelsWidth, devMode.dmPelsHeight, devMode.dmDisplayFrequency);
	}

	public static DisplayMode? FindClosest(string? deviceName, int width, int height, int preferredHz)
	{
		return (from x in GetModes(deviceName)
			where x.Width == width && x.Height == height
			orderby Math.Abs(x.RefreshRate - preferredHz)
			select x).FirstOrDefault();
	}

    public static bool Apply(string? deviceName, DisplayMode selected, bool stretch = false, bool persist = true, int? fixedOutput = null)
	{
		DevMode devMode = CreateDevMode();
        if (!EnumDisplaySettings(deviceName, -1, ref devMode)) return false;
		devMode.dmPelsWidth = selected.Width;
		devMode.dmPelsHeight = selected.Height;
		devMode.dmDisplayFrequency = selected.RefreshRate;
        devMode.dmDisplayFixedOutput = fixedOutput ?? (stretch ? 1 : 0);
		devMode.dmFields = 542638080;
        return ChangeDisplaySettingsEx(deviceName, ref devMode, IntPtr.Zero, persist ? 1 : 0, IntPtr.Zero) == 0
            && GetCurrentMode(deviceName) == selected;
	}

    public static (DisplayMode Mode, int FixedOutput)? Capture(string deviceName)
    {
        DevMode mode = CreateDevMode();
        if (!EnumDisplaySettings(deviceName, -1, ref mode)) return null;
        return (new DisplayMode(mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency), mode.dmDisplayFixedOutput);
    }

	public static bool Apply(DisplayMode selected)
	{
		return Apply(null, selected);
	}

	private static DevMode CreateDevMode()
	{
		return new DevMode
		{
			dmSize = (short)Marshal.SizeOf<DevMode>()
		};
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int ChangeDisplaySettingsEx(string? deviceName, ref DevMode devMode, nint hwnd, int flags, nint lParam);
}
