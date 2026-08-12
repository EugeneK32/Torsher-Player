namespace TorsherPlayer.Player;

public sealed record PlayerSnapshot(
    double Position = 0,
    double Duration = 0,
    bool Paused = false,
    bool Muted = false,
    double Volume = 100,
    double Speed = 1,
    double SubtitleDelay = 0,
    double AudioDelay = 0,
    double SubtitleScale = 1,
    double CacheSeconds = 0,
    bool Buffering = false,
    double CachePercent = 0,
    bool Seeking = false,
    bool FileLoaded = false,
    bool Ended = false,
    string MediaTitle = "",
    string LastError = "",
    IReadOnlyList<MediaTrack>? AudioTracks = null,
    IReadOnlyList<MediaTrack>? SubtitleTracks = null)
{
    public double Percent => Duration <= 0 ? 0 : Math.Clamp(Position / Duration * 100, 0, 100);
    public MediaTrack? SelectedAudio => AudioTracks?.FirstOrDefault(x => x.Selected);
    public MediaTrack? SelectedSubtitle => SubtitleTracks?.FirstOrDefault(x => x.Selected);
}
