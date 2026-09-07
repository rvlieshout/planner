using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Theme;
using AtomUI.Theme.Algorithms;
using AtomUI.Theme.Configuration;
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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // AtomUI is the app's control library and its design system. Everything the window draws —
        // metrics, colours, both theme variants — is compiled from the tokens configured here, so
        // this method is the one place the client's visual language is decided.
        this.UseAtomUI(builder =>
        {
            builder.UseDesktopControls();

            // Segoe on Windows, Inter as the packaged fallback. AtomUI would otherwise ask for its own
            // font stack, which is not the one the OS dresses its windows in.
            builder.WithDefaultFontFamily(AppFontFamily);

            // Follows the OS. A machine set to dark mode gets the dark palette on the first frame,
            // and flipping the system setting swaps the running app over without a restart.
            builder.WithFollowSystemThemes(
                new ThemeRequest(IThemeManager.DEFAULT_THEME_ID, Palette(ThemeAlgorithm.Default), ThemeTransitionReason.FollowSystem),
                new ThemeRequest(IThemeManager.DEFAULT_THEME_ID, Palette(ThemeAlgorithm.Dark), ThemeTransitionReason.FollowSystem));
        });
    }

    private const string AppFontFamily = "Segoe UI Variable Text, Segoe UI, Inter";

    /// <summary>The client's design language, expressed as overrides on Ant Design's seed tokens.
    ///
    /// Ant Design's stock metrics are sized for a web page: 32px controls, 6px corners, 14px text. On a
    /// mouse-driven window that reads as a browser rendered inside a frame. The Compact algorithm plus
    /// these three seeds pull the whole system back to the proportions of a native tool — and because
    /// they are seeds, every derived token, every control and both variants follow from them instead of
    /// being restyled one at a time.</summary>
    private static ThemeConfig Palette(ThemeAlgorithm appearance) =>
        new ThemeConfigBuilder()
            .WithAlgorithms(appearance, ThemeAlgorithm.Compact)
            // The app's indigo, not the Windows accent: the same colour in both variants, which is what
            // makes the client recognisably itself rather than a mirror of someone's personalisation.
            .WithToken("ColorPrimary", appearance is ThemeAlgorithm.Dark ? "#6E79F1" : "#5E6AD2")
            .WithToken("FontSize", "12")
            .WithToken("BorderRadius", "3")
            .Build();

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
