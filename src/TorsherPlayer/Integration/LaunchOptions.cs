using System.Globalization;

namespace TorsherPlayer.Integration;

public sealed class LaunchOptions
{
    public string? Url { get; private set; }
    public string? TorrentPath { get; private set; }
    public int? TorrentFileIndex { get; private set; }
    public string MediaKey { get; private set; } = "";
    public string Title { get; private set; } = "";
    public double? ResumeSeconds { get; private set; }
    public string? Callback { get; private set; }
    public string? CallbackToken { get; private set; }
    public string? ProgressFile { get; private set; }
    public string? UserAgent { get; private set; }
    public string? Referrer { get; private set; }
    public List<string> HttpHeaders { get; } = [];
    public List<string> SubtitleFiles { get; } = [];
    public List<string> AudioFiles { get; } = [];
    public bool Windowed { get; private set; }
    public double? UiScale { get; private set; }
    public double? CompletedAt { get; private set; }
    public int? ProgressEverySeconds { get; private set; }
    public string? InitialError { get; private set; }
    public bool IsTorrent => !string.IsNullOrWhiteSpace(TorrentPath);

    public static LaunchOptions Error(string message) => new() { InitialError = message };

    public void ResolveMedia(string url, string? mediaKey = null, string? title = null)
    {
        Url = url;
        if (!string.IsNullOrWhiteSpace(mediaKey)) MediaKey = mediaKey;
        if (!string.IsNullOrWhiteSpace(title)) Title = title;
        InitialError = null;
    }

    public static LaunchOptions Parse(string[] args)
    {
        var o = new LaunchOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Need() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Нет значения после {args[i]}");
            switch (args[i])
            {
                case "--url": o.Url = Need(); break;
                case "--torrent": o.TorrentPath = Need(); break;
                case "--torrent-file-index": o.TorrentFileIndex = int.Parse(Need(), CultureInfo.InvariantCulture); break;
                case "--media-key": o.MediaKey = Need(); break;
                case "--title": o.Title = Need(); break;
                case "--resume": o.ResumeSeconds = double.Parse(Need(), CultureInfo.InvariantCulture); break;
                case "--callback": o.Callback = Need(); break;
                case "--callback-token": o.CallbackToken = Need(); break;
                case "--progress-file": o.ProgressFile = Need(); break;
                case "--user-agent": o.UserAgent = Need(); break;
                case "--referrer": o.Referrer = Need(); break;
                case "--http-header": o.HttpHeaders.Add(Need()); break;
                case "--sub-file": o.SubtitleFiles.Add(Need()); break;
                case "--audio-file": o.AudioFiles.Add(Need()); break;
                case "--windowed": o.Windowed = true; break;
                case "--ui-scale": o.UiScale = double.Parse(Need(), CultureInfo.InvariantCulture); break;
                case "--completed-at": o.CompletedAt = double.Parse(Need(), CultureInfo.InvariantCulture); break;
                case "--progress-every": o.ProgressEverySeconds = int.Parse(Need(), CultureInfo.InvariantCulture); break;
                case "--help": throw new ArgumentException("Использование: TorsherPlayer (--url <url> | --torrent <file.torrent>) [--torrent-file-index N] [--title ...] [--media-key ...] [--resume seconds]");
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Неизвестный параметр: {args[i]}");
                    if (o.Url is null && o.TorrentPath is null)
                    {
                        if (args[i].EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) o.TorrentPath = args[i];
                        else o.Url = args[i];
                    }
                    else throw new ArgumentException($"Лишний аргумент: {args[i]}");
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(o.Url) && !string.IsNullOrWhiteSpace(o.TorrentPath))
            throw new ArgumentException("Передайте либо --url, либо --torrent, но не оба сразу.");
        if (o.TorrentFileIndex is < 0) throw new ArgumentOutOfRangeException("--torrent-file-index", "Индекс не может быть отрицательным");
        if (o.ResumeSeconds is not null && (!double.IsFinite(o.ResumeSeconds.Value) || o.ResumeSeconds < 0)) throw new ArgumentOutOfRangeException("--resume", "Ожидается конечное неотрицательное число секунд");
        if (o.UiScale is not null && (!double.IsFinite(o.UiScale.Value) || o.UiScale < .9 || o.UiScale > 2.5)) throw new ArgumentOutOfRangeException("--ui-scale", "Допустимый диапазон: 0.9–2.5");
        if (o.CompletedAt is not null && (!double.IsFinite(o.CompletedAt.Value) || o.CompletedAt <= 0 || o.CompletedAt > 1)) throw new ArgumentOutOfRangeException("--completed-at", "Допустимый диапазон: (0, 1]");
        if (o.ProgressEverySeconds is not null && (o.ProgressEverySeconds < 2 || o.ProgressEverySeconds > 60)) throw new ArgumentOutOfRangeException("--progress-every", "Допустимый диапазон: 2–60 секунд");
        if (string.IsNullOrWhiteSpace(o.Url) && string.IsNullOrWhiteSpace(o.TorrentPath))
            o.InitialError = "Не передан источник. Используйте --url <адрес> или --torrent <file.torrent>.";
        return o;
    }
}
