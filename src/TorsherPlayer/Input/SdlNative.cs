using System.Runtime.InteropServices;
using TorsherPlayer.Infrastructure;

namespace TorsherPlayer.Input;

internal static class SdlNative
{
    private const string Lib = NativeDependencyResolver.SdlLibrary;
    internal const uint InitGamepad = 0x00002000u;

    internal enum GamepadButton
    {
        Invalid = -1,
        South = 0,
        East,
        West,
        North,
        Back,
        Guide,
        Start,
        LeftStick,
        RightStick,
        LeftShoulder,
        RightShoulder,
        DpadUp,
        DpadDown,
        DpadLeft,
        DpadRight
    }

    internal enum GamepadAxis
    {
        Invalid = -1,
        LeftX = 0,
        LeftY,
        RightX,
        RightY,
        LeftTrigger,
        RightTrigger
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_InitSubSystem(uint flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_QuitSubSystem(uint flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_UpdateGamepads();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SDL_GetGamepads(out int count);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SDL_OpenGamepad(uint instanceId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_CloseGamepad(IntPtr gamepad);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_GamepadConnected(IntPtr gamepad);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_GetGamepadButton(IntPtr gamepad, GamepadButton button);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern short SDL_GetGamepadAxis(IntPtr gamepad, GamepadAxis axis);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SDL_GetGamepadName(IntPtr gamepad);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SDL_GetError();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_free(IntPtr memory);

    internal static string Error => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "unknown SDL error";
    internal static string Name(IntPtr gamepad) => Marshal.PtrToStringUTF8(SDL_GetGamepadName(gamepad)) ?? "gamepad";
}
