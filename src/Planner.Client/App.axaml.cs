using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Client.Infrastructure;
using Planner.Client.Services;
using Planner.Client.ViewModels;
using Planner.Client.Views;

namespace Planner.Client;

public partial class App : Application
{
    private readonly FileLoggerProvider _loggerProvider;
    private ServiceProvider? _services;

    public App() : this(new FileLoggerProvider())
    {
    }

    public App(FileLoggerProvider loggerProvider) => _loggerProvider = loggerProvider;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = BuildServices();

            var shell = _services.GetRequiredService<ShellViewModel>();

            desktop.MainWindow = new MainWindow { DataContext = shell };
            desktop.ShutdownRequested += (_, _) => _services?.Dispose();

            // Kick off startup work without blocking the first frame.
            _ = shell.StartAsync(CancellationToken.None);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddProvider(_loggerProvider);
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton<SettingsStore>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<RealtimeService>();

        // One HttpClient and one PlannerApiClient for the whole app. It has to be a singleton: the
        // access token lives on it, so a transient registration would hand the workspace a client that
        // has never been signed in. PooledConnectionLifetime keeps a long-lived client honest about DNS.
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        });

        services.AddSingleton<PlannerApiClient>();

        services.AddSingleton<UpdateBannerViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<WorkspaceViewModel>();

        return services.BuildServiceProvider();
    }
}
