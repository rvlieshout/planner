using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Planner.Client.Infrastructure;

namespace Planner.Client.Services;

public sealed class ClientSettings
{
    /// <summary>The hosted deployment. The sign-in screen offers it as the default so a fresh install
    /// is usable without anyone typing a URL; development runs override it with PLANNER_SERVER_URL.</summary>
    public const string DefaultServerUrl = "https://planner.lyste.net";

    /// <summary>Everyone, unless they opt in below. Velopack's default Windows channel, so a stable
    /// install works with no client-side configuration at all.</summary>
    public const string StableChannel = "win";

    /// <summary>The pilot channel. Reached only by setting <c>updateChannel</c> in settings.json, which
    /// is the point: beta builds are published to the same feed and nobody arrives on them by accident.</summary>
    public const string BetaChannel = "win-beta";

    public const string DefaultUpdateFeedUrl = $"{DefaultServerUrl}/updates";

    /// <summary>The Planner API used for sign-in and workspace data.</summary>
    public string ServerUrl { get; set; } = DefaultServerUrl;

    /// <summary>Where Velopack looks for releases. Empty uses the hosted Planner update feed,
    /// independently of ServerUrl. Can also be a UNC path or a local folder.</summary>
    public string? UpdateFeedUrl { get; set; }

    /// <summary>Velopack channel. Empty means <see cref="StableChannel"/>. Set it to
    /// <see cref="BetaChannel"/> on a pilot machine to follow beta releases.</summary>
    public string? UpdateChannel { get; set; }

    public string? LastEmail { get; set; }

    public Guid? LastTeamId { get; set; }

    /// <summary>Resolves the feed once, so the update service and the diagnostics screen cannot disagree.</summary>
    public string ResolveUpdateFeed() =>
        string.IsNullOrWhiteSpace(UpdateFeedUrl)
            ? DefaultUpdateFeedUrl
            : UpdateFeedUrl.Trim();

    /// <summary>Resolves the channel the same way. Named explicitly rather than left to Velopack's
    /// platform default, so a log line or a support question has an answer that is not "whatever the
    /// library decided".</summary>
    public string ResolveUpdateChannel() =>
        string.IsNullOrWhiteSpace(UpdateChannel)
            ? StableChannel
            : UpdateChannel.Trim();
}

/// <summary>Reads and writes <c>%AppData%\Planner\settings.json</c>. A missing or corrupt file is not an
/// error — the client falls back to defaults so a bad write can never brick the app.</summary>
public sealed class SettingsStore(ILogger<SettingsStore> logger)
{
    /// <summary>Overrides the stored server URL for this process only.
    ///
    /// Set by the Aspire app host, which knows the port the API actually landed on and cannot write to a
    /// file the user also edits by hand. It is equally the hook a managed deployment needs: a launcher
    /// script can point a machine at its own server without provisioning a settings file first.</summary>
    private const string ServerUrlVariable = "PLANNER_SERVER_URL";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private ClientSettings? _cached;

    public ClientSettings Current => _cached ??= Load();

    public ClientSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<ClientSettings>(
                    File.ReadAllText(AppPaths.SettingsFile), Options);

                if (loaded is not null)
                {
                    return _cached = Override(loaded);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read settings; falling back to defaults");
        }

        return _cached = Override(new ClientSettings());
    }

    /// <summary>Applies the environment override, if there is one. Deliberately not written back: the
    /// override lasts as long as the process that set it, and must not silently rewrite the URL someone
    /// typed into the sign-in screen.</summary>
    private static ClientSettings Override(ClientSettings settings)
    {
        if (Environment.GetEnvironmentVariable(ServerUrlVariable) is { Length: > 0 } url)
        {
            settings.ServerUrl = url.Trim();
        }

        return settings;
    }

    public void Save(ClientSettings settings)
    {
        _cached = settings;

        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not save settings");
        }
    }
}
