namespace ColorForge.Models;

public sealed class DisplayProfile
{
	public string Name { get; set; } = "Natural";

	public int Saturation { get; set; } = 100;

	public int Vibrance { get; set; } = 50;

	public int Brightness { get; set; }

	public int Contrast { get; set; } = 100;

	public int Gamma { get; set; } = 100;

	public int Temperature { get; set; } = 6500;


	public int Shadows { get; set; }


	public DisplayProfile Clone(string? name = null)
	{
		return new DisplayProfile
		{
			Name = (name ?? Name),
			Saturation = Saturation,
			Vibrance = Vibrance,
			Brightness = Brightness,
			Contrast = Contrast,
			Gamma = Gamma,
			Temperature = Temperature,
			Shadows = Shadows,
		};
	}
}
