using System.Runtime.InteropServices;
using System.Text;
using TorsherPlayer.Infrastructure;

namespace TorsherPlayer.Player;

internal static unsafe class MpvNative
{
    private const string Lib = NativeDependencyResolver.MpvLibrary;

    internal enum MpvFormat { None = 0, String = 1, OsdString = 2, Flag = 3, Int64 = 4, Double = 5, Node = 6, NodeArray = 7, NodeMap = 8, ByteArray = 9 }
    internal enum MpvEventId { None = 0, Shutdown = 1, LogMessage = 2, GetPropertyReply = 3, SetPropertyReply = 4, CommandReply = 5, StartFile = 6, EndFile = 7, FileLoaded = 8, ClientMessage = 16, VideoReconfig = 17, AudioReconfig = 18, Seek = 20, PlaybackRestart = 21, PropertyChange = 22, QueueOverflow = 24, Hook = 25 }
    internal enum MpvEndFileReason { Eof = 0, Stop = 2, Quit = 3, Error = 4, Redirect = 5 }
    internal enum MpvRenderParamType { Invalid = 0, ApiType = 1, OpenGlInitParams = 2, OpenGlFbo = 3, FlipY = 4, Depth = 5, IccProfile = 6, AmbientLight = 7, X11Display = 8, WaylandDisplay = 9, AdvancedControl = 10, NextFrameInfo = 11, BlockForTargetTime = 12, SkipRendering = 13, DrmDisplay = 14, DrmDrawSurfaceSize = 15 }

    [StructLayout(LayoutKind.Sequential)] internal struct MpvEvent { public MpvEventId EventId; public int Error; public ulong ReplyUserdata; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] internal struct MpvEventProperty { public IntPtr Name; public MpvFormat Format; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] internal struct MpvEventLogMessage { public IntPtr Prefix; public IntPtr Level; public IntPtr Text; public int LogLevel; }
    [StructLayout(LayoutKind.Sequential)] internal struct MpvEventEndFile { public MpvEndFileReason Reason; public int Error; }
    [StructLayout(LayoutKind.Sequential)] internal struct MpvRenderParam { public MpvRenderParamType Type; public IntPtr Data; public MpvRenderParam(MpvRenderParamType type, IntPtr data) { Type = type; Data = data; } }
    [StructLayout(LayoutKind.Sequential)] internal struct MpvOpenGlInitParams { public IntPtr GetProcAddress; public IntPtr GetProcAddressCtx; }
    [StructLayout(LayoutKind.Sequential)] internal struct MpvOpenGlFbo { public int Fbo; public int W; public int H; public int InternalFormat; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr GetProcAddressDelegate(IntPtr ctx, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void RenderUpdateDelegate(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mpv_create();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_set_option_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_request_log_messages(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string level);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, MpvFormat format);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mpv_get_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_free(IntPtr data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mpv_error_string(int error);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_command_async(IntPtr ctx, ulong replyUserdata, IntPtr args);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_render_context_create(out IntPtr renderCtx, IntPtr mpv, IntPtr parameters);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_render_context_free(IntPtr renderCtx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_render_context_set_update_callback(IntPtr renderCtx, RenderUpdateDelegate? callback, IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong mpv_render_context_update(IntPtr renderCtx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_render_context_render(IntPtr renderCtx, IntPtr parameters);

    internal static string ErrorString(int error) => PtrToUtf8(mpv_error_string(error));
    internal static string PtrToUtf8(IntPtr ptr) => ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(ptr) ?? "";

    internal static string? GetString(IntPtr mpv, string property)
    {
        var ptr = mpv_get_property_string(mpv, property);
        if (ptr == IntPtr.Zero) return null;
        try { return PtrToUtf8(ptr); } finally { mpv_free(ptr); }
    }

    internal static int CommandAsync(IntPtr ctx, ulong userdata, params string[] args)
    {
        var strings = new IntPtr[args.Length];
        var argv = Marshal.AllocHGlobal((args.Length + 1) * IntPtr.Size);
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                strings[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
                Marshal.WriteIntPtr(argv, i * IntPtr.Size, strings[i]);
            }
            Marshal.WriteIntPtr(argv, args.Length * IntPtr.Size, IntPtr.Zero);
            return mpv_command_async(ctx, userdata, argv);
        }
        finally
        {
            foreach (var p in strings) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            Marshal.FreeHGlobal(argv);
        }
    }
}
