using System;

namespace ColorForge.Services;

public static class OverlayFreshness
{
    public static LiveFrameSnapshot Frames(LiveFrameSnapshot value, DateTime now)
    {
        if (!value.Fps.HasValue && value.FrameTimesMs.Length == 0) return value;
        return now - value.SampledAtUtc <= TimeSpan.FromSeconds(2) && value.SampledAtUtc <= now.AddSeconds(1)
            ? value : new(null, null, Array.Empty<double>(), "FPS — · данные устарели");
    }
}
