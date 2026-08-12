using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TorsherPlayer.Infrastructure;
using TorsherPlayer.Player;

namespace TorsherPlayer.Integration;

public sealed class ProgressReporter : IAsyncDisposable
{
    private readonly LaunchOptions _launch;
    private readonly FileLogger _log;
    private readonly double _completedAt;
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..16];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly Channel<ProgressEvent> _queue = Channel.CreateUnbounded<ProgressEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private int _terminal;

    public ProgressReporter(LaunchOptions launch, FileLogger log, double completedAt)
    {
        _launch = launch; _log = log; _completedAt = completedAt;
        _worker = Task.Run(Worker);
    }

    public double? LoadLocalResume()
    {
        if (string.IsNullOrWhiteSpace(_launch.MediaKey)) return null;
        try
        {
            var path = CheckpointPath(); if (!File.Exists(path)) return null;
            var ev = JsonSerializer.Deserialize<ProgressEvent>(File.ReadAllText(path), Json);
            if (ev is null || ev.Watched || ev.Position <= 1) return null;
            return ev.Position;
        }
        catch (Exception ex) { _log.Exception(ex, "Local checkpoint read"); return null; }
    }

    public void Emit(string name, PlayerSnapshot s, string? error = null)
    {
        // Playback errors are recoverable: the player can retry the stream without creating a new session.
        // Only completed/stopped close the reporting stream.
        if (name is "completed" or "stopped")
            if (Interlocked.Exchange(ref _terminal, 1) != 0) return;
        if (Volatile.Read(ref _terminal) != 0 && name is not ("completed" or "stopped")) return;

        var ev = new ProgressEvent
        {
            Version=1, Event=name, SessionId=_sessionId, MediaKey=_launch.MediaKey, Title=string.IsNullOrWhiteSpace(_launch.Title)?s.MediaTitle:_launch.Title,
            Position=s.Position, Duration=s.Duration, Percent=s.Percent, Paused=s.Paused,
            Watched=s.Duration > 0 && s.Position/s.Duration >= _completedAt, Volume=s.Volume, Muted=s.Muted, Speed=s.Speed,
            AudioTrack=TrackDto.From(s.SelectedAudio), SubtitleTrack=TrackDto.From(s.SelectedSubtitle), Error=error, Timestamp=DateTimeOffset.UtcNow
        };
        _queue.Writer.TryWrite(ev);
    }

    private async Task Worker()
    {
        await foreach (var ev in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            try
            {
                if (ShouldCheckpoint(ev)) SaveCheckpoint(ev);
                if (!string.IsNullOrWhiteSpace(_launch.ProgressFile)) SaveAtomic(_launch.ProgressFile!, JsonSerializer.Serialize(ev, Json));
                if (!string.IsNullOrWhiteSpace(_launch.Callback)) await SendCallback(ev).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.Exception(ex, $"progress event {ev.Event}"); }
        }
    }


    private static bool ShouldCheckpoint(ProgressEvent ev)
    {
        // Never destroy a useful resume point with startup/error noise at 0 seconds.
        // A real playback position is enough; completed events are also retained as watched
        // so LoadLocalResume() knows not to resume at the credits.
        return ev.Position > 1 || ev.Watched || ev.Event == "completed";
    }

    private async Task SendCallback(ProgressEvent ev)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _launch.Callback);
        req.Content = new StringContent(JsonSerializer.Serialize(ev, Json), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(_launch.CallbackToken)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _launch.CallbackToken);
        using var res = await _http.SendAsync(req, _cts.Token).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) _log.Warn($"callback returned {(int)res.StatusCode}");
    }

    private void SaveCheckpoint(ProgressEvent ev)
    {
        if (string.IsNullOrWhiteSpace(_launch.MediaKey)) return;
        SaveAtomic(CheckpointPath(), JsonSerializer.Serialize(ev, Json));
    }

    private string CheckpointPath()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_launch.MediaKey))).ToLowerInvariant();
        return Path.Combine(AppPaths.State, hash + ".json");
    }

    private static void SaveAtomic(string path, string value)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp"; File.WriteAllText(tmp, value); File.Move(tmp, full, true);
    }

    public async Task FlushAsync(TimeSpan timeout)
    {
        _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(timeout); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync(TimeSpan.FromSeconds(2)); _cts.Cancel(); _http.Dispose(); _cts.Dispose();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed class ProgressEvent
{
    public int Version {get;set;} public string Event {get;set;}=""; public string SessionId {get;set;}=""; public string MediaKey {get;set;}=""; public string Title {get;set;}="";
    public double Position {get;set;} public double Duration {get;set;} public double Percent {get;set;} public bool Paused {get;set;} public bool Watched {get;set;}
    public double Volume {get;set;} public bool Muted {get;set;} public double Speed {get;set;} public TrackDto? AudioTrack {get;set;} public TrackDto? SubtitleTrack {get;set;}
    public string? Error {get;set;} public DateTimeOffset Timestamp {get;set;}
}
public sealed record TrackDto(int Id,string Type,string Title,string Lang,string Codec,bool Selected,bool Default,bool Forced,bool External)
{
    public static TrackDto? From(MediaTrack? t) => t is null ? null : new(t.Id,t.Type,t.Title,t.Lang,t.Codec,t.Selected,t.Default,t.Forced,t.External);
}
