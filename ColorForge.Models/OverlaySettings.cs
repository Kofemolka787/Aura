namespace ColorForge.Models;

public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }
public sealed class OverlaySettings
{
    public bool Enabled { get; set; }
    public bool ShowFps { get; set; } = true;
    public bool ShowFrameGraph { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowVram { get; set; } = true;
    public bool ShowProfile { get; set; } = true;
    public OverlayCorner Corner { get; set; }
    public double Scale { get; set; } = 1;
    public double Opacity { get; set; } = 0.85;
    public OverlaySettings Clone() => (OverlaySettings)MemberwiseClone();
}
