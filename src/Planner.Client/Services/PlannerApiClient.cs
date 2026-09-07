using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Planner.Contracts.Auth;
using Planner.Contracts.Common;
using Planner.Contracts.Issues;
using Planner.Contracts.Projects;
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
    // OptionalJson supplies the resolver that omits unset Optional<T> properties, which is what makes
    // a PATCH body mean "change this one field" rather than "clear everything I did not mention".
    private static readonly JsonSerializerOptions Json =
        OptionalJson.CreateOptions(new JsonStringEnumConverter());

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

    public Task<PagedResult<UserSummary>> GetUsersAsync(int page, CancellationToken ct) =>
        GetAsync<PagedResult<UserSummary>>($"api/v1/users?includeInactive=true&pageSize=200&page={page}", ct);

    public Task<UserDetail> GetUserAsync(Guid id, CancellationToken ct) =>
        GetAsync<UserDetail>($"api/v1/users/{id}", ct);

    public Task<UserSummary> CreateUserAsync(CreateUserRequest request, CancellationToken ct) =>
        SendJsonAsync<UserSummary>(HttpMethod.Post, "api/v1/users", request, ct);

    public Task<UserSummary> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct) =>
        SendJsonAsync<UserSummary>(HttpMethod.Patch, $"api/v1/users/{id}", request, ct);

    public Task ResetUserPasswordAsync(Guid id, string password, CancellationToken ct) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Resolve($"api/v1/users/{id}/password"))
        {
            Content = JsonContent.Create(new ResetPasswordRequest(password), options: Json)
        }, ct);

    public Task<IReadOnlyList<TeamDto>> GetAdministrationTeamsAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<TeamDto>>("api/v1/teams?includeArchived=true", ct);

    public Task<TeamMemberDto> AddTeamMemberAsync(Guid teamId, AddTeamMemberRequest request, CancellationToken ct) =>
        SendJsonAsync<TeamMemberDto>(HttpMethod.Post, $"api/v1/teams/{teamId}/members", request, ct);

    public Task<TeamMemberDto> UpdateTeamMemberAsync(Guid teamId, Guid userId, UpdateTeamMemberRequest request, CancellationToken ct) =>
        SendJsonAsync<TeamMemberDto>(HttpMethod.Patch, $"api/v1/teams/{teamId}/members/{userId}", request, ct);

    public Task RemoveTeamMemberAsync(Guid teamId, Guid userId, CancellationToken ct) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Resolve($"api/v1/teams/{teamId}/members/{userId}")), ct);

    private Task<T> SendJsonAsync<T>(HttpMethod method, string path, object body, CancellationToken ct) =>
        SendAsync<T>(() => new HttpRequestMessage(method, Resolve(path))
        {
            Content = JsonContent.Create(body, options: Json)
        }, ct);

    public Task<IReadOnlyList<TeamDto>> GetTeamsAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<TeamDto>>("api/v1/teams", ct);

    public Task<IReadOnlyList<WorkflowStateDto>> GetWorkflowStatesAsync(Guid teamId, CancellationToken ct) =>
        GetAsync<IReadOnlyList<WorkflowStateDto>>($"api/v1/teams/{teamId}/states", ct);

    public Task<PagedResult<IssueSummary>> GetBoardAsync(Guid teamId, CancellationToken ct) =>
        GetAsync<PagedResult<IssueSummary>>($"api/v1/issues?teamId={teamId}&sort=board&pageSize=200", ct);

    public Task<IReadOnlyList<TeamMemberDto>> GetTeamMembersAsync(Guid teamId, CancellationToken ct) =>
        GetAsync<IReadOnlyList<TeamMemberDto>>($"api/v1/teams/{teamId}/members", ct);

    /// <summary>Team labels plus the organisation-wide ones, which is what the issue form should offer.</summary>
    public Task<IReadOnlyList<LabelDto>> GetTeamLabelsAsync(Guid teamId, CancellationToken ct) =>
        GetAsync<IReadOnlyList<LabelDto>>($"api/v1/teams/{teamId}/labels", ct);

    public Task<PagedResult<ProjectDto>> GetProjectsAsync(Guid teamId, CancellationToken ct) =>
        GetAsync<PagedResult<ProjectDto>>($"api/v1/projects?teamId={teamId}&pageSize=100", ct);

    public Task<ProjectDto> GetProjectAsync(Guid projectId, CancellationToken ct) =>
        GetAsync<ProjectDto>($"api/v1/projects/{projectId}", ct);

    public Task<ProjectDto> CreateProjectAsync(CreateProjectRequest request, CancellationToken ct) =>
        SendAsync<ProjectDto>(
            () => new HttpRequestMessage(HttpMethod.Post, Resolve("api/v1/projects"))
            {
                Content = JsonContent.Create(request, options: Json)
            },
            ct);

    /// <summary>Sends only the fields the caller actually set; see <see cref="OptionalJson"/>.</summary>
    public Task<ProjectDto> UpdateProjectAsync(Guid projectId, UpdateProjectRequest request, CancellationToken ct) =>
        SendAsync<ProjectDto>(
            () => new HttpRequestMessage(HttpMethod.Patch, Resolve($"api/v1/projects/{projectId}"))
            {
                Content = JsonContent.Create(request, options: Json)
            },
            ct);

    public Task<IReadOnlyList<MilestoneDto>> GetMilestonesAsync(Guid projectId, CancellationToken ct) =>
        GetAsync<IReadOnlyList<MilestoneDto>>($"api/v1/projects/{projectId}/milestones", ct);

    public Task<MilestoneDto> CreateMilestoneAsync(
        Guid projectId, CreateMilestoneRequest request, CancellationToken ct) =>
        SendAsync<MilestoneDto>(
            () => new HttpRequestMessage(HttpMethod.Post, Resolve($"api/v1/projects/{projectId}/milestones"))
            {
                Content = JsonContent.Create(request, options: Json)
            },
            ct);

    public Task<MilestoneDto> UpdateMilestoneAsync(
        Guid milestoneId, UpdateMilestoneRequest request, CancellationToken ct) =>
        SendAsync<MilestoneDto>(
            () => new HttpRequestMessage(HttpMethod.Patch, Resolve($"api/v1/milestones/{milestoneId}"))
            {
                Content = JsonContent.Create(request, options: Json)
            },
            ct);

    /// <summary>Removes a milestone. Its issues survive and fall back to the project.</summary>
    public Task DeleteMilestoneAsync(Guid milestoneId, CancellationToken ct) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Resolve($"api/v1/milestones/{milestoneId}")), ct);

    /// <summary>Everything assigned to one person, across every team they can see. No teamId filter:
    /// "my issues" is a person's whole workload, not their workload in the team currently on screen.</summary>
    public Task<PagedResult<IssueSummary>> GetAssignedIssuesAsync(Guid userId, CancellationToken ct) =>
        GetAsync<PagedResult<IssueSummary>>(
            $"api/v1/issues?assigneeId={userId}&sort=-updatedAt&pageSize=200", ct);

    public Task<PagedResult<IssueSummary>> GetProjectIssuesAsync(Guid projectId, CancellationToken ct) =>
        GetAsync<PagedResult<IssueSummary>>($"api/v1/issues?projectId={projectId}&sort=board&pageSize=200", ct);

    public Task<IssueSummary> CreateIssueAsync(CreateIssueRequest request, CancellationToken ct) =>
        SendAsync<IssueSummary>(
            () => new HttpRequestMessage(HttpMethod.Post, Resolve("api/v1/issues"))
            {
                Content = JsonContent.Create(request, options: Json)
            },
            ct);

    public Task<IssueDetail> GetIssueAsync(Guid issueId, CancellationToken ct) =>
        GetAsync<IssueDetail>($"api/v1/issues/{issueId}", ct);

    /// <summary>Sends only the fields the caller actually set; see <see cref="OptionalJson"/>.</summary>
    public Task<IssueSummary> UpdateIssueAsync(Guid issueId, UpdateIssueRequest request, CancellationToken ct) =>
        SendAsync<IssueSummary>(
            () => new HttpRequestMessage(HttpMethod.Patch, Resolve($"api/v1/issues/{issueId}"))
            {
                Content = JsonContent.Create(request, options: Json)
            },
            ct);

    public Task<IssueSummary> ArchiveIssueAsync(Guid issueId, CancellationToken ct) =>
        SendAsync<IssueSummary>(
            () => new HttpRequestMessage(HttpMethod.Post, Resolve($"api/v1/issues/{issueId}/archive")),
            ct);

    /// <summary>Moves an issue to a state, and optionally between two neighbours.
    ///
    /// The anchors are what make a drag land where it was dropped: the server takes the midpoint of
    /// the two ranks, so a reorder writes one row instead of renumbering the column. Passing neither
    /// puts the issue at the end of the target column.</summary>
    public Task<IssueSummary> MoveIssueAsync(
        Guid issueId,
        Guid stateId,
        Guid? afterIssueId,
        Guid? beforeIssueId,
        CancellationToken ct) =>
        SendAsync<IssueSummary>(
            () => new HttpRequestMessage(HttpMethod.Post, Resolve($"api/v1/issues/{issueId}/move"))
            {
                Content = JsonContent.Create(
                    new MoveIssueRequest(stateId, null, afterIssueId, beforeIssueId), options: Json)
            },
            ct);

    private Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Get, Resolve(path)), ct);

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        using var response = await SendCoreAsync(factory, ct);

        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new PlannerApiException(response.StatusCode, "The server returned an empty response.");
    }

    /// <summary>For the endpoints that answer 204: there is no body to read, and asking for one would
    /// turn a successful delete into a deserialization failure.</summary>
    private async Task SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct) =>
        (await SendCoreAsync(factory, ct)).Dispose();

    /// <summary>Sends a request, and on a 401 gives <see cref="OnUnauthorized"/> one chance to refresh
    /// before retrying. One retry only — a refresh token the server rejects will not start working on
    /// the third attempt, and a loop here would hammer the login endpoint.</summary>
    private async Task<HttpResponseMessage> SendCoreAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = factory();

            if (_accessToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            }

            var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && OnUnauthorized is not null)
            {
                logger.LogInformation("Access token rejected; attempting refresh");

                if (await OnUnauthorized(ct))
                {
                    response.Dispose();
                    continue;
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    throw new PlannerApiException(response.StatusCode, DescribeProblem(response.StatusCode, body));
                }
            }

            // Handed to the caller still open: it owns the disposal, because it is the one that reads
            // the body.
            return response;
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
