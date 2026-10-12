namespace ColorForge.Services;

public sealed record DisplayMode(int Width, int Height, int RefreshRate)
{
	public override string ToString()
	{
		return $"{Width} × {Height}  ·  {RefreshRate} Гц";
	}
}
