using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Client.Services;
using Planner.Client.ViewModels;
using Planner.Contracts.Auth;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;
using Planner.Contracts.Issues;
using Planner.Contracts.Teams;

internal static class IssueDetailChecks
{
    public static async Task RunAsync()
    {
        using var server = new IssueServer();
        using var http = new HttpClient(server);
        var api = new PlannerApiClient(http, NullLogger<PlannerApiClient>.Instance);
        api.UseServer("https://planner.test");
        var caller = new MeResponse(server.User.Id, server.User.Email, server.User.DisplayName, null,
            "UTC", "member", true, [new(server.TeamId, "ENG", "Engineering", "Member")]);
        var page = new IssueDetailViewModel(api, NullLoggerFactory.Instance, caller, server.Id);
        await page.LoadAsync(default);
        Check(page.Error is null && page.Editor?.IsReady == true && page.Comments.Count == 2,
            "Detail loads editor and every comment page");
        Check(page.CanEdit && page.CanComment && !page.HasUnsavedChanges, "Members can edit and contribute");

        page.Editor!.IssueTitle = "Changed title";
        page.CommentBody = "Draft";
        await page.RefreshContributionsAsync(default);
        Check(page.Editor.IssueTitle == "Changed title" && page.CommentBody == "Draft",
            "Collaboration refresh preserves issue and comment drafts");

        server.FailComment = true;
        await page.PostCommentCommand.ExecuteAsync(null);
        Check(page.CommentBody == "Draft" && page.Error is not null, "A failed comment keeps the draft");
        server.FailComment = false;
        await page.PostCommentCommand.ExecuteAsync(null);
        Check(page.CommentBody is null && page.Comments.Count == 3, "A posted comment is shown once");

        await page.Editor.SubmitCommand.ExecuteAsync(null);
        using (var patch = JsonDocument.Parse(server.LastPatch!))
            Check(patch.RootElement.EnumerateObject().Count() == 1 &&
                patch.RootElement.GetProperty("title").GetString() == "Changed title",
                "Saving sends only changed fields");
        Check(!page.Editor.HasUnsavedChanges, "Saving advances the baseline");
        page.Editor.Description = "Second edit";
        await page.Editor.SubmitCommand.ExecuteAsync(null);
        Check(!server.LastPatch!.Contains("title"), "A subsequent save does not overwrite another person's title");

        page.AttachmentName = "Shared file";
        page.AttachmentUrl = "file:///private/file.txt";
        await page.AddAttachmentCommand.ExecuteAsync(null);
        Check(server.LinkCreates == 0 && page.AttachmentName is not null, "Local paths are not published as shared links");
        page.AttachmentUrl = "https://example.test/file";
        await page.AddAttachmentCommand.ExecuteAsync(null);
        Check(server.LinkCreates == 1 && page.Attachments.Count == 1, "Shared attachments are persisted");

        await page.UploadAsync("notes.txt", [1, 2, 3]);
        Check(server.UploadBytes?.SequenceEqual(new byte[] { 1, 2, 3 }) == true &&
            page.Attachments.Count == 2, "File upload sends bytes and refreshes attachments");
        Check((await page.DownloadAsync(server.Attachments.Last().Id)).SequenceEqual(new byte[] { 1, 2, 3 }),
            "Uploaded files can be downloaded");

        page.SearchText = "ENG";
        await page.SearchCommand.ExecuteAsync(null);
        Check(page.SearchResults.Count == 1 && page.SearchResults[0].Id != page.IssueId,
            "Relation search excludes the current issue");
        page.SelectedRelatedIssue = page.SearchResults[0];
        page.SelectedRelationType = IssueRelationType.Blocks;
        await page.AddRelationCommand.ExecuteAsync(null);
        Check(page.Relations.Count == 1 && page.Relations[0].Relationship == "Blocks", "Relationship type is persisted");
        var relation = page.Relations[0];
        Check(new RelatedIssueRow(relation.Relation with { IsOutgoing = false }).Relationship == "Blocked by",
            "Incoming relationship direction is readable");
        Guid? navigated = null;
        page.NavigateRequested += id => navigated = id;
        page.OpenIssueCommand.Execute(relation.IssueId);
        Check(navigated == relation.IssueId, "Related issues request navigation to their own ID");
        await page.RemoveRelationCommand.ExecuteAsync(relation);
        Check(page.Relations.Count == 0, "Relationships can be removed");

        IssueDetail? parent = null;
        page.CreateChildRequested += detail => parent = detail;
        page.CreateChildCommand.Execute(null);
        var child = IssueEditorViewModel.ForCreate(api, NullLogger.Instance, server.TeamId, "Engineering");
        child.DefaultParentId = parent!.Id;
        await child.LoadAsync(default);
        child.IssueTitle = "Child";
        await child.SubmitCommand.ExecuteAsync(null);
        Check(server.CreatedParent == page.IssueId, "Sub-issue creation persists the parent");

        var guest = new IssueDetailViewModel(api, NullLoggerFactory.Instance, caller with { Role = "guest" }, server.Id);
        await guest.LoadAsync(default);
        Check(!guest.CanEdit && guest.CanComment, "Guests can contribute without editing issues");
        var viewer = new IssueDetailViewModel(api, NullLoggerFactory.Instance,
            caller with { Teams = [new(server.TeamId, "ENG", "Engineering", "Viewer")] }, server.Id);
        await viewer.LoadAsync(default);
        Check(!viewer.CanEdit && !viewer.CanComment, "Viewer permissions match the API");
        Console.WriteLine("Issue detail checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class IssueServer : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
        public Guid Id { get; } = Guid.NewGuid();
        public Guid TeamId { get; } = Guid.NewGuid();
        private Guid TargetId { get; } = Guid.NewGuid();
        public UserSummary User { get; } = new(Guid.NewGuid(), "member@test", "Member", null, true);
        public List<CommentDto> Comments { get; } = [];
        public List<AttachmentDto> Attachments { get; } = [];
        private List<IssueRelationDto> Relations { get; } = [];
        public bool FailComment { get; set; }
        public string? LastPatch { get; private set; }
        public Guid? CreatedParent { get; private set; }
        public int LinkCreates { get; private set; }
        public byte[]? UploadBytes { get; private set; }
        private string Title { get; set; } = "Issue";
        private string? Description { get; set; }

        public IssueServer()
        {
            Comments.Add(Comment("First"));
            Comments.Add(Comment("Second"));
        }

        private CommentDto Comment(string body) => new(Guid.NewGuid(), Id, User, body, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

        private IssueDetail Detail() => JsonSerializer.Deserialize<IssueDetail>(JsonSerializer.Serialize(new
        {
            Id, key = "ENG-1", TeamId, number = 1, Title, Description,
            stateName = "Todo", stateColor = "#888888", creator = User,
            labels = Array.Empty<LabelDto>(), children = Array.Empty<IssueSummary>(),
            relations = Relations, attachments = Attachments, commentCount = Comments.Count
        }, Json), Json)!;

        private IssueSummary Summary() =>
            JsonSerializer.Deserialize<IssueSummary>(JsonSerializer.Serialize(Detail(), Json), Json)!;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/content"))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(UploadBytes!) };
            if (path.EndsWith("/files"))
            {
                UploadBytes = await request.Content!.ReadAsByteArrayAsync(ct);
                var id = Guid.NewGuid();
                var file = new AttachmentDto(id, Id, "notes.txt", null, UploadBytes.Length,
                    $"planner-attachment:{id}", User, DateTimeOffset.UtcNow);
                Attachments.Add(file);
                return Ok(file);
            }
            if (path.EndsWith("/comments"))
            {
                if (request.Method == HttpMethod.Post)
                {
                    if (FailComment) return new(HttpStatusCode.ServiceUnavailable);
                    var body = await request.Content!.ReadFromJsonAsync<CreateCommentRequest>(Json, ct);
                    var comment = Comment(body!.Body);
                    Comments.Add(comment);
                    return Ok(comment);
                }
                var page = request.RequestUri.Query.Contains("page=1&") ? 1 : 2;
                return Ok(new PagedResult<CommentDto>(
                    page == 1 ? Comments.Take(1).ToArray() : Comments.Skip(1).ToArray(),
                    page, page == 1 ? 1 : 200, Comments.Count));
            }
            if (path.EndsWith("/attachments"))
            {
                var body = await request.Content!.ReadFromJsonAsync<CreateAttachmentRequest>(Json, ct);
                var file = new AttachmentDto(Guid.NewGuid(), Id, body!.FileName, null, null,
                    body.StorageUri, User, DateTimeOffset.UtcNow);
                Attachments.Add(file);
                LinkCreates++;
                return Ok(file);
            }
            if (path.Contains("/relations"))
            {
                if (request.Method == HttpMethod.Delete)
                {
                    Relations.Clear();
                    return new(HttpStatusCode.NoContent);
                }
                var body = await request.Content!.ReadFromJsonAsync<CreateIssueRelationRequest>(Json, ct);
                var relation = new IssueRelationDto(Guid.NewGuid(), body!.Type, true,
                    body.TargetIssueId, "ENG-2", "Other", WorkflowStateType.Backlog);
                Relations.Add(relation);
                return Ok(relation);
            }
            if (path == "/api/v1/issues")
            {
                if (request.Method == HttpMethod.Post)
                {
                    var body = await request.Content!.ReadFromJsonAsync<CreateIssueRequest>(Json, ct);
                    CreatedParent = body!.ParentId;
                    return Ok(Summary() with { ParentId = CreatedParent });
                }
                return Ok(new PagedResult<IssueSummary>([Summary(), Summary() with { Id = TargetId }], 1, 50, 2));
            }
            if (path == $"/api/v1/issues/{Id}")
            {
                if (request.Method == HttpMethod.Patch)
                {
                    LastPatch = await request.Content!.ReadAsStringAsync(ct);
                    using var patch = JsonDocument.Parse(LastPatch);
                    if (patch.RootElement.TryGetProperty("title", out var title)) Title = title.GetString()!;
                    if (patch.RootElement.TryGetProperty("description", out var description)) Description = description.GetString();
                    return Ok(Summary());
                }
                return Ok(Detail());
            }
            if (path.EndsWith("/projects")) return Ok(new { items = Array.Empty<object>() });
            if (path.EndsWith("/states") || path.EndsWith("/members") || path.EndsWith("/labels"))
                return Ok(Array.Empty<object>());
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage Ok(object body) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Json) };
    }
}
