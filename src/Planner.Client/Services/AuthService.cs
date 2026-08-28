using Microsoft.Extensions.Logging;
using Planner.Contracts.Auth;

namespace Planner.Client.Services;

public sealed record SignInResult(bool Success, string? Error = null)
{
    public static readonly SignInResult Ok = new(true);

    public static SignInResult Failed(string error) => new(false, error);
}

/// <summary>Owns the tokens and the signed-in identity.
///
/// Note what this class does *not* do: gate the update service. Updates are checked whether or not
/// anyone ever signs in, precisely because a broken sign-in is one of the things an update has to be
/// able to fix.</summary>
public sealed class AuthService(
    PlannerApiClient api,
    SessionStore sessions,
    SettingsStore settings,
    ILogger<AuthService> logger)
{
    private string? _refreshToken;

    public MeResponse? CurrentUser { get; private set; }

    public bool IsSignedIn => CurrentUser is not null;

    /// <summary>Handed to the SignalR connection so the socket authenticates as the same user.</summary>
    public string? AccessToken => api.AccessToken;

    public string ServerUrl => settings.Current.ServerUrl;

    public event Action? StateChanged;

    /// <summary>Called by the API client when a request comes back 401.</summary>
    public void Attach() => api.OnUnauthorized = RefreshAsync;

    /// <summary>Tries to resume the last session without prompting. Any failure is silent and simply
    /// lands the user on the sign-in screen — including a server that is unreachable.</summary>
    public async Task<bool> TryRestoreAsync(CancellationToken ct)
    {
        var saved = sessions.Load();
        if (saved is null)
        {
            return false;
        }

        try
        {
            api.UseServer(saved.ServerUrl);
            var tokens = await api.RefreshAsync(saved.RefreshToken, ct);
            await AdoptAsync(saved.ServerUrl, saved.Email, tokens, ct);

            logger.LogInformation("Resumed the session for {Email}", saved.Email);
            return true;
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation(ex, "Could not resume the stored session");
            sessions.Clear();
            return false;
        }
    }

    public async Task<SignInResult> SignInAsync(string serverUrl, string email, string password, CancellationToken ct)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return SignInResult.Failed("Enter a server address such as http://planner.internal:8080");
        }

        try
        {
            api.UseServer(serverUrl);
            var tokens = await api.SignInAsync(email, password, ct);
            await AdoptAsync(serverUrl, email, tokens, ct);

            logger.LogInformation("Signed in as {Email}", email);
            return SignInResult.Ok;
        }
        catch (PlannerApiException ex)
        {
            logger.LogWarning("Sign-in rejected: {Detail}", ex.Message);
            return SignInResult.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Sign-in could not reach {Server}", serverUrl);
            return SignInResult.Failed($"Could not reach {serverUrl}. Check the address and your connection.");
        }
    }

    public void SignOut()
    {
        sessions.Clear();
        CurrentUser = null;
        _refreshToken = null;
        api.UseAccessToken(null);

        logger.LogInformation("Signed out");
        StateChanged?.Invoke();
    }

    private async Task AdoptAsync(string serverUrl, string email, TokenResponse tokens, CancellationToken ct)
    {
        api.UseAccessToken(tokens.AccessToken);
        _refreshToken = tokens.RefreshToken;

        CurrentUser = await api.GetMeAsync(ct);

        if (tokens.RefreshToken is not null)
        {
            sessions.Save(new SavedSession(serverUrl, email, tokens.RefreshToken));
        }

        var current = settings.Current;
        current.ServerUrl = serverUrl;
        current.LastEmail = email;
        settings.Save(current);

        StateChanged?.Invoke();
    }

    /// <summary>Refreshes in place. Returns false when the session is beyond saving, in which case the
    /// user is signed out rather than left staring at a screen that silently stopped updating.</summary>
    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (_refreshToken is null)
        {
            return false;
        }

        try
        {
            var tokens = await api.RefreshAsync(_refreshToken, ct);
            api.UseAccessToken(tokens.AccessToken);
            _refreshToken = tokens.RefreshToken ?? _refreshToken;

            if (tokens.RefreshToken is not null && CurrentUser is not null)
            {
                sessions.Save(new SavedSession(settings.Current.ServerUrl, CurrentUser.Email, tokens.RefreshToken));
            }

            return true;
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Refresh failed; signing out");
            SignOut();
            return false;
        }
    }
}
