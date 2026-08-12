namespace TorsherPlayer.Torrent;

public sealed record TorrentMediaItem(int Index, string Path, string Name, long SizeBytes)
{
    public string SizeText => SizeBytes switch
    {
        >= 1L << 30 => $"{SizeBytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{SizeBytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{SizeBytes / (double)(1L << 10):0} KB",
        _ => $"{SizeBytes} B"
    };
}
