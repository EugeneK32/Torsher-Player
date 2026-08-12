namespace TorsherPlayer.Input;

public interface IGamepadService : IDisposable
{
    event Action<GamepadAction>? Action;
    event Action<GamepadAction>? ActionReleased;
}
