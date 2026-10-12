using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace ColorForge.Services;

public static class MonitorService
{
	public static List<MonitorTarget> GetMonitors()
	{
		return (from x in Screen.AllScreens
			orderby x.Primary descending, x.Bounds.X, x.Bounds.Y
			select x).ToList().Select((Screen screen, int index) => new MonitorTarget(screen.DeviceName, $"Монитор {index + 1} · {screen.Bounds.Width}×{screen.Bounds.Height}{(screen.Primary ? " · основной" : string.Empty)}", index, screen.Primary, screen.Bounds.Width, screen.Bounds.Height)).ToList();
	}
}
