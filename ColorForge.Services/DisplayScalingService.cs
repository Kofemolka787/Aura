using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ColorForge.Services;

public static class DisplayScalingService
{
	private struct Luid
	{
		public uint LowPart;

		public int HighPart;
	}

	private struct DisplayConfigPathSourceInfo
	{
		public Luid AdapterId;

		public uint Id;

		public uint ModeInfoIdx;

		public uint StatusFlags;
	}

	private struct DisplayConfigRational
	{
		public uint Numerator;

		public uint Denominator;
	}

	private struct DisplayConfigPathTargetInfo
	{
		public Luid AdapterId;

		public uint Id;

		public uint ModeInfoIdx;

		public uint OutputTechnology;

		public uint Rotation;

		public uint Scaling;

		public DisplayConfigRational RefreshRate;

		public uint ScanLineOrdering;

		[MarshalAs(UnmanagedType.Bool)]
		public bool TargetAvailable;

		public uint StatusFlags;
	}

	private struct DisplayConfigPathInfo
	{
		public DisplayConfigPathSourceInfo SourceInfo;

		public DisplayConfigPathTargetInfo TargetInfo;

		public uint Flags;
	}

	private struct DisplayConfig2DRegion
	{
		public uint Width;

		public uint Height;
	}

	private struct DisplayConfigVideoSignalInfo
	{
		public ulong PixelRate;

		public DisplayConfigRational HSyncFreq;

		public DisplayConfigRational VSyncFreq;

		public DisplayConfig2DRegion ActiveSize;

		public DisplayConfig2DRegion TotalSize;

		public uint VideoStandard;

		public uint ScanLineOrdering;
	}

	private struct DisplayConfigTargetMode
	{
		public DisplayConfigVideoSignalInfo TargetVideoSignalInfo;
	}

	private struct PointL
	{
		public int X;

		public int Y;
	}

	private struct RectL
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct DisplayConfigSourceMode
	{
		public uint Width;

		public uint Height;

		public uint PixelFormat;

		public PointL Position;
	}

	private struct DisplayConfigDesktopImageInfo
	{
		public PointL PathSourceSize;

		public RectL DesktopImageRegion;

		public RectL DesktopImageClip;
	}

	[StructLayout(LayoutKind.Explicit, Size = 48)]
	private struct DisplayConfigModeInfoUnion
	{
		[FieldOffset(0)]
		public DisplayConfigTargetMode TargetMode;

		[FieldOffset(0)]
		public DisplayConfigSourceMode SourceMode;

		[FieldOffset(0)]
		public DisplayConfigDesktopImageInfo DesktopImageInfo;
	}

	private struct DisplayConfigModeInfo
	{
		public uint InfoType;

		public uint Id;

		public Luid AdapterId;

		public DisplayConfigModeInfoUnion ModeInfo;
	}

	private struct DisplayConfigDeviceInfoHeader
	{
		public uint Type;

		public uint Size;

		public Luid AdapterId;

		public uint Id;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DisplayConfigSourceDeviceName
	{
		public DisplayConfigDeviceInfoHeader Header;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string ViewGdiDeviceName;
	}

	private const int ERROR_SUCCESS = 0;

	private const int ERROR_INSUFFICIENT_BUFFER = 122;

	private const uint QDC_ONLY_ACTIVE_PATHS = 2u;

	private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 32u;

	private const uint SDC_APPLY = 128u;

	private const uint SDC_SAVE_TO_DATABASE = 512u;

	private const uint SDC_ALLOW_CHANGES = 1024u;

	private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1u;

	private const uint DISPLAYCONFIG_SCALING_STRETCHED = 3u;

	private const uint DISPLAYCONFIG_SCALING_ASPECTRATIOCENTEREDMAX = 4u;

    public static bool TryApply(string deviceName, bool stretch, out string error, bool persist = true)
        => TryApplyScaling(deviceName, stretch ? 3u : 4u, out error, persist);

    public static bool TryApplyScaling(string deviceName, uint scaling, out string error, bool persist = true)
	{
		error = string.Empty;
		uint numPathArrayElements = 0u;
		uint numModeInfoArrayElements = 0u;
		int displayConfigBufferSizes = GetDisplayConfigBufferSizes(2u, out numPathArrayElements, out numModeInfoArrayElements);
		if (displayConfigBufferSizes != 0)
		{
			error = Describe(displayConfigBufferSizes);
			return false;
		}
		int num = 0;
		while (num < 3)
		{
			DisplayConfigPathInfo[] array = new DisplayConfigPathInfo[numPathArrayElements];
			DisplayConfigModeInfo[] modeInfoArray = new DisplayConfigModeInfo[numModeInfoArrayElements];
			uint numPathArrayElements2 = numPathArrayElements;
			uint modeInfoArrayElements = numModeInfoArrayElements;
			displayConfigBufferSizes = QueryDisplayConfig(2u, ref numPathArrayElements2, array, ref modeInfoArrayElements, modeInfoArray, IntPtr.Zero);
			if (displayConfigBufferSizes == 122)
			{
				displayConfigBufferSizes = GetDisplayConfigBufferSizes(2u, out numPathArrayElements, out numModeInfoArrayElements);
				if (displayConfigBufferSizes == 0)
				{
					num++;
					continue;
				}
			}
			if (displayConfigBufferSizes != 0)
			{
				error = Describe(displayConfigBufferSizes);
				return false;
			}
			bool flag = false;
			for (int i = 0; i < numPathArrayElements2; i++)
			{
				DisplayConfigSourceDeviceName requestPacket = new DisplayConfigSourceDeviceName
				{
					Header = new DisplayConfigDeviceInfoHeader
					{
						Type = 1u,
						Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
						AdapterId = array[i].SourceInfo.AdapterId,
						Id = array[i].SourceInfo.Id
					},
					ViewGdiDeviceName = string.Empty
				};
				if (DisplayConfigGetDeviceInfo(ref requestPacket) == 0 && requestPacket.ViewGdiDeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
				{
					DisplayConfigPathInfo displayConfigPathInfo = array[i];
                    displayConfigPathInfo.TargetInfo.Scaling = scaling;
					array[i] = displayConfigPathInfo;
					flag = true;
				}
			}
			if (!flag)
			{
				error = "Windows не нашла выбранный дисплей в активной конфигурации.";
				return false;
			}
        displayConfigBufferSizes = SetDisplayConfig(numPathArrayElements2, array, modeInfoArrayElements, modeInfoArray, persist ? 1696u : 1184u);
			if (displayConfigBufferSizes == 0)
			{
                if (TryReadScaling(deviceName, out uint actual) && actual == scaling) return true;
                error = "Драйвер не подтвердил запрошенное масштабирование.";
                return false;
			}
			error = Describe(displayConfigBufferSizes);
			return false;
		}
		error = "Конфигурация дисплеев изменилась во время применения.";
		return false;
	}

	private static string Describe(int code)
	{
		return "Windows scaling · " + new Win32Exception(code).Message;
	}

    public static bool TryReadScaling(string deviceName, out uint scaling)
    {
        scaling = 0;
        if (GetDisplayConfigBufferSizes(2, out uint pathCount, out uint modeCount) != 0) return false;
        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];
        if (QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return false;
        for (int index = 0; index < pathCount; index++)
        {
            DisplayConfigSourceDeviceName source = new()
            {
                Header = new() { Type = 1, Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(), AdapterId = paths[index].SourceInfo.AdapterId, Id = paths[index].SourceInfo.Id },
                ViewGdiDeviceName = string.Empty
            };
            if (DisplayConfigGetDeviceInfo(ref source) == 0 && source.ViewGdiDeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
            { scaling = paths[index].TargetInfo.Scaling; return true; }
        }
        return false;
    }

	[DllImport("user32.dll")]
	private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

	[DllImport("user32.dll")]
	private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DisplayConfigPathInfo[] pathInfoArray, ref uint modeInfoArrayElements, [Out] DisplayConfigModeInfo[] modeInfoArray, nint currentTopologyId);

	[DllImport("user32.dll")]
	private static extern int SetDisplayConfig(uint numPathArrayElements, [In] DisplayConfigPathInfo[] pathArray, uint numModeInfoArrayElements, [In] DisplayConfigModeInfo[] modeInfoArray, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);
}
