using System.Collections.Generic;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed record CrosshairLine(double X1, double Y1, double X2, double Y2);
public static class CrosshairGeometry
{
    public static IReadOnlyList<CrosshairLine> Lines(CrosshairSettings settings)
    {
        double size = settings.Size, gap = settings.Gap;
        if (settings.Shape is CrosshairShape.Circle or CrosshairShape.Dot) return System.Array.Empty<CrosshairLine>();
        List<CrosshairLine> lines = new()
        {
            new(-gap-size,0,-gap,0), new(gap,0,gap+size,0), new(0,gap,0,gap+size)
        };
        if (settings.Shape == CrosshairShape.Cross) lines.Add(new(0,-gap-size,0,-gap));
        return lines;
    }
}
