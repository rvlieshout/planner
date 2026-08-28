using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Planner.Client.Services;

namespace Planner.Client.ViewModels;

/// <summary>The strip along the top of the window that reports the update state.
///
/// It is part of the shell rather than any one screen, so it is visible on the sign-in screen too.
/// That is the whole point: if a release broke sign-in, the person who most needs to know an update
/// exists is the person who cannot get past the login box.</summary>
public sealed partial class UpdateBannerViewModel : ViewModelBase
{
    private readonly UpdateService _updates;

    public UpdateBannerViewModel(UpdateService updates)
    {
        _updates = updates;
        _updates.StatusChanged += OnStatusChanged;
        Apply(updates.Status);
    }

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    public partial string Headline { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Detail { get; set; }

    [ObservableProperty]
    public partial bool CanRestart { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int PercentComplete { get; set; }

    [ObservableProperty]
    public partial bool IsProblem { get; set; }

    public string CurrentVersion => _updates.CurrentVersion;

    /// <summary>True once an update is staged. The sign-in screen reads this to suggest installing it
    /// when a sign-in attempt fails.</summary>
    public bool IsUpdateReady => _updates.Status.IsUpdateReady;

    [RelayCommand]
    private void RestartAndInstall()
    {
        IsBusy = true;
        Headline = "Installing the update…";

        // Succeeds by never returning: the process is replaced.
        if (!_updates.ApplyAndRestart())
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task CheckNow() => _updates.CheckAsync();

    private void OnStatusChanged(UpdateStatus status) => Dispatcher.UIThread.Post(() => Apply(status));

    private void Apply(UpdateStatus status)
    {
        PercentComplete = status.PercentComplete;
        CanRestart = status.State == UpdateState.ReadyToRestart;
        IsProblem = false;
        Detail = null;

        switch (status.State)
        {
            case UpdateState.ReadyToRestart:
                IsVisible = true;
                Headline = $"Version {status.AvailableVersion} is ready to install";
                Detail = "Restart to finish. Your work is saved on the server.";
                break;

            case UpdateState.Downloading:
                IsVisible = true;
                Headline = $"Downloading version {status.AvailableVersion}…";
                Detail = $"{status.PercentComplete}%";
                break;

            case UpdateState.Failed:
                // Quiet by design: a laptop off the network should not be nagged every four hours.
                IsVisible = false;
                IsProblem = true;
                Detail = status.Message;
                break;

            default:
                IsVisible = false;
                break;
        }

        OnPropertyChanged(nameof(IsUpdateReady));
    }
}
