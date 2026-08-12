namespace TorsherPlayer.Infrastructure;

public static class AppPaths
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TorsherPlayer");
    public static readonly string Logs = Path.Combine(Root, "logs");
    public static readonly string State = Path.Combine(Root, "state");
    public static readonly string Torrents = Path.Combine(Root, "torrents");
    public static readonly string SettingsFile = Path.Combine(Root, "settings.json");
    public static readonly string AppLog = Path.Combine(Logs, "torsherplayer.log");
    public static readonly string InstanceLock = Path.Combine(Root, "instance.lock");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Torrents);
    }
}
