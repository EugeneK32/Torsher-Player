using System.Runtime.InteropServices;
using TorsherPlayer.Infrastructure;

namespace TorsherPlayer.Input;

public sealed class SdlGamepadService : IGamepadService
{
    [Flags]
    private enum Btn : ushort
    {
        None = 0,
        Up = 1 << 0,
        Down = 1 << 1,
        Left = 1 << 2,
        Right = 1 << 3,
        Menu = 1 << 4,
        View = 1 << 5,
        LB = 1 << 6,
        RB = 1 << 7,
        A = 1 << 8,
        B = 1 << 9,
        X = 1 << 10,
        Y = 1 << 11
    }

    private readonly FileLogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private IntPtr _gamepad;
    private Btn _last;
    private Btn _repeatButton;
    private DateTime _nextRepeat;
    private bool _sdlInitialized;

    public event Action<GamepadAction>? Action;
    public event Action<GamepadAction>? ActionReleased;

    private SdlGamepadService(FileLogger log)
    {
        _log = log;

        // SDL requires subsystem initialization on the main thread. MainWindow creates
        // this service on Avalonia's UI thread; polling itself then happens off-thread.
        if (!SdlNative.SDL_InitSubSystem(SdlNative.InitGamepad))
            throw new InvalidOperationException($"SDL gamepad init failed: {SdlNative.Error}");

        _sdlInitialized = true;
        _log.Info("SDL3 gamepad subsystem initialized");
        try
        {
            _thread = new Thread(Loop)
            {
                Name = "sdl3-gamepad",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }
        catch
        {
            SdlNative.SDL_QuitSubSystem(SdlNative.InitGamepad);
            _sdlInitialized = false;
            throw;
        }
    }

    public static IGamepadService CreateOrNull(FileLogger log)
    {
        try
        {
            return new SdlGamepadService(log);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            log.Exception(ex, "SDL3 gamepad unavailable; continuing without gamepad input");
            return new NullGamepadService();
        }
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                SdlNative.SDL_UpdateGamepads();
                EnsureGamepad();
                if (_gamepad == IntPtr.Zero)
                {
                    ResetState();
                    Thread.Sleep(300);
                    continue;
                }

                if (!SdlNative.SDL_GamepadConnected(_gamepad))
                {
                    _log.Info("gamepad disconnected");
                    CloseCurrentGamepad();
                    ResetState();
                    Thread.Sleep(100);
                    continue;
                }

                PollCurrentGamepad();
            }
            catch (Exception ex)
            {
                _log.Exception(ex, "SDL3 gamepad loop");
                Thread.Sleep(250);
            }

            Thread.Sleep(12);
        }
    }

    private void EnsureGamepad()
    {
        if (_gamepad != IntPtr.Zero) return;

        var list = SdlNative.SDL_GetGamepads(out var count);
        if (list == IntPtr.Zero || count <= 0) return;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var id = unchecked((uint)Marshal.ReadInt32(list, i * sizeof(uint)));
                var opened = SdlNative.SDL_OpenGamepad(id);
                if (opened == IntPtr.Zero) continue;
                _gamepad = opened;
                _log.Info($"gamepad connected: {SdlNative.Name(opened)}");
                return;
            }
        }
        finally
        {
            SdlNative.SDL_free(list);
        }
    }

    private void PollCurrentGamepad()
    {
        var current = Btn.None;
        if (Pressed(SdlNative.GamepadButton.South)) current |= Btn.A;
        if (Pressed(SdlNative.GamepadButton.East)) current |= Btn.B;
        if (Pressed(SdlNative.GamepadButton.West)) current |= Btn.X;
        if (Pressed(SdlNative.GamepadButton.North)) current |= Btn.Y;
        if (Pressed(SdlNative.GamepadButton.Start)) current |= Btn.Menu;
        if (Pressed(SdlNative.GamepadButton.Back)) current |= Btn.View;
        if (Pressed(SdlNative.GamepadButton.LeftShoulder)) current |= Btn.LB;
        if (Pressed(SdlNative.GamepadButton.RightShoulder)) current |= Btn.RB;
        if (Pressed(SdlNative.GamepadButton.DpadUp)) current |= Btn.Up;
        if (Pressed(SdlNative.GamepadButton.DpadDown)) current |= Btn.Down;
        if (Pressed(SdlNative.GamepadButton.DpadLeft)) current |= Btn.Left;
        if (Pressed(SdlNative.GamepadButton.DpadRight)) current |= Btn.Right;

        const short dead = 17000;
        var lx = SdlNative.SDL_GetGamepadAxis(_gamepad, SdlNative.GamepadAxis.LeftX);
        var ly = SdlNative.SDL_GetGamepadAxis(_gamepad, SdlNative.GamepadAxis.LeftY);
        if (lx <= -dead) current |= Btn.Left;
        else if (lx >= dead) current |= Btn.Right;
        // SDL axes use negative for up and positive for down.
        if (ly <= -dead) current |= Btn.Up;
        else if (ly >= dead) current |= Btn.Down;

        // Releases are emitted before new presses. This matters when the user
        // changes seek direction in a single polling frame: the old scrub is
        // committed before the new one starts.
        var released = _last & ~current;
        foreach (var pair in Map)
            if ((released & pair.Button) != 0) ActionReleased?.Invoke(pair.Action);

        var pressed = current & ~_last;
        foreach (var pair in Map)
            if ((pressed & pair.Button) != 0) Action?.Invoke(pair.Action);

        HandleDirectionalRepeat(current);
        _last = current;
    }

    private bool Pressed(SdlNative.GamepadButton button) => SdlNative.SDL_GetGamepadButton(_gamepad, button);

    private void HandleDirectionalRepeat(Btn current)
    {
        var repeat = current & (Btn.Up | Btn.Down | Btn.Left | Btn.Right);
        if (repeat == Btn.None)
        {
            _repeatButton = Btn.None;
            return;
        }

        var now = DateTime.UtcNow;
        var first = FirstRepeat(repeat);
        if (_repeatButton != first)
        {
            _repeatButton = first;
            _nextRepeat = now.AddMilliseconds(420);
            return;
        }

        if (now < _nextRepeat) return;
        var action = Map.First(x => x.Button == first).Action;
        Action?.Invoke(action);
        _nextRepeat = now.AddMilliseconds(110);
    }

    private static Btn FirstRepeat(Btn buttons)
    {
        foreach (var button in new[] { Btn.Up, Btn.Down, Btn.Left, Btn.Right })
            if ((buttons & button) != 0) return button;
        return Btn.None;
    }

    private void ResetState()
    {
        // A disconnect while a direction is held must look like a release to the
        // UI; otherwise a seek-preview could remain active forever.
        foreach (var pair in Map)
            if ((_last & pair.Button) != 0) ActionReleased?.Invoke(pair.Action);

        _last = Btn.None;
        _repeatButton = Btn.None;
    }

    private void CloseCurrentGamepad()
    {
        var gamepad = Interlocked.Exchange(ref _gamepad, IntPtr.Zero);
        if (gamepad != IntPtr.Zero) SdlNative.SDL_CloseGamepad(gamepad);
    }

    private static readonly (Btn Button, GamepadAction Action)[] Map =
    [
        (Btn.A, GamepadAction.A), (Btn.B, GamepadAction.B), (Btn.X, GamepadAction.X), (Btn.Y, GamepadAction.Y),
        (Btn.Menu, GamepadAction.Menu), (Btn.View, GamepadAction.View),
        (Btn.Up, GamepadAction.Up), (Btn.Down, GamepadAction.Down),
        (Btn.Left, GamepadAction.Left), (Btn.Right, GamepadAction.Right),
        (Btn.LB, GamepadAction.LeftBumper), (Btn.RB, GamepadAction.RightBumper)
    ];

    public void Dispose()
    {
        _cts.Cancel();
        try { if (_thread.IsAlive) _thread.Join(500); } catch { }
        CloseCurrentGamepad();
        _cts.Dispose();

        if (_sdlInitialized)
        {
            try { SdlNative.SDL_QuitSubSystem(SdlNative.InitGamepad); }
            catch (Exception ex) { _log.Exception(ex, "SDL3 gamepad shutdown"); }
            _sdlInitialized = false;
        }
    }
}
