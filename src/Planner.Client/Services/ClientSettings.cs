using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Planner.Client.Infrastructure;

namespace Planner.Client.Services;

public sealed class ClientSettings
{
    /// <summary>The on-prem Planner API. Everything else is derived from it unless overridden.</summary>
    public string ServerUrl { get; set; } = "http://localhost:8080";

    /// <summary>Where Velopack looks for releases. Empty means <c>{ServerUrl}/updates</c>, which is what
    /// the API serves. Can also be a UNC path or a local folder for sites that distribute over a share.</summary>
    public string? UpdateFeedUrl { get; set; }

    /// <summary>Velopack channel. Empty uses the platform default (<c>win</c>), which is what
    /// <c>build/release.ps1</c> publishes. Point a pilot group at a channel such as <c>beta</c>.</summary>
    public string? UpdateChannel { get; set; }

    public string? LastEmail { get; set; }

    public Guid? LastTeamId { get; set; }

    /// <summary>Resolves the feed once, so the update service and the diagnostics screen cannot disagree.</summary>
    public string ResolveUpdateFeed() =>
        string.IsNullOrWhiteSpace(UpdateFeedUrl)
            ? $"{ServerUrl.TrimEnd('/')}/updates"
            : UpdateFeedUrl.Trim();
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
