using System;

namespace ColorForge.Models;

public sealed record NvidiaColorState(int Current, int Minimum, int Maximum, int Default, bool Legacy)
{
    public bool IsValid => Maximum > Minimum && Minimum <= Default && Default <= Maximum
        && Current >= Minimum && Current <= Maximum && (long)Maximum - Minimum <= 1000000;
    public double Normalized => Current == Default ? 0 : (double)(Current - Default) / (Current > Default ? Maximum - Default : Default - Minimum);
    public NvidiaColorState WithNormalized(double value)
    {
        if (!IsValid || !double.IsFinite(value) || value < -1 || value > 1) throw new ArgumentOutOfRangeException(nameof(value));
        int range = value >= 0 ? Maximum - Default : Default - Minimum;
        return this with { Current = Math.Clamp(Default + (int)Math.Round(value * range), Minimum, Maximum) };
    }
}
