using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Planner.Contracts.Auth;
using Planner.Contracts.Common;
using Planner.Contracts.Issues;
using Planner.Contracts.Teams;

namespace Planner.Client.Services;

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

/// <summary>Raised for any non-success response, carrying what the API said so the UI can show a real
/// reason instead of "something went wrong".</summary>
public sealed class PlannerApiException(HttpStatusCode status, string detail)
    : Exception(detail)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Thin typed wrapper over the REST API. Deliberately not generated: the surface the client
/// actually uses is small, and hand-writing it keeps the DTOs shared rather than duplicated.</summary>
public sealed class PlannerApiClient(HttpClient http, ILogger<PlannerApiClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private string? _accessToken;

    /// <summary>Invoked on a 401 so the caller can refresh and let the request be retried once.
    /// Set by <see cref="AuthService"/>, which owns the tokens.</summary>
    public Func<CancellationToken, Task<bool>>? OnUnauthorized { get; set; }

    public Uri? BaseAddress { get; private set; }

    public void UseServer(string serverUrl) =>
        BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/", UriKind.Absolute);

    public void UseAccessToken(string? accessToken) => _accessToken = accessToken;

    /// <summary>The current bearer token, for the SignalR connection to reuse.</summary>
    public string? AccessToken => _accessToken;

    public async Task<TokenResponse> SignInAsync(string email, string password, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "planner-desktop",
            ["username"] = email,
            ["password"] = password,
            ["scope"] = "openid profile email roles offline_access planner.api"
        };

        return await PostTokenAsync(form, ct);
    }

    public async Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "planner-desktop",
            ["refresh_token"] = refreshToken
        };

        return await PostTokenAsync(form, ct);
    }

    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Resolve("connect/token"))
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // OAuth errors are { error, error_description } — surface the description, it is written
            // for a person and says things like "this account has been deactivated".
            var detail = TryReadOAuthError(body) ?? $"Sign-in failed ({(int)response.StatusCode}).";
            throw new PlannerApiException(response.StatusCode, detail);
        }

        return JsonSerializer.Deserialize<TokenResponse>(body, Json)
               ?? throw new PlannerApiException(response.StatusCode, "The server returned an empty token response.");
    }

    public Task<MeResponse> GetMeAsync(CancellationToken ct) =>
        GetAsync<MeResponse>("api/v1/me", ct);

    public Task<IReadOnlyList<TeamDto>> GetTeamsAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<TeamDto>>("api/v1/teams", ct);

    public Task<IReadOnlyList<WorkflowStateDto>> GetWorkflowStatesAsync(Guid teamId, CancellationToken ct) =>
        GetAsync<IReadOnlyList<WorkflowStateDto>>($"api/v1/teams/{teamId}/states", ct);

    public Task<PagedResult<IssueSummary>> GetBoardAsync(Guid teamId, CancellationToken ct) =>
        GetAsync<PagedResult<IssueSummary>>($"api/v1/issues?teamId={teamId}&sort=board&pageSize=200", ct);

    public Task<IssueSummary> MoveIssueAsync(Guid issueId, Guid stateId, CancellationToken ct) =>
        SendAsync<IssueSummary>(
            () => new HttpRequestMessage(HttpMethod.Post, Resolve($"api/v1/issues/{issueId}/move"))
            {
                Content = JsonContent.Create(new MoveIssueRequest(stateId, null, null, null), options: Json)
            },
            ct);

    private Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Get, Resolve(path)), ct);

    /// <summary>Sends a request, and on a 401 gives <see cref="OnUnauthorized"/> one chance to refresh
    /// before retrying. One retry only — a refresh token the server rejects will not start working on
    /// the third attempt, and a loop here would hammer the login endpoint.</summary>
    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = factory();

            if (_accessToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            }

            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && OnUnauthorized is not null)
            {
                logger.LogInformation("Access token rejected; attempting refresh");

                if (await OnUnauthorized(ct))
                {
                    continue;
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new PlannerApiException(response.StatusCode, DescribeProblem(response.StatusCode, body));
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                   ?? throw new PlannerApiException(response.StatusCode, "The server returned an empty response.");
        }
    }

    private Uri Resolve(string path) =>
        BaseAddress is null
            ? throw new InvalidOperationException("No server has been configured.")
            : new Uri(BaseAddress, path);

    private static string? TryReadOAuthError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error_description", out var description))
            {
                return description.GetString();
            }

            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Turns an RFC 9457 problem document into one sentence for the UI.</summary>
    private static string DescribeProblem(HttpStatusCode status, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("detail", out var detail) &&
                detail.GetString() is { Length: > 0 } text)
            {
                return text;
            }

            if (document.RootElement.TryGetProperty("errors", out var errors))
            {
                var first = errors.EnumerateObject().FirstOrDefault();
                if (first.Value.ValueKind == JsonValueKind.Array &&
                    first.Value.EnumerateArray().FirstOrDefault().GetString() is { } message)
                {
                    return message;
                }
            }

            if (document.RootElement.TryGetProperty("title", out var title) && title.GetString() is { } titleText)
            {
                return titleText;
            }
        }
        catch (JsonException)
        {
        }

        return $"The server returned {(int)status}.";
    }
}
