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
                    return _cached = loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read settings; falling back to defaults");
        }

        return _cached = new ClientSettings();
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
