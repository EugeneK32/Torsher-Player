using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TorsherPlayer.Infrastructure;
using TorsherPlayer.Integration;

namespace TorsherPlayer;

public partial class App : Application
{
    private FileLogger? _log;
    private MainWindow? _window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppPaths.Ensure();
        _log = new FileLogger(AppPaths.AppLog);
        var log = _log;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => log.Exception(args.ExceptionObject as Exception, "Unhandled AppDomain exception");
        TaskScheduler.UnobservedTaskException += (_, args) => { _log?.Exception(args.Exception, "Unobserved task exception"); args.SetObserved(); };
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            _log?.Exception(args.Exception, "Avalonia dispatcher exception");
            args.Handled = true;
            _window?.ShowFatalError("Ошибка интерфейса", args.Exception.Message);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            LaunchOptions options;
            try { options = LaunchOptions.Parse(desktop.Args ?? []); }
            catch (Exception ex)
            {
                _log?.Exception(ex, "Invalid command line");
                options = LaunchOptions.Error(ex.Message);
            }

            _window = new MainWindow(options, log);
            desktop.MainWindow = _window;
            desktop.Exit += (_, _) =>
            {
                try { _log?.Info("application exit"); } catch { }
                _log?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
