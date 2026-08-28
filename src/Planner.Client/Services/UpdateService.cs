using Microsoft.Extensions.Logging;
using Planner.Client.Infrastructure;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Planner.Client.Services;

public enum UpdateState
{
    /// <summary>Running from a development build or an unpacked folder, where Velopack has nothing to
    /// update. Not an error.</summary>
    Unsupported,

    /// <summary>Up to date as of the last check.</summary>
    UpToDate,

    Checking,

    Downloading,

    /// <summary>Downloaded and staged. One restart away.</summary>
    ReadyToRestart,

    /// <summary>The last check or download failed. Retried on the next tick.</summary>
    Failed
}

public sealed record UpdateStatus(
    UpdateState State,
    string CurrentVersion,
    string? AvailableVersion = null,
    string? Message = null,
    int PercentComplete = 0,
    DateTimeOffset? LastChecked = null)
{
    public bool IsUpdateReady => State is UpdateState.ReadyToRestart;
}

/// <summary>Keeps the client current.
///
/// Two rules drive the design:
///
/// 1. It runs from application start, on its own schedule, with no reference to whether anyone has
///    signed in. If a release broke sign-in, the update that fixes it must still be able to arrive —
///    so the feed is anonymous and this service never asks for a token.
/// 2. An available update is downloaded immediately and only *applied* when the user says so. Restarting
///    someone's app underneath them is never the right call; having the bytes already staged means
///    saying yes takes a second.</summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>Four hours, as specified. A desktop left running for a week still lands the fix the
    /// same day, and an on-prem feed sees six requests per client per day — nothing.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly SettingsStore _settings;
    private readonly ILogger<UpdateService> _logger;
    private readonly VelopackLoggerAdapter _velopackLogger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    private readonly IVelopackLocator? _locator;

    private UpdateManager? _manager;
    private string? _managerFeed;
    private UpdateInfo? _pending;
    private Task? _loop;

    /// <param name="locator">Overrides how Velopack discovers the installed app. Production leaves this
    /// null and gets the platform default; the update tests inject a locator pointed at a temporary
    /// install so the whole check-and-stage path can be exercised without installing anything.</param>
    public UpdateService(
        SettingsStore settings,
        ILogger<UpdateService> logger,
        IVelopackLocator? locator = null)
    {
        _settings = settings;
        _logger = logger;
        _locator = locator;
        _velopackLogger = new VelopackLoggerAdapter(logger);

        CurrentVersion = ResolveCurrentVersion();
        Status = new UpdateStatus(UpdateState.UpToDate, CurrentVersion);
    }

    public string CurrentVersion { get; }

    public UpdateStatus Status { get; private set; }

    /// <summary>Raised on a background thread. Subscribers marshal to the UI themselves.</summary>
    public event Action<UpdateStatus>? StatusChanged;

    /// <summary>Starts the check-at-startup-then-every-four-hours loop. Safe to call once.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _logger.LogInformation(
            "Update service starting. Version {Version}, feed {Feed}, interval {Interval}",
            CurrentVersion, _settings.Current.ResolveUpdateFeed(), CheckInterval);

        _loop = Task.Run(() => RunAsync(_stopping.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // An update downloaded in a previous session survives a restart; say so before checking again.
        if (TryGetManager()?.UpdatePendingRestart is { } staged)
        {
            Publish(Status with
            {
                State = UpdateState.ReadyToRestart,
                AvailableVersion = staged.Version?.ToString(),
                Message = "An update was downloaded earlier and is ready to install."
            });
        }

        await CheckAsync(ct);

        try
        {
            using var timer = new PeriodicTimer(CheckInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await CheckAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Runs a check now. Used by the loop and by the "Check for updates" action.</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);

        if (!await _checkGate.WaitAsync(TimeSpan.Zero, linked.Token))
        {
            return; // A check is already running; a second one would only duplicate the work.
        }

        try
        {
            await CheckCoreAsync(linked.Token);
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        var manager = TryGetManager();

        if (manager is null)
        {
            Publish(Status with
            {
                State = UpdateState.Unsupported,
                Message = "Updates are managed by your development environment in this build.",
                LastChecked = DateTimeOffset.Now
            });
            return;
        }

        // Already staged: re-checking would only find the same release again.
        if (Status.State == UpdateState.ReadyToRestart)
        {
            return;
        }

        Publish(Status with { State = UpdateState.Checking, Message = null });

        try
        {
            var update = await manager.CheckForUpdatesAsync();

            if (update is null)
            {
                _logger.LogInformation("No update available; {Version} is current", CurrentVersion);
                Publish(Status with
                {
                    State = UpdateState.UpToDate,
                    AvailableVersion = null,
                    Message = null,
                    LastChecked = DateTimeOffset.Now
                });
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            _logger.LogInformation("Update {Version} available; downloading", version);

            Publish(Status with
            {
                State = UpdateState.Downloading,
                AvailableVersion = version,
                PercentComplete = 0,
                Message = null
            });

            await manager.DownloadUpdatesAsync(
                update,
                percent => Publish(Status with
                {
                    State = UpdateState.Downloading,
                    AvailableVersion = version,
                    PercentComplete = percent
                }),
                ct);

            _pending = update;

            _logger.LogInformation("Update {Version} downloaded and staged", version);
            Publish(Status with
            {
                State = UpdateState.ReadyToRestart,
                AvailableVersion = version,
                PercentComplete = 100,
                Message = null,
                LastChecked = DateTimeOffset.Now
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A missing feed, a DNS failure or a laptop on a train are all routine. Log it, surface it
            // quietly, and try again on the next tick — never interrupt the user over it.
            _logger.LogWarning(ex, "Update check failed");

            Publish(Status with
            {
                State = UpdateState.Failed,
                Message = Describe(ex),
                LastChecked = DateTimeOffset.Now
            });
        }
    }

    /// <summary>Applies the staged update and restarts. Does not return when it succeeds.</summary>
    public bool ApplyAndRestart()
    {
        var manager = TryGetManager();
        var asset = _pending?.TargetFullRelease ?? manager?.UpdatePendingRestart;

        if (manager is null || asset is null)
        {
            _logger.LogWarning("Asked to apply an update, but nothing is staged");
            return false;
        }

        try
        {
            _logger.LogInformation("Applying update {Version} and restarting", asset.Version);
            manager.ApplyUpdatesAndRestart(asset);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not apply the update");
            Publish(Status with
            {
                State = UpdateState.Failed,
                Message = "The update could not be installed. See the log for details."
            });
            return false;
        }
    }

    /// <summary>Builds an <see cref="UpdateManager"/> for the configured feed, rebuilding it when the
    /// user points the client at a different server. Returns null when this build cannot self-update.</summary>
    private UpdateManager? TryGetManager()
    {
        var feed = _settings.Current.ResolveUpdateFeed();

        if (_manager is not null && _managerFeed == feed)
        {
            return _manager.IsInstalled ? _manager : null;
        }

        try
        {
            var options = new UpdateOptions();
            var channel = _settings.Current.UpdateChannel;

            if (!string.IsNullOrWhiteSpace(channel))
            {
                options.ExplicitChannel = channel.Trim();
            }

            // A plain path works as well as a URL here, so sites distributing over a file share can put
            // \\\\fileserver\\planner\\releases in settings and nothing else changes.
            var source = feed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new SimpleWebSource(feed)
                : (IUpdateSource)new SimpleFileSource(new DirectoryInfo(feed));

            var locator = _locator ?? VelopackLocator.CreateDefaultForPlatform(logger: _velopackLogger);
            var manager = new UpdateManager(source, options, locator);

            _manager = manager;
            _managerFeed = feed;

            if (!manager.IsInstalled)
            {
                _logger.LogInformation(
                    "Not running from an installed build, so updates are disabled for this process");
                return null;
            }

            return manager;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialise the update manager for feed {Feed}", feed);
            _manager = null;
            _managerFeed = null;
            return null;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException => "Could not reach the update server.",
        TaskCanceledException => "The update server did not respond in time.",
        DirectoryNotFoundException or FileNotFoundException => "The update location does not exist.",
        _ => "The update check failed. See the log for details."
    };

    /// <summary>The installed version when packaged, otherwise the assembly's informational version so
    /// the About box is never blank in development.</summary>
    private string ResolveCurrentVersion()
    {
        try
        {
            if (_manager?.CurrentVersion is { } packaged)
            {
                return packaged.ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the packaged version");
        }

        var informational = typeof(UpdateService).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        // Strip the +sourcerevision suffix the SDK appends.
        var plus = informational?.IndexOf('+') ?? -1;
        return plus > 0 ? informational![..plus] : informational ?? "0.0.0";
    }

    private void Publish(UpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _checkGate.Dispose();
    }
}
