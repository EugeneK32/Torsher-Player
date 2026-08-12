namespace TorsherPlayer.Input;

internal sealed class NullGamepadService : IGamepadService
{
    public event Action<GamepadAction>? Action { add { } remove { } }
    public event Action<GamepadAction>? ActionReleased { add { } remove { } }
    public void Dispose() { }
}
