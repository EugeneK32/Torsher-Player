using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TorsherPlayer.Infrastructure;

internal sealed class PowerKeepAlive : IDisposable
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState state);

    private readonly FileLogger _log;
    private Process? _inhibitor;
    private bool _windowsActive;

    public PowerKeepAlive(FileLogger log)
    {
        _log = log;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _windowsActive = SetThreadExecutionState(
                    ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired) != 0;
                if (!_windowsActive) _log.Warn("SetThreadExecutionState failed");
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                _inhibitor = StartInhibitor("caffeinate", $"-d -i -s -w {Environment.ProcessId}");
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                _inhibitor = StartInhibitor(
                    "systemd-inhibit",
                    "--what=idle:sleep --who=TorsherPlayer --why=Video-playback --mode=block sleep infinity");
            }
        }
        catch (Exception ex)
        {
            // Playback must never fail just because the OS has no supported inhibit API.
            _log.Exception(ex, "PowerKeepAlive start");
        }
    }

    private Process? StartInhibitor(string fileName, string arguments)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is not null) _log.Info($"sleep inhibitor started: {fileName}");
            return process;
        }
        catch (Exception ex)
        {
            _log.Exception(ex, $"sleep inhibitor unavailable: {fileName}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_windowsActive)
        {
            try { SetThreadExecutionState(ExecutionState.Continuous); }
            catch (Exception ex) { _log.Exception(ex, "PowerKeepAlive stop"); }
            _windowsActive = false;
        }

        var inhibitor = Interlocked.Exchange(ref _inhibitor, null);
        if (inhibitor is null) return;
        try
        {
            if (!inhibitor.HasExited) inhibitor.Kill(entireProcessTree: true);
            inhibitor.Dispose();
        }
        catch (Exception ex) { _log.Exception(ex, "sleep inhibitor stop"); }
    }
}
