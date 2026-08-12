using System.IO;
using System.Text;

namespace TorsherPlayer.Infrastructure;

public sealed class FileLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    public FileLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true };
        var version = typeof(FileLogger).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        Info($"--- start {DateTimeOffset.Now:O} pid={Environment.ProcessId} version={version} ---");
    }
    public void Info(string message) => Write("INF", message);
    public void Warn(string message) => Write("WRN", message);
    public void Error(string message) => Write("ERR", message);
    public void Exception(Exception? ex, string context) => Write("ERR", ex is null ? context : $"{context}: {ex}");
    private void Write(string level, string text) { lock (_gate) _writer.WriteLine($"{DateTimeOffset.Now:O} [{level}] {text}"); }
    public void Dispose() { lock (_gate) _writer.Dispose(); }
}
