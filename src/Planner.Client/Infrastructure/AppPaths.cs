namespace Planner.Client.Infrastructure;

/// <summary>Where the client keeps state that must survive an update.
///
/// Velopack installs each version into its own <c>app-{version}</c> folder and deletes the old one, so
/// anything written next to the executable is gone after the next update. Settings, the saved session
/// and logs therefore live in the roaming profile instead.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Planner");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    /// <summary>Encrypted refresh token. Not in settings.json, so a user can hand that file to support.</summary>
    public static string SessionFile { get; } = Path.Combine(DataDirectory, "session.dat");

    public static string LogFile { get; } = Path.Combine(LogDirectory, $"planner-{DateTime.Now:yyyy-MM-dd}.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
