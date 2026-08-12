using System.Collections;
using System.Reflection;
using TorsherPlayer.Infrastructure;

namespace TorsherPlayer.Torrent;

/// <summary>
/// Thin compatibility layer around MonoTorrent's streaming API. Reflection is
/// intentional: MonoTorrent's public async primitives have changed between
/// releases, while the stable AddStreamingAsync/StreamProvider surface remains.
/// Keeping those details here prevents torrent implementation types leaking into UI.
/// </summary>
public sealed class TorrentSession : IAsyncDisposable
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".webm", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg"
    };

    private readonly FileLogger _log;
    private readonly object _engine;
    private readonly object _manager;
    private readonly List<object> _nativeFiles;
    private readonly List<object> _httpStreams = [];
    private bool _started;
    private bool _disposed;

    public string TorrentPath { get; }
    public IReadOnlyList<TorrentMediaItem> MediaFiles { get; }

    private TorrentSession(string torrentPath, FileLogger log, object engine, object manager, List<object> nativeFiles, List<TorrentMediaItem> mediaFiles)
    {
        TorrentPath = torrentPath;
        _log = log;
        _engine = engine;
        _manager = manager;
        _nativeFiles = nativeFiles;
        MediaFiles = mediaFiles;
    }

    public static async Task<TorrentSession> OpenAsync(string torrentPath, string saveDirectory, FileLogger log, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(torrentPath)) throw new ArgumentException("Не указан torrent-файл", nameof(torrentPath));
        torrentPath = Path.GetFullPath(torrentPath);
        if (!File.Exists(torrentPath)) throw new FileNotFoundException("Torrent-файл не найден", torrentPath);
        Directory.CreateDirectory(saveDirectory);

        var assembly = Assembly.Load("MonoTorrent");
        var engineType = assembly.GetType("MonoTorrent.Client.ClientEngine", throwOnError: true)!;
        var engine = Activator.CreateInstance(engineType) ?? throw new InvalidOperationException("Не удалось создать MonoTorrent ClientEngine");

        try
        {
            var manager = await InvokeAwaitableAsync(engine, "AddStreamingAsync", cancellationToken, torrentPath, saveDirectory)
                ?? throw new InvalidOperationException("MonoTorrent AddStreamingAsync вернул null");

            var filesValue = GetProperty(manager, "Files") as IEnumerable
                ?? throw new InvalidOperationException("MonoTorrent не предоставил список файлов торрента");

            var nativeAll = filesValue.Cast<object>().ToList();
            var candidates = new List<(object Native, int NativeIndex, string Path, long Length)>();
            for (var i = 0; i < nativeAll.Count; i++)
            {
                var native = nativeAll[i];
                var path = Convert.ToString(GetProperty(native, "Path")) ?? $"Файл {i + 1}";
                var length = ConvertToInt64(GetProperty(native, "Length"));
                candidates.Add((native, i, path, length));
            }

            var mediaCandidates = candidates.Where(x => VideoExtensions.Contains(Path.GetExtension(x.Path))).ToList();
            if (mediaCandidates.Count == 0) mediaCandidates = candidates;

            var nativeMedia = mediaCandidates.Select(x => x.Native).ToList();
            var media = mediaCandidates.Select((x, mediaIndex) => new TorrentMediaItem(
                mediaIndex,
                x.Path,
                Path.GetFileName(x.Path),
                x.Length)).ToList();

            log.Info($"torrent opened: {torrentPath}; files={candidates.Count}; media={media.Count}");
            return new TorrentSession(torrentPath, log, engine, manager, nativeMedia, media);
        }
        catch
        {
            await DisposeObjectAsync(engine);
            throw;
        }
    }

    public async Task<string> OpenMediaAsync(int mediaIndex, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mediaIndex < 0 || mediaIndex >= _nativeFiles.Count) throw new ArgumentOutOfRangeException(nameof(mediaIndex));

        if (!_started)
        {
            await InvokeAwaitableAsync(_manager, "StartAsync", cancellationToken);
            _started = true;
        }

        var provider = GetProperty(_manager, "StreamProvider")
            ?? throw new InvalidOperationException("MonoTorrent StreamProvider недоступен");
        var stream = await InvokeAwaitableAsync(provider, "CreateHttpStreamAsync", cancellationToken, _nativeFiles[mediaIndex])
            ?? throw new InvalidOperationException("MonoTorrent не смог создать HTTP stream");

        var uriValue = GetProperty(stream, "Uri") ?? GetProperty(stream, "URL") ?? GetProperty(stream, "Url");
        var uri = uriValue switch
        {
            Uri u => u.ToString(),
            string s when !string.IsNullOrWhiteSpace(s) => s,
            _ => throw new InvalidOperationException("MonoTorrent HTTP stream не вернул URI")
        };

        // Keep previously created HTTP streams alive until the torrent session ends.
        // mpv may still be reading the old stream for a few milliseconds while a new
        // episode is being loaded, so disposing it here creates a needless switch race.
        _httpStreams.Add(stream);
        _log.Info($"torrent media stream ready: index={mediaIndex}; uri={uri}");
        return uri;
    }

    private static object? GetProperty(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

    private static long ConvertToInt64(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt64(value); }
        catch { return 0; }
    }

    private static async Task<object?> InvokeAwaitableAsync(object target, string methodName, CancellationToken cancellationToken, params object?[] supplied)
    {
        var methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == methodName)
            .OrderBy(m => m.GetParameters().Length)
            .ToArray();

        foreach (var method in methods)
        {
            if (!TryBuildArguments(method.GetParameters(), supplied, cancellationToken, out var args)) continue;
            var result = method.Invoke(target, args);
            return await AwaitObjectAsync(result).ConfigureAwait(false);
        }

        throw new MissingMethodException(target.GetType().FullName, methodName);
    }

    private static bool TryBuildArguments(ParameterInfo[] parameters, object?[] supplied, CancellationToken cancellationToken, out object?[] args)
    {
        args = new object?[parameters.Length];
        if (supplied.Length > parameters.Length) return false;

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (i < supplied.Length)
            {
                var value = supplied[i];
                if (value is not null && !p.ParameterType.IsInstanceOfType(value)) return false;
                args[i] = value;
                continue;
            }

            if (p.ParameterType == typeof(CancellationToken)) args[i] = cancellationToken;
            else if (p.HasDefaultValue) args[i] = p.DefaultValue;
            else if (!p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) is not null) args[i] = null;
            else return false;
        }
        return true;
    }

    private static async Task<object?> AwaitObjectAsync(object? value)
    {
        if (value is null) return null;
        if (value is Task task)
        {
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        var type = value.GetType();
        var getAwaiter = type.GetMethod("GetAwaiter", Type.EmptyTypes);
        if (getAwaiter is null) return value;
        var awaiter = getAwaiter.Invoke(value, null) ?? throw new InvalidOperationException($"{type.Name}.GetAwaiter() returned null");
        var awaiterType = awaiter.GetType();
        var isCompleted = awaiterType.GetProperty("IsCompleted")
            ?? throw new InvalidOperationException($"{awaiterType.Name} has no IsCompleted");
        if (!(bool)(isCompleted.GetValue(awaiter) ?? false))
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var continuation = (Action)(() => tcs.TrySetResult());
            var onCompleted = awaiterType.GetMethod("UnsafeOnCompleted", [typeof(Action)])
                ?? awaiterType.GetMethod("OnCompleted", [typeof(Action)])
                ?? throw new InvalidOperationException($"{awaiterType.Name} has no OnCompleted");
            onCompleted.Invoke(awaiter, [continuation]);
            await tcs.Task.ConfigureAwait(false);
        }
        return awaiterType.GetMethod("GetResult", Type.EmptyTypes)?.Invoke(awaiter, null);
    }

    private static async ValueTask DisposeObjectAsync(object? value)
    {
        if (value is null) return;
        try
        {
            if (value is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (value is IDisposable disposable) disposable.Dispose();
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var stream in _httpStreams)
            await DisposeObjectAsync(stream);
        _httpStreams.Clear();
        try { if (_started) await InvokeAwaitableAsync(_manager, "StopAsync", CancellationToken.None); } catch { }
        await DisposeObjectAsync(_manager);
        await DisposeObjectAsync(_engine);
    }
}
