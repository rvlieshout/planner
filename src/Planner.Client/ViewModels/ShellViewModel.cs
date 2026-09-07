using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Client.Services;

namespace Planner.Client.ViewModels;

/// <summary>The window's outer frame: an update banner that is always present, and beneath it either
/// the sign-in screen or the workspace.
///
/// The ordering in <see cref="StartAsync"/> is deliberate. Updates start first and never wait on
/// authentication, so a release that broke sign-in can still be replaced by one that fixes it.</summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly AuthService _auth;
    private readonly UpdateService _updates;
    private readonly ILogger<ShellViewModel> _logger;


    public ShellViewModel(
        IServiceProvider services,
        AuthService auth,
        UpdateService updates,
        UpdateBannerViewModel updateBanner,
        ILogger<ShellViewModel> logger)
    {
        _services = services;
        _auth = auth;
        _updates = updates;
        _logger = logger;

        Update = updateBanner;
        _auth.StateChanged += OnAuthStateChanged;
    }

    public UpdateBannerViewModel Update { get; }

    [ObservableProperty]
    public partial ViewModelBase? Content { get; set; }

    /// <summary>The signed-in workspace, or null on the sign-in screen. The window's menu bar and
    /// status bar are part of the frame rather than of the workspace view, so they reach the commands
    /// through here — and disable themselves when it is null.</summary>
    [ObservableProperty]
    public partial WorkspaceViewModel? Workspace { get; set; }

    [ObservableProperty]
    public partial bool IsStarting { get; set; } = true;

    /// <summary>The menu bar lives in the title bar and starts folded away, which is where the window
    /// gets its height back. The hamburger — sitting where the app icon would be — and F10 both
    /// unfold it; while it is folded that space carries the quick actions instead.</summary>
    [ObservableProperty]
    public partial bool IsMenuVisible { get; set; }

    public Avalonia.Media.Geometry? MenuIcon => Controls.AppIcons.Menu;

    public Avalonia.Media.Geometry? PlusIcon => Controls.AppIcons.Plus;

    public Avalonia.Media.Geometry? RefreshIcon => Controls.AppIcons.Refresh;

    public Avalonia.Media.Geometry? MinimiseIcon => Controls.AppIcons.WindowMinimise;

    public Avalonia.Media.Geometry? MaximiseIcon => Controls.AppIcons.WindowMaximise;

    public Avalonia.Media.Geometry? RestoreIcon => Controls.AppIcons.WindowRestore;

    public Avalonia.Media.Geometry? CloseIcon => Controls.AppIcons.Close;

    [RelayCommand]
    private void ToggleMenu() => IsMenuVisible = !IsMenuVisible;

    /// <summary>Shown in the status bar and the About dialog; the version someone reads back to you
    /// when reporting a bug.</summary>
    public string AppVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? "1.0.0";

    [RelayCommand]
    private static void Exit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // 1. Updates, unconditionally and before anything that can fail.
        _updates.Start();

        // 2. Wire up token refresh, then try to resume a saved session.
        _auth.Attach();

        try
        {
            await _auth.TryRestoreAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session restore failed");
        }

        IsStarting = false;
        await ShowCurrentAsync(ct);
    }

    private void OnAuthStateChanged() => _ = ShowCurrentAsync(CancellationToken.None);

    private async Task ShowCurrentAsync(CancellationToken ct)
    {
        if (_auth.IsSignedIn)
        {
            if (Content is WorkspaceViewModel)
            {
                return;
            }

            var workspace = _services.GetRequiredService<WorkspaceViewModel>();
            Workspace = workspace;
            Content = workspace;

            await workspace.InitialiseAsync(ct);
            return;
        }

        if (Workspace is not null)
        {
            await Workspace.DisposeAsync();
            Workspace = null;
        }

        Content = _services.GetRequiredService<LoginViewModel>();
    }
}
