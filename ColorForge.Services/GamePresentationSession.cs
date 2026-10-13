using ColorForge.Models;

namespace ColorForge.Services;

// Game overrides remain in memory; the common configuration is never replaced by them.
public sealed class GamePresentationSession
{
    private readonly AppSettings _settings;
    private CrosshairSettings? _crosshair;
    private OverlaySettings? _overlay;
    public GameBinding? Binding { get; private set; }
    public GamePresentationSession(AppSettings settings) => _settings = settings;
    public CrosshairSettings Crosshair => Binding?.UseCrosshair == true ? _crosshair! : _settings.Crosshair;
    public OverlaySettings Overlay => Binding?.UseOverlay == true ? _overlay! : _settings.Overlay;
    public void Activate(GameBinding? binding)
    {
        Binding = binding?.Clone();
        _crosshair = Binding?.Crosshair.Clone(); _overlay = Binding?.Overlay.Clone();
    }
}
