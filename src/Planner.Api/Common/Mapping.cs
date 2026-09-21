using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Planner.Contracts.Auth;
using Planner.Contracts.Common;
using Planner.Contracts.Issues;
using Planner.Contracts.Projects;
using Planner.Contracts.Teams;
using Planner.Domain.Entities;
using Planner.Contracts.Enums;
using Planner.Domain.Identity;

namespace Planner.Api.Common;

/// <summary>Entity to DTO projections. They are declared as expression trees so EF Core can push them
/// into SQL and fetch only the columns a view needs; the compiled counterparts are reused for
/// in-memory mapping so the two can never drift apart.</summary>
public static class Mapping
{
    public static readonly Expression<Func<AppUser, UserSummary>> UserSummaryProjection =
        u => new UserSummary(u.Id, u.Email!, u.DisplayName, u.AvatarUrl, u.IsActive);

    public static readonly Func<AppUser, UserSummary> ToUserSummary = UserSummaryProjection.Compile();

    public static readonly Expression<Func<Team, TeamDto>> TeamProjection =
        t => new TeamDto(
            t.Id,
            t.Key,
            t.Name,
            t.Description,
            t.Color,
            t.IsPrivate,
            t.Members.Count,
            t.CreatedAt,
            t.UpdatedAt,
            t.ArchivedAt);

    public static readonly Func<Team, TeamDto> ToTeam = TeamProjection.Compile();

    public static readonly Expression<Func<TeamMember, TeamMemberDto>> TeamMemberProjection =
        m => new TeamMemberDto(
            m.TeamId,
            m.UserId,
            m.User.DisplayName,
            m.User.Email!,
            m.User.AvatarUrl,
            m.Role,
            m.CreatedAt);

    public static readonly Func<TeamMember, TeamMemberDto> ToTeamMember = TeamMemberProjection.Compile();

    public static readonly Expression<Func<WorkflowState, WorkflowStateDto>> WorkflowStateProjection =
        s => new WorkflowStateDto(s.Id, s.TeamId, s.Name, s.Type, s.Color, s.Position, s.IsDefault);

    public static readonly Func<WorkflowState, WorkflowStateDto> ToWorkflowState = WorkflowStateProjection.Compile();

    public static readonly Expression<Func<Label, LabelDto>> LabelProjection =
        l => new LabelDto(l.Id, l.TeamId, l.Name, l.Color, l.Description);

    public static readonly Func<Label, LabelDto> ToLabel = LabelProjection.Compile();

    public static readonly Expression<Func<Project, ProjectDto>> ProjectProjection =
        p => new ProjectDto(
            p.Id,
            p.TeamId,
            p.Team.Key,
            p.Name,
            p.Summary,
            p.Description,
            p.Status,
            p.Health,
            p.Color,
            p.LeadUser == null
                ? null
                : new UserSummary(
                    p.LeadUser.Id,
                    p.LeadUser.Email!,
                    p.LeadUser.DisplayName,
                    p.LeadUser.AvatarUrl,
                    p.LeadUser.IsActive),
            p.StartDate,
            p.TargetDate,
            p.SortOrder,
            new ProjectProgress(
                p.Issues.Count(i => i.ArchivedAt == null),
                p.Issues.Count(i => i.ArchivedAt == null && i.State.Type == WorkflowStateType.Completed),
                p.Issues.Count(i => i.ArchivedAt == null && i.State.Type == WorkflowStateType.Started),
                p.Issues.Count(i => i.ArchivedAt == null && i.State.Type == WorkflowStateType.Canceled)),
            p.CreatedAt,
            p.UpdatedAt,
            p.CompletedAt,
            p.ArchivedAt);

    public static readonly Func<Project, ProjectDto> ToProject = ProjectProjection.Compile();

    public static readonly Expression<Func<Milestone, MilestoneDto>> MilestoneProjection =
        m => new MilestoneDto(
            m.Id,
            m.ProjectId,
            m.Name,
            m.Description,
            m.TargetDate,
            m.Status,
            m.SortOrder,
            new ProjectProgress(
                m.Issues.Count(i => i.ArchivedAt == null),
                m.Issues.Count(i => i.ArchivedAt == null && i.State.Type == WorkflowStateType.Completed),
                m.Issues.Count(i => i.ArchivedAt == null && i.State.Type == WorkflowStateType.Started),
                m.Issues.Count(i => i.ArchivedAt == null && i.State.Type == WorkflowStateType.Canceled)),
            m.CreatedAt,
            m.UpdatedAt,
            m.CompletedAt);

    public static readonly Func<Milestone, MilestoneDto> ToMilestone = MilestoneProjection.Compile();

    public static readonly Expression<Func<Document, DocumentSummary>> DocumentSummaryProjection =
        d => new DocumentSummary(
            d.Id,
            d.TeamId,
            d.ProjectId,
            d.Title,
            new UserSummary(
                d.CreatedBy.Id,
                d.CreatedBy.Email!,
                d.CreatedBy.DisplayName,
                d.CreatedBy.AvatarUrl,
                d.CreatedBy.IsActive),
            d.UpdatedBy == null
                ? null
                : new UserSummary(
                    d.UpdatedBy.Id,
                    d.UpdatedBy.Email!,
                    d.UpdatedBy.DisplayName,
                    d.UpdatedBy.AvatarUrl,
                    d.UpdatedBy.IsActive),
            d.CreatedAt,
            d.UpdatedAt,
            d.ArchivedAt);

    public static readonly Func<Document, DocumentSummary> ToDocumentSummary = DocumentSummaryProjection.Compile();

    public static readonly Expression<Func<Document, DocumentDto>> DocumentProjection =
        d => new DocumentDto(
            d.Id,
            d.TeamId,
            d.ProjectId,
            d.Title,
            d.Content,
            new UserSummary(
                d.CreatedBy.Id,
                d.CreatedBy.Email!,
                d.CreatedBy.DisplayName,
                d.CreatedBy.AvatarUrl,
                d.CreatedBy.IsActive),
            d.UpdatedBy == null
                ? null
                : new UserSummary(
                    d.UpdatedBy.Id,
                    d.UpdatedBy.Email!,
                    d.UpdatedBy.DisplayName,
                    d.UpdatedBy.AvatarUrl,
                    d.UpdatedBy.IsActive),
            d.CreatedAt,
            d.UpdatedAt,
            d.ArchivedAt);

    public static readonly Expression<Func<Issue, IssueSummary>> IssueSummaryProjection =
        i => new IssueSummary(
            i.Id,
            i.Team.Key + "-" + i.Number.ToString(),
            i.TeamId,
            i.Number,
            i.Title,
            i.StateId,
            i.State.Name,
            i.State.Type,
            i.State.Color,
            i.Priority,
            i.Assignee == null
                ? null
                : new UserSummary(
                    i.Assignee.Id,
                    i.Assignee.Email!,
                    i.Assignee.DisplayName,
                    i.Assignee.AvatarUrl,
                    i.Assignee.IsActive),
            i.ProjectId,
            i.MilestoneId,
            i.ParentId,
            i.Estimate,
            i.DueDate,
            i.SortOrder,
            i.Labels
                .Select(l => new LabelDto(l.Label.Id, l.Label.TeamId, l.Label.Name, l.Label.Color, l.Label.Description))
                .ToList(),
            i.Children.Count,
            i.Comments.Count,
            i.CreatedAt,
            i.UpdatedAt,
            i.CompletedAt,
            i.ArchivedAt);

    public static readonly Func<Issue, IssueSummary> ToIssueSummary = IssueSummaryProjection.Compile();

    public static readonly Expression<Func<Comment, CommentDto>> CommentProjection =
        c => new CommentDto(
            c.Id,
            c.IssueId,
            new UserSummary(c.Author.Id, c.Author.Email!, c.Author.DisplayName, c.Author.AvatarUrl, c.Author.IsActive),
            c.Body,
            c.ParentCommentId,
            c.CreatedAt,
            c.UpdatedAt,
            c.EditedAt);

    public static readonly Func<Comment, CommentDto> ToComment = CommentProjection.Compile();

    public static readonly Expression<Func<Attachment, AttachmentDto>> AttachmentProjection =
        a => new AttachmentDto(
            a.Id,
            a.IssueId,
            a.FileName,
            a.ContentType,
            a.SizeBytes,
            a.StorageUri,
            new UserSummary(
                a.UploadedBy.Id,
                a.UploadedBy.Email!,
                a.UploadedBy.DisplayName,
                a.UploadedBy.AvatarUrl,
                a.UploadedBy.IsActive),
            a.CreatedAt);

    public static readonly Func<Attachment, AttachmentDto> ToAttachment = AttachmentProjection.Compile();

    /// <summary>Activity rows carry a jsonb payload, and JsonDocument.RootElement has no SQL
    /// translation, so this one maps after materialisation rather than inside the query.</summary>
    public static ActivityEventDto ToActivity(ActivityEvent a) =>
        new(a.Id,
            a.EntityType,
            a.EntityId,
            a.TeamId,
            a.ProjectId,
            a.IssueId,
            ToUserSummary(a.Actor),
            a.Action,
            ActivityData(a.Data),
            a.CreatedAt);

    /// <summary>Re-encodes the ids buried in an audit payload.
    ///
    /// The jsonb column is written with the plain serializer, so a payload like
    /// <c>{ "from": null, "to": "&lt;uuid&gt;" }</c> holds canonical uuids — including in rows written
    /// before ids were base58 on the wire. The typed fields around it come back base58, so these have
    /// to as well or a client cannot match "assignee changed to X" against the user X it already
    /// holds. Only strings that are exactly a uuid are touched; a title or a state name never is.</summary>
    private static JsonElement? ActivityData(JsonDocument? data)
    {
        if (data is null)
        {
            return null;
        }

        var converted = Encode(data.RootElement);

        return converted is null ? data.RootElement : JsonSerializer.SerializeToElement(converted);

        static JsonNode? Encode(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return Guid.TryParseExact(element.GetString(), "D", out var id)
                        ? JsonValue.Create(id.ToBase58())
                        : null;

                case JsonValueKind.Array:
                {
                    JsonArray? rewritten = null;
                    var index = 0;

                    foreach (var item in element.EnumerateArray())
                    {
                        if (Encode(item) is { } encoded)
                        {
                            rewritten ??= JsonNode.Parse(element.GetRawText())!.AsArray();
                            rewritten[index] = encoded;
                        }

                        index++;
                    }

                    return rewritten;
                }

                case JsonValueKind.Object:
                {
                    JsonObject? rewritten = null;

                    foreach (var property in element.EnumerateObject())
                    {
                        if (Encode(property.Value) is { } encoded)
                        {
                            rewritten ??= JsonNode.Parse(element.GetRawText())!.AsObject();
                            rewritten[property.Name] = encoded;
                        }
                    }

                    return rewritten;
                }

                default:
                    return null;
            }
        }
    }

    /// <summary>Builds the detail view. Children and the comment count are passed in rather than read
    /// off navigations: counting an unloaded collection silently yields zero, and loading every comment
    /// body just to count them is worse. Relations are unioned from both directions, so a row stored as
    /// "A blocks B" also surfaces on B as an inbound relation.</summary>
    public static IssueDetail ToIssueDetail(Issue issue, IReadOnlyList<IssueSummary> children, int commentCount)
    {
        var relations = issue.OutgoingRelations
            .Select(r => new IssueRelationDto(
                r.Id,
                r.Type,
                true,
                r.TargetIssue.Id,
                r.TargetIssue.Team.Key + "-" + r.TargetIssue.Number,
                r.TargetIssue.Title,
                r.TargetIssue.State.Type))
            .Concat(issue.IncomingRelations
                .Select(r => new IssueRelationDto(
                    r.Id,
                    r.Type,
                    false,
                    r.SourceIssue.Id,
                    r.SourceIssue.Team.Key + "-" + r.SourceIssue.Number,
                    r.SourceIssue.Title,
                    r.SourceIssue.State.Type)))
            .ToList();

        return new IssueDetail(
            issue.Id,
            $"{issue.Team.Key}-{issue.Number}",
            issue.TeamId,
            issue.Number,
            issue.Title,
            issue.Description,
            issue.StateId,
            issue.State.Name,
            issue.State.Type,
            issue.State.Color,
            issue.Priority,
            issue.Assignee is null ? null : ToUserSummary(issue.Assignee),
            ToUserSummary(issue.Creator),
            issue.ProjectId,
            issue.Project?.Name,
            issue.MilestoneId,
            issue.Milestone?.Name,
            issue.ParentId,
            issue.Parent is null ? null : $"{issue.Team.Key}-{issue.Parent.Number}",
            issue.Estimate,
            issue.DueDate,
            issue.SortOrder,
            issue.Labels.Select(l => ToLabel(l.Label)).ToList(),
            children,
            relations,
            issue.Attachments.Select(ToAttachment).ToList(),
            commentCount,
            issue.CreatedAt,
            issue.UpdatedAt,
            issue.StartedAt,
            issue.CompletedAt,
            issue.CanceledAt,
            issue.ArchivedAt);
    }
}
