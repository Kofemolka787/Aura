using System.Windows;
using System.Windows.Media;
using ColorForge.Models;
using ColorForge.Windows;

namespace ColorForge.Controls;

public sealed class CrosshairPreview : FrameworkElement
{
    private CrosshairSettings _settings = new();
    public void UpdateSettings(CrosshairSettings settings) { _settings = settings; InvalidateVisual(); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        CrosshairRenderer.Draw(context, new Point(ActualWidth / 2, ActualHeight / 2), _settings);
    }
}
