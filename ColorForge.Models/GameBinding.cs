namespace ColorForge.Models;

public sealed class GameBinding
{
    public string DisplayName { get; set; } = string.Empty;
    public string MonitorDeviceName { get; set; } = string.Empty;
    public bool ApplyColor { get; set; } = true;
    public bool UseCrosshair { get; set; }
    public CrosshairSettings Crosshair { get; set; } = new();
    public bool UseOverlay { get; set; }
    public OverlaySettings Overlay { get; set; } = new();
    public bool RestoreOnFocusLoss { get; set; } = true;
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? ProcessName : DisplayName;
    public string Description => (ApplyColor ? ProfileName : "цвет без изменений")
        + (UseCrosshair ? " · свой прицел" : "") + (UseOverlay ? " · свой оверлей" : "")
        + (RestoreOnFocusLoss ? " · возврат при Alt-Tab" : " · до выхода из игры");
    public GameBinding Clone()
    {
        GameBinding copy = (GameBinding)MemberwiseClone();
        copy.Crosshair = (Crosshair ?? new()).Clone();
        copy.Overlay = (Overlay ?? new()).Clone();
        return copy;
    }
	public string ProcessName { get; set; } = "RustClient";

	public string ProfileName { get; set; } = "AURA · Зелёнка";

	public bool Enabled { get; set; } = true;
}
