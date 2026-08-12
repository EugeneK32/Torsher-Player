using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using TorsherPlayer.Infrastructure;

namespace TorsherPlayer.Player;

/// <summary>
/// Avalonia OpenGL surface used only for libmpv rendering. Playback commands never
/// execute here; this control owns only mpv_render_* calls while Avalonia's GL context
/// is current.
/// </summary>
public sealed class MpvVideoView : OpenGlControlBase
{
    private MpvEngine? _engine;
    private FileLogger? _log;
    private int _framePending;
    private TaskCompletionSource<bool>? _releaseTcs;
    private bool _releaseRequested;

    public event Action? RendererReady;
    public event Action<string>? RenderError;

    public void Attach(MpvEngine engine, FileLogger log)
    {
        _engine = engine;
        _log = log;
        _releaseRequested = false;
        _releaseTcs = null;
        QueueFrame();
    }

    public void Detach() => _engine = null;

    public Task ReleaseRendererAsync()
    {
        if (_engine?.RendererReady != true) return Task.CompletedTask;
        if (_releaseTcs is { Task.IsCompleted: false }) return _releaseTcs.Task;
        _releaseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _releaseRequested = true;
        QueueFrame();
        return _releaseTcs.Task;
    }

    public void QueueFrame()
    {
        if (Interlocked.Exchange(ref _framePending, 1) != 0) return;
        Dispatcher.Post(() =>
        {
            if (!IsLoaded)
            {
                Interlocked.Exchange(ref _framePending, 0);
                return;
            }
            RequestNextFrameRendering();
        }, DispatcherPriority.Render);
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        QueueFrame();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        Interlocked.Exchange(ref _framePending, 0);
        try
        {
            var engine = _engine;
            if (engine is null) return;

            if (_releaseRequested)
            {
                if (engine.RendererReady) engine.ReleaseRenderer();
                _releaseRequested = false;
                _releaseTcs?.TrySetResult(true);
                return;
            }

            if (!engine.RendererReady)
            {
                engine.InitializeRenderer(gl.GetProcAddress, QueueFrame);
                RendererReady?.Invoke();
            }

            var top = TopLevel.GetTopLevel(this);
            var scale = top?.RenderScaling ?? 1.0;
            var width = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
            engine.Render(fb, width, height);
        }
        catch (Exception ex)
        {
            _log?.Exception(ex, "Avalonia OpenGL render");
            RenderError?.Invoke(ex.Message);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        try
        {
            if (_engine?.RendererReady == true) _engine.ReleaseRenderer();
            _releaseTcs?.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _log?.Exception(ex, "Avalonia OpenGL deinit");
            _releaseTcs?.TrySetException(ex);
        }
        finally
        {
            _releaseRequested = false;
            Interlocked.Exchange(ref _framePending, 0);
        }
        base.OnOpenGlDeinit(gl);
    }
}
