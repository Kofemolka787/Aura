namespace ColorForge.Models;

public sealed class CrosshairSettings
{
	public bool Enabled { get; set; }

	public CrosshairShape Shape { get; set; }

	public int Size { get; set; } = 8;

	public int Gap { get; set; } = 3;

	public int Thickness { get; set; } = 2;

	public int Outline { get; set; } = 1;

	public int Argb { get; set; } = -16711936;
}
