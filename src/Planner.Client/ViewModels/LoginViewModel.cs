using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Planner.Client.Services;

namespace Planner.Client.ViewModels;

public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly AuthService _auth;
    private readonly SettingsStore _settings;
    private readonly UpdateBannerViewModel _update;

    public LoginViewModel(AuthService auth, SettingsStore settings, UpdateBannerViewModel update)
    {
        _auth = auth;
        _settings = settings;
        _update = update;

        var current = settings.Current;
        ServerUrl = current.ServerUrl;
        Email = current.LastEmail ?? string.Empty;

        _update.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ShowUpdateHint));
    }

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Shown when a sign-in attempt failed *and* an update is waiting. The failure may be the
    /// thing the update fixes, and the person cannot get to any in-app menu to find out.</summary>
    public bool ShowUpdateHint => Error is not null && _update.IsUpdateReady;

    [RelayCommand]
    private async Task SignInAsync(CancellationToken ct)
    {
        Error = null;

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            Error = "Enter your email address and password.";
            OnPropertyChanged(nameof(ShowUpdateHint));
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _auth.SignInAsync(ServerUrl.Trim(), Email.Trim(), Password, ct);

            if (!result.Success)
            {
                Error = result.Error;
                Password = string.Empty;
            }
            else
            {
                // Remember the server even when the account changes.
                var settings = _settings.Current;
                settings.ServerUrl = ServerUrl.Trim();
                _settings.Save(settings);
            }
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ShowUpdateHint));
        }
    }
}
