using AtomUI;
using Avalonia;
using Microsoft.Extensions.Logging;
using Planner.Client.Infrastructure;
using Velopack;

namespace Planner.Client;

internal sealed class Program
{
    // Avalonia must not be touched before AppMain runs; nothing here does.
    [STAThread]
    public static void Main(string[] args)
    {
        AppPaths.EnsureCreated();

        // The provider is created here and handed to App so Velopack's hooks and the application share
        // one log file, in the order things actually happened.
        var loggerProvider = new FileLoggerProvider(LogLevel.Information);
        var velopackLogger = new VelopackLoggerAdapter(loggerProvider.CreateLogger("Velopack"));
        var startupLogger = loggerProvider.CreateLogger("Startup");

        // MUST be the first thing that runs.
        //
        // On an update, Velopack relaunches the app with hook arguments (--veloapp-install and friends).
        // This call handles them and exits the process. Any work done before it — reading settings,
        // opening a window, phoning the server — happens once per hook invocation for no reason, and a
        // crash in that work would break installing and updating rather than just running.
        VelopackApp.Build()
            .SetArgs(args)
            .SetLogger(velopackLogger)
            .OnFirstRun(version => startupLogger.LogInformation("First run after installing {Version}", version))
            .OnRestarted(version => startupLogger.LogInformation("Restarted into {Version}", version))
            .Run();

        startupLogger.LogInformation("Planner client starting");

        try
        {
            BuildAvaloniaApp(loggerProvider).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // A crash before the window appears leaves nothing on screen; at least leave it on disk.
            startupLogger.LogCritical(ex, "The client terminated unexpectedly");
            throw;
        }
        finally
        {
            loggerProvider.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp(FileLoggerProvider loggerProvider) =>
        AppBuilder.Configure(() => new App(loggerProvider))
            // AtomUI's own platform detection. It is `UsePlatformDetect` plus the windowing backends
            // AtomUI draws its window chrome through; the stock call leaves those unregistered and the
            // title bar falls back to the system caption.
            .UseAtomUIPlatformDetect()
            // Registers the render/motion defaults AtomUI's control themes are written against.
            .WithAtomUIDefaultOptions()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    /// <summary>Used by the XAML previewer, which constructs the app without going through Main.</summary>
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(new FileLoggerProvider());
}
