using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TorsherPlayer.Infrastructure;
using TorsherPlayer.Integration;

namespace TorsherPlayer.Player;

public sealed unsafe class MpvEngine : IDisposable
{
    private readonly FileLogger _log;
    private readonly LaunchOptions _launch;
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<CommandRequest> _commands = Channel.CreateUnbounded<CommandRequest>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly Thread _commandThread;
    private readonly Thread _eventThread;
    private readonly ConcurrentDictionary<ulong, PendingCommand> _pendingCommands = new();
    private sealed record CommandRequest(string[] Args, bool WaitForReply);
    private sealed record PendingCommand(string Description, TaskCompletionSource<int> Completion, long StartedAt);
    private IntPtr _mpv;
    private IntPtr _render;
    private long _nextCommandId;
    private PlayerSnapshot _snapshot = new(AudioTracks: [], SubtitleTracks: []);
    private MpvNative.GetProcAddressDelegate? _glProc;
    private MpvNative.RenderUpdateDelegate? _renderUpdate;
    private Action? _requestRender;
    private int _rendererReady;
    private int _disposed;

    public event Action<PlayerSnapshot>? StateChanged;
    public event Action<string>? PlayerEvent;
    public event Action<string>? Error;
    public bool RendererReady => Volatile.Read(ref _rendererReady) == 1;
    public PlayerSnapshot Snapshot { get { lock (_stateGate) return _snapshot; } }

    public MpvEngine(LaunchOptions launch, FileLogger log, string hardwareDecoding)
    {
        _launch = launch;
        _log = log;
        _mpv = MpvNative.mpv_create();
        if (_mpv == IntPtr.Zero) throw new InvalidOperationException("mpv_create() вернул NULL");

        SetOption("vo", "libmpv");
        SetOption("hwdec", hardwareDecoding);
        SetOption("audio-channels", "auto-safe");
        SetOption("cache", "yes");
        SetOption("cache-pause", "yes");
        SetOption("cache-pause-wait", "0.10");
        SetOption("cache-secs", "60");
        SetOption("demuxer-readahead-secs", "60");
        SetOption("demuxer-max-bytes", "256MiB");
        SetOption("demuxer-max-back-bytes", "128MiB");
        SetOption("demuxer-hysteresis-secs", "0");
        // Network-backed media (including the local HTTP bridge used by torrent
        // streaming) can legitimately need longer to deliver enough header/index
        // data for large 4K files. mpv defaults to 60 seconds; give HTTP sources a
        // wider startup/read window without touching protocols such as RTSP where
        // mpv explicitly warns against network-timeout.
        if (Uri.TryCreate(launch.Url, UriKind.Absolute, out var sourceUri) && sourceUri.Scheme is "http" or "https")
            SetOption("network-timeout", "180");
        SetOption("alang", "rus,ru,eng,en");
        SetOption("slang", "rus,ru,eng,en");
        SetOption("sid", "no");
        SetOption("keep-open", "no");
        SetOption("osd-level", "0");
        if (!string.IsNullOrWhiteSpace(launch.UserAgent)) SetOption("user-agent", launch.UserAgent!);
        if (!string.IsNullOrWhiteSpace(launch.Referrer)) SetOption("referrer", launch.Referrer!);
        if (launch.HttpHeaders.Count > 0) SetOption("http-header-fields", string.Join(',', launch.HttpHeaders));

        Check(MpvNative.mpv_initialize(_mpv), "mpv_initialize");
        MpvNative.mpv_request_log_messages(_mpv, "warn");
        Observe("time-pos", MpvNative.MpvFormat.Double);
        Observe("duration", MpvNative.MpvFormat.Double);
        Observe("pause", MpvNative.MpvFormat.Flag);
        Observe("mute", MpvNative.MpvFormat.Flag);
        Observe("volume", MpvNative.MpvFormat.Double);
        Observe("speed", MpvNative.MpvFormat.Double);
        Observe("sub-delay", MpvNative.MpvFormat.Double);
        Observe("audio-delay", MpvNative.MpvFormat.Double);
        Observe("sub-scale", MpvNative.MpvFormat.Double);
        Observe("demuxer-cache-duration", MpvNative.MpvFormat.Double);
        Observe("cache-buffering-state", MpvNative.MpvFormat.Double);
        Observe("paused-for-cache", MpvNative.MpvFormat.Flag);
        Observe("media-title", MpvNative.MpvFormat.String);
        Observe("seeking", MpvNative.MpvFormat.Flag);
        Observe("track-list/count", MpvNative.MpvFormat.Int64);

        // IMPORTANT: the Avalonia/OpenGL render thread is the libmpv render thread. With
        // MPV_RENDER_PARAM_ADVANCED_CONTROL enabled it must never call normal
        // libmpv APIs. All playback commands therefore cross this queue and are
        // executed by a dedicated core-command thread.
        _commandThread = new Thread(CommandLoop) { Name = "libmpv-commands", IsBackground = true };
        _eventThread = new Thread(EventLoop) { Name = "libmpv-events", IsBackground = true };
        _commandThread.Start();
        _eventThread.Start();
    }

    private void SetOption(string name, string value)
    {
        var rc = MpvNative.mpv_set_option_string(_mpv, name, value);
        if (rc < 0) _log.Warn($"mpv option {name}={value}: {MpvNative.ErrorString(rc)}");
    }

    private void Observe(string name, MpvNative.MpvFormat format)
    {
        var rc = MpvNative.mpv_observe_property(_mpv, 0, name, format);
        if (rc < 0) _log.Warn($"observe {name}: {MpvNative.ErrorString(rc)}");
    }

    public void InitializeRenderer(Func<string, IntPtr> getProcAddress, Action requestRender)
    {
        if (RendererReady) return;
        ArgumentNullException.ThrowIfNull(getProcAddress);
        _requestRender = requestRender;
        _glProc = (_, namePtr) => getProcAddress(MpvNative.PtrToUtf8(namePtr));
        _renderUpdate = _ => _requestRender?.Invoke();
        var api = Marshal.StringToCoTaskMemUTF8("opengl");
        var init = new MpvNative.MpvOpenGlInitParams { GetProcAddress = Marshal.GetFunctionPointerForDelegate(_glProc), GetProcAddressCtx = IntPtr.Zero };
        int advanced = 1;
        try
        {
            var p = stackalloc MpvNative.MpvRenderParam[4];
            p[0] = new(MpvNative.MpvRenderParamType.ApiType, api);
            p[1] = new(MpvNative.MpvRenderParamType.OpenGlInitParams, (IntPtr)(&init));
            p[2] = new(MpvNative.MpvRenderParamType.AdvancedControl, (IntPtr)(&advanced));
            p[3] = new(MpvNative.MpvRenderParamType.Invalid, IntPtr.Zero);
            Check(MpvNative.mpv_render_context_create(out _render, _mpv, (IntPtr)p), "mpv_render_context_create");
            MpvNative.mpv_render_context_set_update_callback(_render, _renderUpdate, IntPtr.Zero);
            Volatile.Write(ref _rendererReady, 1);
            _log.Info("libmpv OpenGL render context initialized");
        }
        finally { Marshal.FreeCoTaskMem(api); }
    }

    public void Render(int framebuffer, int width, int height)
    {
        if (!RendererReady || _render == IntPtr.Zero || width <= 0 || height <= 0) return;
        MpvNative.mpv_render_context_update(_render);
        var fbo = new MpvNative.MpvOpenGlFbo { Fbo = framebuffer, W = width, H = height, InternalFormat = 0 };
        // libmpv renders into Avalonia's OpenGL FBO. Keep the explicit Y flip that
        // was validated on the real TV/Windows setup; it can be made configurable
        // later if another platform presents its FBO with different orientation.
        int flip = 1;
        int blockForTargetTime = 0;
        var p = stackalloc MpvNative.MpvRenderParam[4];
        p[0] = new(MpvNative.MpvRenderParamType.OpenGlFbo, (IntPtr)(&fbo));
        p[1] = new(MpvNative.MpvRenderParamType.FlipY, (IntPtr)(&flip));
        p[2] = new(MpvNative.MpvRenderParamType.BlockForTargetTime, (IntPtr)(&blockForTargetTime));
        p[3] = new(MpvNative.MpvRenderParamType.Invalid, IntPtr.Zero);
        var rc = MpvNative.mpv_render_context_render(_render, (IntPtr)p);
        if (rc < 0) _log.Warn($"render: {MpvNative.ErrorString(rc)}");
    }

    public void Load(double resumeSeconds)
    {
        if (!RendererReady) throw new InvalidOperationException("Render context ещё не готов");
        // A retry must start from a clean observable state without rebuilding libmpv or
        // discarding the demuxer/cache machinery. Commands are asynchronous and preserve UI responsiveness.
        Update(s => s with { FileLoaded = false, Ended = false, LastError = "", Paused = false });
        // Set before loadfile so a very fast local file cannot beat FILE_LOADED.
        Interlocked.Exchange(ref _pendingResume, resumeSeconds > 0.1 ? resumeSeconds : 0);
        Command("set", "pause", "no");
        Command("set", "sid", "no");
        Command("loadfile", _launch.Url!, "replace");
    }

    private double _pendingResume;
    public void TogglePause() => Command("cycle", "pause");
    public void SetPause(bool paused) => Command("set", "pause", paused ? "yes" : "no");
    public void SeekRelative(double seconds) => Command("seek", F(seconds), "relative+exact");
    public void SeekAbsolute(double seconds) => Command("seek", F(Math.Max(0, seconds)), "absolute+exact");
    public void SetVolume(double value) => Command("set", "volume", F(Math.Clamp(value, 0, 150)));
    public void AddVolume(double delta) => Command("add", "volume", F(delta));
    public void ToggleMute() => Command("cycle", "mute");
    public void SetMute(bool muted) => Command("set", "mute", muted ? "yes" : "no");
    public void SetSpeed(double value) => Command("set", "speed", F(Math.Clamp(value, .25, 4)));
    public void SetSubtitleDelay(double value) => Command("set", "sub-delay", F(value));
    public void SetAudioDelay(double value) => Command("set", "audio-delay", F(value));
    public void SetSubtitleScale(double value) => Command("set", "sub-scale", F(Math.Clamp(value, .5, 3)));
    public void SetAudioTrack(int id) { Command("set", "aid", id.ToString(CultureInfo.InvariantCulture)); MarkSelected("audio", id); }
    public void SetSubtitleTrack(int? id) { Command("set", "sid", id?.ToString(CultureInfo.InvariantCulture) ?? "no"); MarkSelected("sub", id); }
    public void AddSubtitle(string path) { CommandUnsequenced("sub-add", path, "select"); }
    public void AddAudio(string path) { CommandUnsequenced("audio-add", path, "select"); }
    public void Screenshot(string path) => CommandUnsequenced("screenshot-to-file", path, "window");

    private void Command(params string[] args) => EnqueueCommand(true, args);
    private void CommandUnsequenced(params string[] args) => EnqueueCommand(false, args);

    private void EnqueueCommand(bool waitForReply, params string[] args)
    {
        // Never touch the mpv core from the Avalonia/render thread. Enqueuing is the
        // only work performed by callers and cannot block on decoding/network IO.
        if (Volatile.Read(ref _disposed) != 0 || args.Length == 0) return;
        if (!_commands.Writer.TryWrite(new CommandRequest(args, waitForReply)))
            _log.Warn($"command queue rejected: {string.Join(' ', args)}");
    }

    private void CommandLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!_commands.Reader.WaitToReadAsync(_cts.Token).AsTask().GetAwaiter().GetResult()) break;
                while (_commands.Reader.TryRead(out var request))
                {
                    if (_cts.IsCancellationRequested || _mpv == IntPtr.Zero) return;
                    var args = request.Args;
                    var id = (ulong)Interlocked.Increment(ref _nextCommandId);
                    var description = string.Join(' ', args);
                    var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingCommands[id] = new PendingCommand(description, completion, Stopwatch.GetTimestamp());
                    var rc = MpvNative.CommandAsync(_mpv, id, args);
                    if (rc < 0)
                    {
                        _pendingCommands.TryRemove(id, out _);
                        _log.Warn($"command {description}: {MpvNative.ErrorString(rc)}");
                        continue;
                    }

                    // libmpv explicitly does not guarantee ordering between independent
                    // async requests. Playback controls are therefore serialized by their
                    // COMMAND_REPLY. Slow ancillary operations (external track loading,
                    // screenshots) are intentionally fire-and-forget so they cannot hold
                    // pause/seek/volume behind I/O.
                    if (request.WaitForReply)
                    {
                        try
                        {
                            completion.Task.WaitAsync(TimeSpan.FromSeconds(2), _cts.Token).GetAwaiter().GetResult();
                        }
                        catch (TimeoutException)
                        {
                            if (_pendingCommands.TryRemove(id, out _))
                                _log.Warn($"command reply timeout: {description}");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { _log.Exception(ex, "libmpv command loop"); Error?.Invoke(ex.Message); }
    }

    private void EventLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested && _mpv != IntPtr.Zero)
            {
                var ptr = MpvNative.mpv_wait_event(_mpv, 0.25);
                if (ptr == IntPtr.Zero) continue;
                var ev = Marshal.PtrToStructure<MpvNative.MpvEvent>(ptr);
                switch (ev.EventId)
                {
                    case MpvNative.MpvEventId.None: break;
                    case MpvNative.MpvEventId.PropertyChange: HandleProperty(ev.Data); break;
                    case MpvNative.MpvEventId.CommandReply:
                    {
                        if (_pendingCommands.TryRemove(ev.ReplyUserdata, out var pending))
                        {
                            var elapsed = Stopwatch.GetElapsedTime(pending.StartedAt);
                            if (elapsed >= TimeSpan.FromMilliseconds(150))
                                _log.Warn($"slow command {elapsed.TotalMilliseconds:0} ms: {pending.Description}");
                            if (ev.Error < 0) _log.Warn($"async command {pending.Description}: {MpvNative.ErrorString(ev.Error)}");
                            pending.Completion.TrySetResult(ev.Error);
                        }
                        else if (ev.Error < 0)
                        {
                            _log.Warn($"async command #{ev.ReplyUserdata}: {MpvNative.ErrorString(ev.Error)}");
                        }
                        break;
                    }
                    case MpvNative.MpvEventId.FileLoaded:
                        Update(s => s with { FileLoaded = true, Ended = false, LastError = "" });
                        RefreshTracks();
                        foreach (var file in _launch.SubtitleFiles) CommandUnsequenced("sub-add", file, "auto");
                        foreach (var file in _launch.AudioFiles) CommandUnsequenced("audio-add", file, "auto");
                        var resume = Interlocked.Exchange(ref _pendingResume, 0);
                        // Read the loaded file directly: observed duration can still belong to the previous episode.
                        if (resume > .1 &&
                            double.TryParse(MpvNative.GetString(_mpv, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) &&
                            IsNearEnd(resume, duration))
                        {
                            _log.Info($"Saved position {resume:0.0}s is near the end ({duration:0.0}s); starting from the beginning");
                            resume = 0;
                        }
                        if (resume > .1) Command("seek", F(resume), "absolute+exact");
                        PlayerEvent?.Invoke("file_loaded");
                        break;
                    case MpvNative.MpvEventId.Seek: PlayerEvent?.Invoke("seeked"); break;
                    case MpvNative.MpvEventId.PlaybackRestart: PlayerEvent?.Invoke("playback_restart"); break;
                    case MpvNative.MpvEventId.AudioReconfig: RefreshTracks(); break;
                    case MpvNative.MpvEventId.EndFile: HandleEndFile(ev.Data); break;
                    case MpvNative.MpvEventId.LogMessage: HandleLog(ev.Data); break;
                    case MpvNative.MpvEventId.QueueOverflow: _log.Warn("libmpv event queue overflow"); break;
                    case MpvNative.MpvEventId.Shutdown: return;
                }
            }
        }
        catch (Exception ex) { _log.Exception(ex, "libmpv event loop"); Error?.Invoke(ex.Message); }
    }

    private void HandleProperty(IntPtr data)
    {
        if (data == IntPtr.Zero) return;
        var p = Marshal.PtrToStructure<MpvNative.MpvEventProperty>(data);
        var name = MpvNative.PtrToUtf8(p.Name);
        try
        {
            switch (p.Format)
            {
                case MpvNative.MpvFormat.Double when p.Data != IntPtr.Zero:
                    var d = Marshal.PtrToStructure<double>(p.Data);
                    if (!double.IsFinite(d)) break;
                    Update(s => name switch
                    {
                        "time-pos" => s with { Position = d }, "duration" => s with { Duration = d }, "volume" => s with { Volume = d }, "speed" => s with { Speed = d },
                        "sub-delay" => s with { SubtitleDelay = d }, "audio-delay" => s with { AudioDelay = d }, "sub-scale" => s with { SubtitleScale = d },
                        "demuxer-cache-duration" => s with { CacheSeconds = d },
                        "cache-buffering-state" => s with { CachePercent = Math.Clamp(d, 0, 100) }, _ => s
                    });
                    break;
                case MpvNative.MpvFormat.Flag when p.Data != IntPtr.Zero:
                    var b = Marshal.ReadInt32(p.Data) != 0;
                    var before = Snapshot;
                    Update(s => name switch
                    {
                        "pause" => s with { Paused = b },
                        "mute" => s with { Muted = b },
                        "seeking" => s with { Seeking = b },
                        "paused-for-cache" => s with { Buffering = b },
                        _ => s
                    });
                    if (name == "pause" && before.Paused != b)
                    {
                        _log.Info($"user pause state -> {(b ? "paused" : "playing")}");
                        PlayerEvent?.Invoke(b ? "paused" : "playing");
                    }
                    if (name == "paused-for-cache" && before.Buffering != b)
                        _log.Info($"cache pause state -> {(b ? "buffering" : "ready")}, cache={Snapshot.CacheSeconds:0.0}s, target={Snapshot.CachePercent:0}%");
                    break;
                case MpvNative.MpvFormat.String when p.Data != IntPtr.Zero:
                    var strPtr = Marshal.ReadIntPtr(p.Data);
                    var text = MpvNative.PtrToUtf8(strPtr);
                    if (name == "media-title") Update(s => s with { MediaTitle = text });
                    break;
                case MpvNative.MpvFormat.Int64 when p.Data != IntPtr.Zero:
                    if (name == "track-list/count") RefreshTracks();
                    break;
            }
        }
        catch (Exception ex) { _log.Exception(ex, $"property-change {name}"); }
    }

    private static bool IsNearEnd(double position, double duration) =>
        double.IsFinite(position) && double.IsFinite(duration) && duration > 0 &&
        (position >= Math.Max(0, duration - 5) || position / duration >= 0.98);

    private void HandleEndFile(IntPtr data)
    {
        if (data == IntPtr.Zero) return;
        var end = Marshal.PtrToStructure<MpvNative.MpvEventEndFile>(data);
        if (end.Error < 0 || end.Reason == MpvNative.MpvEndFileReason.Error)
        {
            var message = end.Error < 0 ? MpvNative.ErrorString(end.Error) : "Ошибка воспроизведения";
            Update(s => s with { LastError = message, Ended = true, FileLoaded = false });
            Error?.Invoke(message); PlayerEvent?.Invoke("error");
        }
        else if (end.Reason == MpvNative.MpvEndFileReason.Eof)
        {
            var current = Snapshot;
            if (IsNearEnd(current.Position, current.Duration))
            {
                Update(s => s with { Ended = true, Position = Math.Max(s.Position, s.Duration) });
                PlayerEvent?.Invoke("completed");
            }
            else
            {
                // Network/TorrServer streams can surface an EOF when the upstream connection is lost.
                // Never mark an episode watched or close the player unless we were actually near the end.
                const string message = "Поток неожиданно завершился. Можно повторить воспроизведение с текущей позиции.";
                Update(s => s with { LastError = message, Ended = true, FileLoaded = false });
                Error?.Invoke(message);
                PlayerEvent?.Invoke("error");
            }
        }
    }

    private void HandleLog(IntPtr data)
    {
        if (data == IntPtr.Zero) return;
        var m = Marshal.PtrToStructure<MpvNative.MpvEventLogMessage>(data);
        var level = MpvNative.PtrToUtf8(m.Level);
        var text = MpvNative.PtrToUtf8(m.Text).TrimEnd();
        if (level is "error" or "fatal" or "warn") _log.Warn($"mpv/{MpvNative.PtrToUtf8(m.Prefix)}: {text}");
    }

    private void MarkSelected(string type, int? id)
    {
        Update(s => type == "audio"
            ? s with { AudioTracks = (s.AudioTracks ?? []).Select(t => t with { Selected = t.Id == id }).ToArray() }
            : s with { SubtitleTracks = (s.SubtitleTracks ?? []).Select(t => t with { Selected = t.Id == id }).ToArray() });
    }

    private void RefreshTracks()
    {
        if (_mpv == IntPtr.Zero) return;
        try
        {
            var count = ParseInt(MpvNative.GetString(_mpv, "track-list/count"));
            var audio = new List<MediaTrack>(); var subs = new List<MediaTrack>();
            for (var i = 0; i < count; i++)
            {
                string P(string n) => MpvNative.GetString(_mpv, $"track-list/{i}/{n}") ?? "";
                var type = P("type");
                if (type is not ("audio" or "sub")) continue;
                var t = new MediaTrack(ParseInt(P("id")), type, P("title"), P("lang"), P("codec"), Yes(P("selected")), Yes(P("default")), Yes(P("forced")), Yes(P("external")));
                if (type == "audio") audio.Add(t); else subs.Add(t);
            }
            Update(s => s with { AudioTracks = audio, SubtitleTracks = subs });
        }
        catch (Exception ex) { _log.Exception(ex, "RefreshTracks"); }
    }

    private static bool Yes(string s) => s is "yes" or "true" or "1";
    private static int ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0;
    private static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    private void Update(Func<PlayerSnapshot, PlayerSnapshot> apply)
    {
        PlayerSnapshot copy;
        lock (_stateGate) { _snapshot = apply(_snapshot); copy = _snapshot; }
        StateChanged?.Invoke(copy);
    }

    private void Check(int rc, string operation) { if (rc < 0) throw new InvalidOperationException($"{operation}: {MpvNative.ErrorString(rc)}"); }

    public void ReleaseRenderer()
    {
        if (_render == IntPtr.Zero) return;
        MpvNative.mpv_render_context_set_update_callback(_render, null, IntPtr.Zero);
        MpvNative.mpv_render_context_free(_render);
        _render = IntPtr.Zero;
        Volatile.Write(ref _rendererReady, 0);
        _log.Info("libmpv render context released");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _commands.Writer.TryComplete();
        _cts.Cancel();
        try { if (_commandThread.IsAlive) _commandThread.Join(700); } catch { }
        try { if (_eventThread.IsAlive) _eventThread.Join(700); } catch { }
        if (_render != IntPtr.Zero) _log.Warn("Renderer was not released on GL thread before engine dispose");
        _pendingCommands.Clear();
        if (_mpv != IntPtr.Zero) { MpvNative.mpv_terminate_destroy(_mpv); _mpv = IntPtr.Zero; }
        _cts.Dispose();
    }
}
