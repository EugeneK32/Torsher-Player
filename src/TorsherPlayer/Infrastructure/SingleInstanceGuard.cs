namespace TorsherPlayer.Infrastructure;

/// <summary>
/// Cross-platform per-user single-instance guard. The operating system releases
/// the exclusive file handle automatically if the process crashes.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private FileStream? _stream;

    private SingleInstanceGuard(FileStream stream) => _stream = stream;

    public static bool TryAcquire(out SingleInstanceGuard? guard)
    {
        guard = null;
        AppPaths.Ensure();
        try
        {
            var stream = new FileStream(
                AppPaths.InstanceLock,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128,
                FileOptions.None);

            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, bufferSize: 128, leaveOpen: true))
            {
                writer.Write($"pid={Environment.ProcessId}\nstarted={DateTimeOffset.UtcNow:O}\n");
                writer.Flush();
            }
            stream.Position = 0;
            guard = new SingleInstanceGuard(stream);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Do not make the player unusable if a broken permission on the lock
            // file prevents the safety feature from being established.
            return true;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}
