using System;
using System.Collections.Generic;
using System.Linq;
using NvAPIWrapper.Display;

namespace ColorForge.Services;

internal sealed class NvidiaVibranceService
{
	public bool IsAvailable { get; }

	public NvidiaVibranceService()
	{
		try
		{
			IsAvailable = Display.GetDisplays().Length != 0;
		}
		catch
		{
			IsAvailable = false;
		}
	}

	public bool SetPercent(int percent, string deviceName)
	{
        return SetLevel(Math.Clamp(((double)percent - 50) / 50, -1, 1), deviceName);
    }

    public double? GetLevel(string deviceName)
    {
        try
        {
            var display = Display.GetDisplays().FirstOrDefault(x => x.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
            return display?.DigitalVibranceControl.NormalizedLevel;
        }
        catch (Exception exception) { AppLogService.Error("Could not capture NVIDIA color baseline.", exception); return null; }
    }

    public bool SetLevel(double normalizedLevel, string deviceName)
    {
		if (!IsAvailable)
		{
			return false;
		}
		try
		{
			Display val = ((IEnumerable<Display>)Display.GetDisplays()).FirstOrDefault((Func<Display, bool>)((Display x) => x.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)));
			if (val == null)
			{
				return false;
			}
			val.DigitalVibranceControl.NormalizedLevel = normalizedLevel;
            return Math.Abs(val.DigitalVibranceControl.NormalizedLevel - normalizedLevel) < 0.025;
		}
		catch
		{
			return false;
		}
	}
}
