using System;
using System.Windows;
using System.Windows.Media;
using ColorForge.Models;
using ColorForge.Services;

namespace ColorForge.Windows;

public static class CrosshairRenderer
{
    public static void Draw(DrawingContext context, Point center, CrosshairSettings settings)
    {
        var raw = System.Drawing.Color.FromArgb(settings.Argb);
        Brush brush = new SolidColorBrush(Color.FromArgb(raw.A, raw.R, raw.G, raw.B));
        double thickness = Math.Max(1, settings.Thickness), outline = Math.Max(0, settings.Outline);
        if (settings.Shape == CrosshairShape.Dot)
        {
            double radius = Math.Max(0.5, settings.Size / 2d);
            if (outline > 0) context.DrawEllipse(Brushes.Black, null, center, radius + outline, radius + outline);
            context.DrawEllipse(brush, null, center, radius, radius);
        }
        else if (settings.Shape == CrosshairShape.Circle)
        {
            if (outline > 0) context.DrawEllipse(null, new Pen(Brushes.Black, thickness + outline * 2), center, settings.Size, settings.Size);
            context.DrawEllipse(null, new Pen(brush, thickness), center, settings.Size, settings.Size);
        }
        else
        {
            foreach (CrosshairLine line in CrosshairGeometry.Lines(settings))
            {
                Point start = new(center.X + line.X1, center.Y + line.Y1), end = new(center.X + line.X2, center.Y + line.Y2);
                if (outline > 0) context.DrawLine(new Pen(Brushes.Black, thickness + outline * 2), start, end);
                context.DrawLine(new Pen(brush, thickness), start, end);
            }
        }
    }
}
