using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Planner.Client.Infrastructure;

namespace Planner.Client.Services;

/// <summary>A refresh token plus the server and account it belongs to.</summary>
public sealed record SavedSession(string ServerUrl, string Email, string RefreshToken);

/// <summary>Persists the refresh token between runs so the client is not a login prompt every morning.
///
/// On Windows the file is encrypted with DPAPI scoped to the current user, so another account on the
/// same machine cannot read it. Elsewhere it is written with owner-only permissions and no encryption —
/// stated plainly rather than dressed up, since obfuscation would only look like security.</summary>
public sealed class SessionStore(ILogger<SessionStore> logger)
{
    private static readonly byte[] Entropy = "Planner.Client.Session.v1"u8.ToArray();

    public SavedSession? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SessionFile))
            {
                return null;
            }

            var stored = File.ReadAllBytes(AppPaths.SessionFile);
            var json = OperatingSystem.IsWindows() ? Unprotect(stored) : Encoding.UTF8.GetString(stored);

            return JsonSerializer.Deserialize<SavedSession>(json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException
                                       or UnauthorizedAccessException)
        {
            // A session that cannot be read is the same as no session: the user signs in again.
            logger.LogWarning(ex, "Stored session could not be read; it will be discarded");
            Clear();
            return null;
        }
    }

    public void Save(SavedSession session)
    {
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(session);

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(AppPaths.SessionFile, Protect(json));
            }
            else
            {
                File.WriteAllText(AppPaths.SessionFile, json);
                RestrictToOwner(AppPaths.SessionFile);
            }
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not save the session; the user will have to sign in next time");
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.SessionFile))
            {
                File.Delete(AppPaths.SessionFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the stored session");
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(string json) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static string Unprotect(byte[] data) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));

    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
