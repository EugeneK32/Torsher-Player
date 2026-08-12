using System.Runtime.InteropServices;
using LampaPlayer.Infrastructure;

namespace LampaPlayer.Input;

public sealed class XInputGamepadService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Packet; public Gamepad Pad; }
    [StructLayout(LayoutKind.Sequential)] private struct Gamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [Flags] private enum Btn : ushort { Up=0x0001, Down=0x0002, Left=0x0004, Right=0x0008, Menu=0x0010, View=0x0020, LB=0x0100, RB=0x0200, A=0x1000, B=0x2000, X=0x4000, Y=0x8000 }

    [DllImport("xinput1_4.dll", EntryPoint="XInputGetState")] private static extern uint Get14(uint user, out State state);
    [DllImport("xinput9_1_0.dll", EntryPoint="XInputGetState")] private static extern uint Get91(uint user, out State state);

    private readonly FileLogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private ushort _last;
    private DateTime _nextRepeat;
    private Btn _repeatButton;
    public event Action<GamepadAction>? Action;

    public XInputGamepadService(FileLogger log)
    {
        _log = log;
        _thread = new Thread(Loop) { Name = "xinput", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private static bool TryState(out State state)
    {
        for (uint i=0;i<4;i++)
        {
            try { if (Get14(i, out state) == 0) return true; } catch (DllNotFoundException) { break; }
        }
        for (uint i=0;i<4;i++)
        {
            try { if (Get91(i, out state) == 0) return true; } catch { break; }
        }
        state = default; return false;
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!TryState(out var s)) { _last = 0; Thread.Sleep(300); continue; }
                var now = DateTime.UtcNow;
                var current = (Btn)s.Pad.Buttons;
                // Treat the left stick as a D-pad as well. This keeps the TV UI natural on
                // controllers where the stick is the primary navigation control.
                const short dead = 17000;
                if (s.Pad.LX <= -dead) current |= Btn.Left;
                else if (s.Pad.LX >= dead) current |= Btn.Right;
                if (s.Pad.LY <= -dead) current |= Btn.Down;
                else if (s.Pad.LY >= dead) current |= Btn.Up;
                var pressed = (Btn)((ushort)current & ~_last);
                foreach (var pair in Map)
                    if ((pressed & pair.Button) != 0) Action?.Invoke(pair.Action);

                var repeat = current & (Btn.Up|Btn.Down|Btn.Left|Btn.Right);
                if (repeat != 0)
                {
                    var first = FirstRepeat(repeat);
                    if (_repeatButton != first) { _repeatButton = first; _nextRepeat = now.AddMilliseconds(420); }
                    else if (now >= _nextRepeat)
                    {
                        var a = Map.First(x => x.Button == first).Action;
                        Action?.Invoke(a); _nextRepeat = now.AddMilliseconds(110);
                    }
                }
                else _repeatButton = 0;
                _last = (ushort)current;
            }
            catch (Exception ex) { _log.Exception(ex, "XInput loop"); }
            Thread.Sleep(12);
        }
    }

    private static Btn FirstRepeat(Btn b)
    {
        foreach (var x in new[]{Btn.Up,Btn.Down,Btn.Left,Btn.Right}) if ((b & x) != 0) return x;
        return 0;
    }

    private static readonly (Btn Button, GamepadAction Action)[] Map = [
        (Btn.A,GamepadAction.A),(Btn.B,GamepadAction.B),(Btn.X,GamepadAction.X),(Btn.Y,GamepadAction.Y),
        (Btn.Menu,GamepadAction.Menu),(Btn.View,GamepadAction.View),(Btn.Up,GamepadAction.Up),(Btn.Down,GamepadAction.Down),
        (Btn.Left,GamepadAction.Left),(Btn.Right,GamepadAction.Right),(Btn.LB,GamepadAction.LeftBumper),(Btn.RB,GamepadAction.RightBumper)
    ];

    public void Dispose() { _cts.Cancel(); try { if (_thread.IsAlive) _thread.Join(300); } catch { } _cts.Dispose(); }
}
