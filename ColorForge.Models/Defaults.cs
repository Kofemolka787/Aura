using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ColorForge.Models;

public static class Defaults
{
	public static List<DisplayProfile> CreateProfiles()
	{
		int num = 10;
		List<DisplayProfile> list = new List<DisplayProfile>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<DisplayProfile> span = CollectionsMarshal.AsSpan(list);
		int num2 = 0;
		span[num2] = new DisplayProfile
		{
			Name = "Natural"
		};
		num2++;
		span[num2] = new DisplayProfile
		{
			Name = "AURA · Зима",
			Saturation = 145,
			Vibrance = 70,
			Brightness = -3,
			Contrast = 112,
			Gamma = 100,
			Temperature = 5800,
			Shadows = 12
		};
		num2++;
		span[num2] = new DisplayProfile
		{
			Name = "AURA · Ночь",
			Saturation = 165,
			Vibrance = 82,
			Brightness = 7,
			Contrast = 94,
			Gamma = 130,
			Temperature = 7300,
			Shadows = 46
		};
		num2++;
		span[num2] = new DisplayProfile
		{
			Name = "AURA · Пустыня",
			Saturation = 155,
			Vibrance = 76,
			Contrast = 109,
			Gamma = 103,
			Temperature = 5400,
			Shadows = 16
		};
		num2++;
		span[num2] = new DisplayProfile
		{
			Name = "AURA · Джунгли",
			Saturation = 170,
			Vibrance = 83,
			Contrast = 111,
			Gamma = 104,
			Temperature = 6500,
			Shadows = 20
		};
		num2++;
		span[num2] = new DisplayProfile
		{
			Name = "AURA · Зелёнка",
			Saturation = 152,
			Vibrance = 75,
			Brightness = 1,
			Contrast = 107,
			Gamma = 106,
			Temperature = 6700,
			Shadows = 23
		};
		num2++;
		span[num2] = new DisplayProfile
		{
			Name = "AURA · Метро",
			Saturation = 140,
			Vibrance = 70,
			Brightness = 5,
			Contrast = 104,
			Gamma = 120,
			Temperature = 6400,
			Shadows = 36
		};
		num2++;
		span[num2] = new DisplayProfile
		{
			Name = "AURA · CS2",
			Saturation = 158,
			Vibrance = 79,
			Contrast = 112,
			Gamma = 105,
			Temperature = 6500,
			Shadows = 18
		};
		num2++;
		span[num2] = new DisplayProfile
		{
			Name = "AURA · Dota 2",
			Saturation = 148,
			Vibrance = 74,
			Brightness = 1,
			Contrast = 108,
			Gamma = 106,
			Temperature = 6300,
			Shadows = 22
		};
		num2++;
		span[num2] = new DisplayProfile
		{
			Name = "AURA · Кино",
			Saturation = 116,
			Vibrance = 58,
			Brightness = -2,
			Contrast = 106,
			Gamma = 98,
			Temperature = 5200,
			Shadows = 14
		};
		num2++;
		return list;
	}

	public static List<GameBinding> CreateGames()
	{
		int num = 3;
		List<GameBinding> list = new List<GameBinding>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<GameBinding> span = CollectionsMarshal.AsSpan(list);
		int num2 = 0;
		span[num2] = new GameBinding
		{
			ProcessName = "RustClient",
			ProfileName = "AURA · Зелёнка"
		};
		num2++;
		span[num2] = new GameBinding
		{
			ProcessName = "cs2",
			ProfileName = "AURA · CS2"
		};
		num2++;
		span[num2] = new GameBinding
		{
			ProcessName = "dota2",
			ProfileName = "AURA · Dota 2"
		};
		num2++;
		return list;
	}
}
