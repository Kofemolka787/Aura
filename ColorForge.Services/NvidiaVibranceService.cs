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
		if (!IsAvailable)
		{
			return false;
		}
		double normalizedLevel = Math.Clamp(((double)percent - 50.0) / 50.0, -1.0, 1.0);
		try
		{
			Display val = ((IEnumerable<Display>)Display.GetDisplays()).FirstOrDefault((Func<Display, bool>)((Display x) => x.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)));
			if (val == null)
			{
				return false;
			}
			val.DigitalVibranceControl.NormalizedLevel = normalizedLevel;
			return true;
		}
		catch
		{
			return false;
		}
	}
}
