using Microsoft.EntityFrameworkCore;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Realtime;
using Planner.Contracts.Enums;
using Planner.Contracts.Issues;
using Planner.Contracts.Realtime;
using Planner.Domain.Entities;
using Planner.Infrastructure;

namespace Planner.Api.Endpoints;

/// <summary>Creates, edits and moves issues. The one place that does, so a change made through the REST
/// API and one made by an assistant over MCP are permission-checked, validated, audited and broadcast
/// the same way.</summary>
public sealed class IssueCommands(
    PlannerDbContext db,
    ITeamAccess access,
    CurrentUser current,
    IIssueNumberGenerator numbers,
    IActivityLog activity,
    IRealtimeNotifier notifier)
{
    public Task<WriteResult<IssueSummary>> UpdateAsync(Guid id, UpdateIssueRequest request, CancellationToken ct) =>
        IssueEndpoints.ApplyUpdateAsync(id, request, db, access, activity, notifier, ct);

    public Task<WriteResult<IssueSummary>> MoveAsync(Guid id, MoveIssueRequest request, CancellationToken ct) =>
        IssueEndpoints.ApplyMoveAsync(id, request, db, access, activity, notifier, ct);

    public async Task<WriteResult<IssueSummary>> CreateAsync(CreateIssueRequest request, CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, request.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return Fail(denied);
        }

        var validation = new Validation()
            .Required(request.Title, "title")
            .MaxLength(request.Title, 500, "title")
            .Range(request.Estimate, 0, 1000, "estimate");

        if (validation.HasErrors)
        {
            return Fail(validation.ToResult());
        }

        var state = request.StateId is { } stateId
            ? await db.WorkflowStates.FirstOrDefaultAsync(s => s.Id == stateId && s.TeamId == request.TeamId, ct)
            : await db.WorkflowStates
                .Where(s => s.TeamId == request.TeamId)
                .OrderByDescending(s => s.IsDefault)
                .ThenBy(s => s.Rank)
                .FirstOrDefaultAsync(ct);

        if (state is null)
        {
            return Fail(ApiResults.BadRequest("That workflow state does not belong to this team."));
        }

        if (await IssueEndpoints.ValidateLinksAsync(db, request.TeamId, request.ProjectId, request.MilestoneId,
                request.ParentId, request.AssigneeId, ct) is { } linkError)
        {
            return Fail(linkError);
        }

        if (request.ParentId is { } newParent && await IssueEndpoints.IsArchivedAsync(db, newParent, ct))
        {
            return Fail(ApiResults.BadRequest("Sub-issues cannot be added to an archived issue."));
        }

        var labelIds = request.LabelIds ?? [];
        if (labelIds.Count > 0 &&
            await IssueEndpoints.CountUsableLabelsAsync(db, request.TeamId, labelIds, ct) != labelIds.Count)
        {
            return Fail(ApiResults.BadRequest("One or more labels do not exist or belong to another team."));
        }

        var rank = await Ranks.AppendAsync(
            db.Issues.Where(i => i.TeamId == request.TeamId && i.StateId == state.Id).Select(i => i.Rank), ct);

        var issue = new Issue
        {
            TeamId = request.TeamId,
            Number = await numbers.NextAsync(request.TeamId, ct),
            Title = request.Title.Trim(),
            Description = request.Description,
            StateId = state.Id,
            Priority = request.Priority,
            AssigneeId = request.AssigneeId,
            CreatorId = current.Id,
            ProjectId = request.ProjectId,
            MilestoneId = request.MilestoneId,
            ParentId = request.ParentId,
            Estimate = request.Estimate,
            DueDate = request.DueDate,
            Rank = rank,
            StartedAt = state.Type == WorkflowStateType.Started ? DateTimeOffset.UtcNow : null,
            CompletedAt = state.Type == WorkflowStateType.Completed ? DateTimeOffset.UtcNow : null,
            CanceledAt = state.Type == WorkflowStateType.Canceled ? DateTimeOffset.UtcNow : null
        };

        db.Issues.Add(issue);

        foreach (var labelId in labelIds)
        {
            db.IssueLabels.Add(new IssueLabel { IssueId = issue.Id, LabelId = labelId });
        }

        activity.Record(EntityTypes.Issue, issue.Id, ActivityActions.Created, new { title = issue.Title },
            teamId: issue.TeamId, projectId: issue.ProjectId, issueId: issue.Id);

        await db.SaveChangesAsync(ct);

        var summary = await IssueEndpoints.SummaryAsync(db, issue.Id, ct);
        await notifier.IssueChanged(ChangeKind.Created, summary);

        // Built from `state` rather than `RollupOf`: the issue was constructed, not loaded, so its
        // State navigation is not populated. A new issue is never archived.
        await IssueEndpoints.PublishRollupsAsync(db, notifier, issue.TeamId, null,
            new IssueEndpoints.Rollup(issue.ProjectId, issue.MilestoneId, state.Type, Archived: false), ct);

        return WriteResult<IssueSummary>.Succeeded(summary);
    }

    private static WriteResult<IssueSummary> Fail(IResult error) => WriteResult<IssueSummary>.Failed(error);
}
