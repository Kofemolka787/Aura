namespace ColorForge.Services;

public sealed record MonitorTarget(string DeviceName, string Label, int Index, bool IsPrimary, int Width, int Height)
{
	public override string ToString()
	{
		return Label;
	}
}
