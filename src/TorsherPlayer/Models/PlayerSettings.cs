namespace TorsherPlayer.Models;

public sealed class PlayerSettings
{
    public double UiScale { get; set; } = 1.80;
    public double CompletedAt { get; set; } = 0.90;
    public int ProgressEverySeconds { get; set; } = 5;
    public bool StartFullscreen { get; set; } = true;
    public string HardwareDecoding { get; set; } = "auto-copy";
    public string Theme { get; set; } = "Midnight";
}
