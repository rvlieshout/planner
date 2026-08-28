using CommunityToolkit.Mvvm.ComponentModel;
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

    private WorkspaceViewModel? _workspace;

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

    [ObservableProperty]
    public partial bool IsStarting { get; set; } = true;

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
            _workspace = workspace;
            Content = workspace;

            await workspace.InitialiseAsync(ct);
            return;
        }

        if (_workspace is not null)
        {
            await _workspace.DisposeAsync();
            _workspace = null;
        }

        Content = _services.GetRequiredService<LoginViewModel>();
    }
}
