using Avalonia;
using TorsherPlayer.Infrastructure;

namespace TorsherPlayer;

internal static class Program
{
    private static SingleInstanceGuard? _instanceGuard;

    [STAThread]
    public static void Main(string[] args)
    {
        AppPaths.Ensure();
        if (!SingleInstanceGuard.TryAcquire(out _instanceGuard))
            return;

        try
        {
            NativeDependencyResolver.Register();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _instanceGuard?.Dispose();
            _instanceGuard = null;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
