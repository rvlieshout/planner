using Microsoft.EntityFrameworkCore;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Realtime;
using Planner.Contracts.Common;
using Planner.Contracts.Issues;
using Planner.Contracts.Realtime;
using Planner.Domain.Entities;
using Planner.Contracts.Enums;
using Planner.Infrastructure;

namespace Planner.Api.Endpoints;

public static class IssueEndpoints
{
    public static IEndpointRouteBuilder MapIssueEndpoints(this IEndpointRouteBuilder app)
    {
        var issues = app.MapGroup("/api/v1/issues").WithTags("Issues");

        issues.MapGet("/", ListAsync)
            .WithSummary("Filter issues across every team the caller can read");

        issues.MapGet("/{id:guid}", GetAsync).WithSummary("An issue with sub-issues, relations and attachments");
        issues.MapGet("/by-key/{key}", GetByKeyAsync).WithSummary("Look an issue up by its human key, e.g. ENG-42");

        issues.MapPost("/", CreateAsync).WithSummary("Create an issue");
        issues.MapPatch("/{id:guid}", UpdateAsync).WithSummary("Update an issue");
        issues.MapPost("/{id:guid}/move", MoveAsync).WithSummary("Move an issue between board columns or ranks");
        issues.MapPost("/{id:guid}/archive", ArchiveAsync).WithSummary("Archive an issue");
        issues.MapPost("/{id:guid}/restore", RestoreAsync).WithSummary("Restore an archived issue");
        issues.MapDelete("/{id:guid}", DeleteAsync).WithSummary("Delete an issue permanently");

        issues.MapGet("/{id:guid}/comments", ListCommentsAsync).WithSummary("Comments on an issue");
        issues.MapPost("/{id:guid}/comments", CreateCommentAsync).WithSummary("Comment on an issue");

        issues.MapPost("/{id:guid}/attachments", CreateAttachmentAsync).WithSummary("Attach a file reference or link");
        issues.MapPost("/{id:guid}/relations", CreateRelationAsync).WithSummary("Relate this issue to another");
        issues.MapDelete("/{id:guid}/relations/{relationId:guid}", DeleteRelationAsync).WithSummary("Remove a relation");
        issues.MapGet("/{id:guid}/activity", ListIssueActivityAsync).WithSummary("Audit trail for one issue");

        var comments = app.MapGroup("/api/v1/comments").WithTags("Comments");
        comments.MapPatch("/{commentId:guid}", UpdateCommentAsync).WithSummary("Edit your own comment");
        comments.MapDelete("/{commentId:guid}", DeleteCommentAsync).WithSummary("Delete a comment");

        app.MapDelete("/api/v1/attachments/{attachmentId:guid}", DeleteAttachmentAsync)
            .WithTags("Attachments")
            .WithSummary("Remove an attachment reference");

        app.MapGet("/api/v1/activity", ListActivityAsync)
            .WithTags("Activity")
            .WithSummary("Organisation-wide audit feed, filtered by team, project or time");

        return app;
    }

    private static async Task<IResult> ListAsync(
        PlannerDbContext db,
        ITeamAccess access,
        [AsParameters] IssueFilter filter,
        [AsParameters] PageQuery paging,
        CancellationToken ct)
    {
        var readable = await access.ReadableTeamIdsAsync(ct);

        var query = db.Issues.AsNoTracking().Where(i => readable.Contains(i.TeamId));

        if (filter.TeamId is { } teamId)
        {
            query = query.Where(i => i.TeamId == teamId);
        }

        if (filter.ProjectId is { } projectId)
        {
            query = query.Where(i => i.ProjectId == projectId);
        }

        if (filter.MilestoneId is { } milestoneId)
        {
            query = query.Where(i => i.MilestoneId == milestoneId);
        }

        if (filter.ParentId is { } parentId)
        {
            query = query.Where(i => i.ParentId == parentId);
        }
        else if (filter.TopLevelOnly)
        {
            query = query.Where(i => i.ParentId == null);
        }

        if (filter.StateId is { Length: > 0 } stateIds)
        {
            query = query.Where(i => stateIds.Contains(i.StateId));
        }

        if (filter.StateType is { Length: > 0 } stateTypes)
        {
            query = query.Where(i => stateTypes.Contains(i.State.Type));
        }

        if (filter.Unassigned == true)
        {
            query = query.Where(i => i.AssigneeId == null);
        }
        else if (filter.AssigneeId is { Length: > 0 } assignees)
        {
            query = query.Where(i => i.AssigneeId != null && assignees.Contains(i.AssigneeId.Value));
        }

        if (filter.LabelId is { Length: > 0 } labels)
        {
            // Every requested label must be present, which is what a label filter on a board means.
            foreach (var labelId in labels)
            {
                query = query.Where(i => i.Labels.Any(l => l.LabelId == labelId));
            }
        }

        if (filter.Priority is { Length: > 0 } priorities)
        {
            query = query.Where(i => priorities.Contains(i.Priority));
        }

        if (!filter.IncludeArchived)
        {
            query = query.Where(i => i.ArchivedAt == null);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();

            // "ENG-42" in the search box means "take me to that issue", not "find these words".
            var separator = search.LastIndexOf('-');
            if (separator > 0 && int.TryParse(search[(separator + 1)..], out var number))
            {
                var key = search[..separator].ToUpperInvariant();
                query = query.Where(i => i.Team.Key == key && i.Number == number);
            }
            else
            {
                var pattern = $"%{search}%";
                query = query.Where(i =>
                    EF.Functions.ILike(i.Title, pattern) ||
                    (i.Description != null && EF.Functions.ILike(i.Description, pattern)));
            }
        }

        if (filter.DueBefore is { } dueBefore)
        {
            query = query.Where(i => i.DueDate != null && i.DueDate <= dueBefore);
        }

        if (filter.DueAfter is { } dueAfter)
        {
            query = query.Where(i => i.DueDate != null && i.DueDate >= dueAfter);
        }

        if (filter.UpdatedSince is { } since)
        {
            query = query.Where(i => i.UpdatedAt > since);
        }

        query = ApplySort(query, filter.Sort);

        var total = await query.LongCountAsync(ct);
        var items = await query
            .Skip(paging.Skip)
            .Take(paging.NormalizedSize)
            .Select(Mapping.IssueSummaryProjection)
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<IssueSummary>(items, paging.NormalizedPage, paging.NormalizedSize, total));
    }

    /// <summary>Whitelisted sorts only. A client-supplied column name reaching the query builder is how
    /// injection bugs happen, and an unindexed sort is how a board view times out.</summary>
    private static IQueryable<Issue> ApplySort(IQueryable<Issue> query, string? sort) => sort switch
    {
        "board" => query.OrderBy(i => i.State.Position).ThenBy(i => i.SortOrder),
        "priority" => query.OrderBy(i => i.Priority == IssuePriority.None).ThenBy(i => i.Priority)
            .ThenBy(i => i.SortOrder),
        "dueDate" => query.OrderBy(i => i.DueDate == null).ThenBy(i => i.DueDate),
        "createdAt" => query.OrderBy(i => i.CreatedAt),
        "-createdAt" => query.OrderByDescending(i => i.CreatedAt),
        "updatedAt" => query.OrderBy(i => i.UpdatedAt),
        "number" => query.OrderBy(i => i.TeamId).ThenBy(i => i.Number),
        "sortOrder" => query.OrderBy(i => i.SortOrder),
        _ => query.OrderByDescending(i => i.UpdatedAt)
    };

    private static async Task<IResult> GetAsync(Guid id, PlannerDbContext db, ITeamAccess access, CancellationToken ct)
    {
        var issue = await LoadDetailAsync(db, i => i.Id == id, ct);
        if (issue is null)
        {
            return ApiResults.NotFound("That issue");
        }

        if (await ApiResults.RequireTeamAsync(access, issue.TeamId, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        return Results.Ok(await BuildDetailAsync(db, issue, ct));
    }

    private static async Task<IResult> GetByKeyAsync(
        string key,
        PlannerDbContext db,
        ITeamAccess access,
        CancellationToken ct)
    {
        var separator = key.LastIndexOf('-');
        if (separator <= 0 || !int.TryParse(key[(separator + 1)..], out var number))
        {
            return ApiResults.BadRequest("An issue key looks like ENG-42.");
        }

        var teamKey = key[..separator].ToUpperInvariant();
        var issue = await LoadDetailAsync(db, i => i.Team.Key == teamKey && i.Number == number, ct);

        if (issue is null)
        {
            return ApiResults.NotFound("That issue");
        }

        if (await ApiResults.RequireTeamAsync(access, issue.TeamId, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        return Results.Ok(await BuildDetailAsync(db, issue, ct));
    }

    /// <summary>Sub-issues and the comment count are projected in SQL rather than pulled through the
    /// include graph, which keeps a busy issue's payload proportional to what the detail pane shows.</summary>
    private static async Task<Contracts.Issues.IssueDetail> BuildDetailAsync(
        PlannerDbContext db,
        Issue issue,
        CancellationToken ct)
    {
        var children = await db.Issues.AsNoTracking()
            .Where(i => i.ParentId == issue.Id)
            .OrderBy(i => i.SortOrder)
            .Select(Mapping.IssueSummaryProjection)
            .ToListAsync(ct);

        var commentCount = await db.Comments.CountAsync(c => c.IssueId == issue.Id, ct);

        return Mapping.ToIssueDetail(issue, children, commentCount);
    }

    private static Task<Issue?> LoadDetailAsync(
        PlannerDbContext db,
        System.Linq.Expressions.Expression<Func<Issue, bool>> predicate,
        CancellationToken ct) =>
        db.Issues.AsNoTracking()
            .Include(i => i.Team)
            .Include(i => i.State)
            .Include(i => i.Assignee)
            .Include(i => i.Creator)
            .Include(i => i.Project)
            .Include(i => i.Milestone)
            .Include(i => i.Parent)
            .Include(i => i.Labels).ThenInclude(l => l.Label)
            .Include(i => i.Attachments).ThenInclude(a => a.UploadedBy)
            .Include(i => i.OutgoingRelations).ThenInclude(r => r.TargetIssue).ThenInclude(t => t.Team)
            .Include(i => i.OutgoingRelations).ThenInclude(r => r.TargetIssue).ThenInclude(t => t.State)
            .Include(i => i.IncomingRelations).ThenInclude(r => r.SourceIssue).ThenInclude(t => t.Team)
            .Include(i => i.IncomingRelations).ThenInclude(r => r.SourceIssue).ThenInclude(t => t.State)
            .AsSplitQuery()
            .FirstOrDefaultAsync(predicate, ct);

    private static async Task<IResult> CreateAsync(
        CreateIssueRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        IIssueNumberGenerator numbers,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, request.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        var validation = new Validation()
            .Required(request.Title, "title")
            .MaxLength(request.Title, 500, "title")
            .Range(request.Estimate, 0, 1000, "estimate");

        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        var state = request.StateId is { } stateId
            ? await db.WorkflowStates.FirstOrDefaultAsync(s => s.Id == stateId && s.TeamId == request.TeamId, ct)
            : await db.WorkflowStates
                .Where(s => s.TeamId == request.TeamId)
                .OrderByDescending(s => s.IsDefault)
                .ThenBy(s => s.Position)
                .FirstOrDefaultAsync(ct);

        if (state is null)
        {
            return ApiResults.BadRequest("That workflow state does not belong to this team.");
        }

        if (await ValidateLinksAsync(db, request.TeamId, request.ProjectId, request.MilestoneId, request.ParentId,
                request.AssigneeId, ct) is { } linkError)
        {
            return linkError;
        }

        var labelIds = request.LabelIds ?? [];
        if (labelIds.Count > 0 && await CountUsableLabelsAsync(db, request.TeamId, labelIds, ct) != labelIds.Count)
        {
            return ApiResults.BadRequest("One or more labels do not exist or belong to another team.");
        }

        var maxSort = await db.Issues
            .Where(i => i.TeamId == request.TeamId && i.StateId == state.Id)
            .MaxAsync(i => (double?)i.SortOrder, ct);

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
            SortOrder = (maxSort ?? 0) + 1000,
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

        var summary = await SummaryAsync(db, issue.Id, ct);
        await notifier.IssueChanged(ChangeKind.Created, summary);
        return Results.Created($"/api/v1/issues/{issue.Id}", summary);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateIssueRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var issue = await db.Issues.Include(i => i.State).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (issue is null)
        {
            return ApiResults.NotFound("That issue");
        }

        if (await ApiResults.RequireTeamAsync(access, issue.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        var validation = new Validation();
        if (request.Title.TryGet(out var title))
        {
            validation.Required(title, "title").MaxLength(title, 500, "title");
        }

        if (request.Estimate.TryGet(out var estimate))
        {
            validation.Range(estimate, 0, 1000, "estimate");
        }

        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        var projectId = request.ProjectId.Or(issue.ProjectId);
        var milestoneId = request.MilestoneId.Or(issue.MilestoneId);
        var parentId = request.ParentId.Or(issue.ParentId);
        var assigneeId = request.AssigneeId.Or(issue.AssigneeId);

        if (parentId == issue.Id)
        {
            return ApiResults.BadRequest("An issue cannot be its own parent.");
        }

        if (await ValidateLinksAsync(db, issue.TeamId, projectId, milestoneId, parentId, assigneeId, ct)
            is { } linkError)
        {
            return linkError;
        }

        // Moving to a different project silently drops a milestone from the old one.
        if (projectId != issue.ProjectId && milestoneId == issue.MilestoneId)
        {
            milestoneId = null;
        }

        if (request.StateId.TryGet(out var newStateId) && newStateId != issue.StateId)
        {
            var state = await db.WorkflowStates.FirstOrDefaultAsync(s => s.Id == newStateId && s.TeamId == issue.TeamId, ct);
            if (state is null)
            {
                return ApiResults.BadRequest("That workflow state does not belong to this team.");
            }

            activity.Record(EntityTypes.Issue, issue.Id, ActivityActions.StateChanged,
                new { from = issue.State.Name, to = state.Name },
                teamId: issue.TeamId, projectId: issue.ProjectId, issueId: issue.Id);

            ApplyStateTransition(issue, state);
        }

        if (assigneeId != issue.AssigneeId)
        {
            activity.Record(EntityTypes.Issue, issue.Id, ActivityActions.AssigneeChanged,
                new { from = issue.AssigneeId, to = assigneeId },
                teamId: issue.TeamId, projectId: issue.ProjectId, issueId: issue.Id);
        }

        if (request.Priority.TryGet(out var priority) && priority != issue.Priority)
        {
            activity.Record(EntityTypes.Issue, issue.Id, ActivityActions.PriorityChanged,
                new { from = issue.Priority.ToString(), to = priority.ToString() },
                teamId: issue.TeamId, projectId: issue.ProjectId, issueId: issue.Id);
        }

        issue.Title = request.Title.Or(issue.Title)!;
        issue.Description = request.Description.Or(issue.Description);
        issue.Priority = request.Priority.Or(issue.Priority);
        issue.AssigneeId = assigneeId;
        issue.ProjectId = projectId;
        issue.MilestoneId = milestoneId;
        issue.ParentId = parentId;
        issue.Estimate = request.Estimate.Or(issue.Estimate);
        issue.DueDate = request.DueDate.Or(issue.DueDate);
        issue.SortOrder = request.SortOrder.Or(issue.SortOrder);

        if (request.LabelIds.TryGet(out var labelIds) && labelIds is not null)
        {
            if (await CountUsableLabelsAsync(db, issue.TeamId, labelIds, ct) != labelIds.Count)
            {
                return ApiResults.BadRequest("One or more labels do not exist or belong to another team.");
            }

            var existing = await db.IssueLabels.Where(l => l.IssueId == issue.Id).ToListAsync(ct);
            db.IssueLabels.RemoveRange(existing.Where(l => !labelIds.Contains(l.LabelId)));

            foreach (var labelId in labelIds.Where(l => existing.All(e => e.LabelId != l)))
            {
                db.IssueLabels.Add(new IssueLabel { IssueId = issue.Id, LabelId = labelId });
            }

            activity.Record(EntityTypes.Issue, issue.Id, ActivityActions.LabelsChanged, new { labelIds },
                teamId: issue.TeamId, projectId: issue.ProjectId, issueId: issue.Id);
        }

        await db.SaveChangesAsync(ct);

        var summary = await SummaryAsync(db, issue.Id, ct);
        await notifier.IssueChanged(ChangeKind.Updated, summary);
        return Results.Ok(summary);
    }

    private static async Task<IResult> MoveAsync(
        Guid id,
        MoveIssueRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var issue = await db.Issues.Include(i => i.State).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (issue is null)
        {
            return ApiResults.NotFound("That issue");
        }

        if (await ApiResults.RequireTeamAsync(access, issue.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        if (request.StateId is { } stateId && stateId != issue.StateId)
        {
            var state = await db.WorkflowStates.FirstOrDefaultAsync(s => s.Id == stateId && s.TeamId == issue.TeamId, ct);
            if (state is null)
            {
                return ApiResults.BadRequest("That workflow state does not belong to this team.");
            }

            activity.Record(EntityTypes.Issue, issue.Id, ActivityActions.StateChanged,
                new { from = issue.State.Name, to = state.Name },
                teamId: issue.TeamId, projectId: issue.ProjectId, issueId: issue.Id);

            ApplyStateTransition(issue, state);
        }

        issue.SortOrder = await ResolveSortOrderAsync(db, issue, request, ct);

        await db.SaveChangesAsync(ct);

        var summary = await SummaryAsync(db, issue.Id, ct);
        await notifier.IssueChanged(ChangeKind.Updated, summary);
        return Results.Ok(summary);
    }

    /// <summary>Fractional ranking: an issue dropped between two neighbours takes the midpoint of their
    /// ranks, so a reorder writes one row instead of renumbering the whole column.</summary>
    private static async Task<double> ResolveSortOrderAsync(
        PlannerDbContext db,
        Issue issue,
        MoveIssueRequest request,
        CancellationToken ct)
    {
        const double Gap = 1000d;

        var after = request.AfterIssueId is { } afterId
            ? await db.Issues.Where(i => i.Id == afterId).Select(i => (double?)i.SortOrder).FirstOrDefaultAsync(ct)
            : null;

        var before = request.BeforeIssueId is { } beforeId
            ? await db.Issues.Where(i => i.Id == beforeId).Select(i => (double?)i.SortOrder).FirstOrDefaultAsync(ct)
            : null;

        if (after is { } a && before is { } b)
        {
            return (a + b) / 2;
        }

        if (after is { } onlyAfter)
        {
            return onlyAfter + Gap;
        }

        if (before is { } onlyBefore)
        {
            return onlyBefore - Gap;
        }

        if (request.SortOrder is { } explicitOrder)
        {
            return explicitOrder;
        }

        // No anchors: drop it at the end of the target column.
        var max = await db.Issues
            .Where(i => i.TeamId == issue.TeamId && i.StateId == issue.StateId && i.Id != issue.Id)
            .MaxAsync(i => (double?)i.SortOrder, ct);

        return (max ?? 0) + Gap;
    }

    /// <summary>Keeps the lifecycle timestamps consistent with the state's semantic type, so reports do
    /// not have to guess what "done" means for a team that renamed its columns.</summary>
    private static void ApplyStateTransition(Issue issue, WorkflowState state)
    {
        issue.StateId = state.Id;
        issue.State = state;

        switch (state.Type)
        {
            case WorkflowStateType.Started:
                issue.StartedAt ??= DateTimeOffset.UtcNow;
                issue.CompletedAt = null;
                issue.CanceledAt = null;
                break;

            case WorkflowStateType.Completed:
                issue.StartedAt ??= DateTimeOffset.UtcNow;
                issue.CompletedAt = DateTimeOffset.UtcNow;
                issue.CanceledAt = null;
                break;

            case WorkflowStateType.Canceled:
                issue.CanceledAt = DateTimeOffset.UtcNow;
                issue.CompletedAt = null;
                break;

            default:
                issue.CompletedAt = null;
                issue.CanceledAt = null;
                break;
        }
    }

    private static Task<IResult> ArchiveAsync(
        Guid id, PlannerDbContext db, ITeamAccess access, IActivityLog activity, IRealtimeNotifier notifier,
        CancellationToken ct) => SetArchivedAsync(id, db, access, activity, notifier, DateTimeOffset.UtcNow, ct);

    private static Task<IResult> RestoreAsync(
        Guid id, PlannerDbContext db, ITeamAccess access, IActivityLog activity, IRealtimeNotifier notifier,
        CancellationToken ct) => SetArchivedAsync(id, db, access, activity, notifier, null, ct);

    private static async Task<IResult> SetArchivedAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        DateTimeOffset? archivedAt,
        CancellationToken ct)
    {
        var issue = await db.Issues.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (issue is null)
        {
            return ApiResults.NotFound("That issue");
        }

        if (await ApiResults.RequireTeamAsync(access, issue.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        issue.ArchivedAt = archivedAt;
        activity.Record(EntityTypes.Issue, issue.Id,
            archivedAt is null ? ActivityActions.Restored : ActivityActions.Archived, null,
            teamId: issue.TeamId, projectId: issue.ProjectId, issueId: issue.Id);

        await db.SaveChangesAsync(ct);

        var summary = await SummaryAsync(db, issue.Id, ct);
        await notifier.IssueChanged(archivedAt is null ? ChangeKind.Restored : ChangeKind.Archived, summary);
        return Results.Ok(summary);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var issue = await db.Issues.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (issue is null)
        {
            return ApiResults.NotFound("That issue");
        }

        // Archiving is the everyday action; destroying history needs team-lead authority.
        if (await ApiResults.RequireTeamAsync(access, issue.TeamId, TeamPermission.Administer, ct) is { } denied)
        {
            return denied;
        }

        var summary = await SummaryAsync(db, issue.Id, ct);

        // Audit rows outlive the issue, so drop the FK-free reference rather than blocking the delete.
        await db.ActivityEvents.Where(a => a.IssueId == id)
            .ExecuteUpdateAsync(a => a.SetProperty(x => x.IssueId, (Guid?)null), ct);

        db.Issues.Remove(issue);
        await db.SaveChangesAsync(ct);

        await notifier.IssueChanged(ChangeKind.Deleted, summary);
        return Results.NoContent();
    }

    private static async Task<IResult> ListCommentsAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        [AsParameters] PageQuery paging,
        CancellationToken ct)
    {
        var teamId = await db.Issues.Where(i => i.Id == id).Select(i => (Guid?)i.TeamId).FirstOrDefaultAsync(ct);
        if (teamId is null)
        {
            return ApiResults.NotFound("That issue");
        }

        if (await ApiResults.RequireTeamAsync(access, teamId.Value, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        var query = db.Comments.AsNoTracking().Where(c => c.IssueId == id);
        var total = await query.LongCountAsync(ct);

        var items = await query
            .OrderBy(c => c.CreatedAt)
            .Skip(paging.Skip)
            .Take(paging.NormalizedSize)
            .Select(Mapping.CommentProjection)
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<CommentDto>(items, paging.NormalizedPage, paging.NormalizedSize, total));
    }

    private static async Task<IResult> CreateCommentAsync(
        Guid id,
        CreateCommentRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var issue = await db.Issues.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (issue is null)
        {
            return ApiResults.NotFound("That issue");
        }

        // Commenting is the one write a guest is allowed.
        if (await ApiResults.RequireTeamAsync(access, issue.TeamId, TeamPermission.Comment, ct) is { } denied)
        {
            return denied;
        }

        var validation = new Validation().Required(request.Body, "body");
        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        if (request.ParentCommentId is { } parentId &&
            !await db.Comments.AnyAsync(c => c.Id == parentId && c.IssueId == id, ct))
        {
            return ApiResults.BadRequest("The comment you are replying to is not on this issue.");
        }

        var comment = new Comment
        {
            IssueId = id,
            AuthorId = current.Id,
            Body = request.Body,
            ParentCommentId = request.ParentCommentId
        };

        db.Comments.Add(comment);
        activity.Record(EntityTypes.Comment, comment.Id, ActivityActions.Commented, null,
            teamId: issue.TeamId, projectId: issue.ProjectId, issueId: id);

        await db.SaveChangesAsync(ct);

        var dto = await db.Comments.AsNoTracking().Where(c => c.Id == comment.Id)
            .Select(Mapping.CommentProjection).FirstAsync(ct);

        await notifier.CommentChanged(ChangeKind.Created, dto, issue.TeamId);
        return Results.Created($"/api/v1/comments/{comment.Id}", dto);
    }

    private static async Task<IResult> UpdateCommentAsync(
        Guid commentId,
        UpdateCommentRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var comment = await db.Comments.Include(c => c.Issue).FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (comment is null)
        {
            return ApiResults.NotFound("That comment");
        }

        if (await ApiResults.RequireTeamAsync(access, comment.Issue.TeamId, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        // Editing someone else's words is not an administrative power.
        if (comment.AuthorId != current.Id)
        {
            return ApiResults.Forbidden("You can only edit your own comments.");
        }

        var validation = new Validation().Required(request.Body, "body");
        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        comment.Body = request.Body;
        comment.EditedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        var dto = await db.Comments.AsNoTracking().Where(c => c.Id == commentId)
            .Select(Mapping.CommentProjection).FirstAsync(ct);

        await notifier.CommentChanged(ChangeKind.Updated, dto, comment.Issue.TeamId);
        return Results.Ok(dto);
    }

    private static async Task<IResult> DeleteCommentAsync(
        Guid commentId,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var comment = await db.Comments.Include(c => c.Issue).FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (comment is null)
        {
            return ApiResults.NotFound("That comment");
        }

        var teamId = comment.Issue.TeamId;
        var permission = await access.GetPermissionAsync(teamId, ct);

        if (permission == TeamPermission.None)
        {
            return ApiResults.NotFound("That comment");
        }

        // Authors delete their own; team leads and admins can moderate anyone's.
        if (comment.AuthorId != current.Id && permission < TeamPermission.Administer)
        {
            return ApiResults.Forbidden("Only the author or a team lead can delete this comment.");
        }

        var dto = await db.Comments.AsNoTracking().Where(c => c.Id == commentId)
            .Select(Mapping.CommentProjection).FirstAsync(ct);

        db.Comments.Remove(comment);
        await db.SaveChangesAsync(ct);

        await notifier.CommentChanged(ChangeKind.Deleted, dto, teamId);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateAttachmentAsync(
        Guid id,
        CreateAttachmentRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var issue = await db.Issues.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (issue is null)
        {
            return ApiResults.NotFound("That issue");
        }

        if (await ApiResults.RequireTeamAsync(access, issue.TeamId, TeamPermission.Comment, ct) is { } denied)
        {
            return denied;
        }

        var validation = new Validation()
            .Required(request.FileName, "fileName")
            .MaxLength(request.FileName, 300, "fileName")
            .Required(request.StorageUri, "storageUri")
            .MaxLength(request.StorageUri, 2000, "storageUri");

        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        var attachment = new Attachment
        {
            IssueId = id,
            FileName = request.FileName,
            ContentType = request.ContentType,
            SizeBytes = request.SizeBytes,
            StorageUri = request.StorageUri,
            UploadedById = current.Id
        };

        db.Attachments.Add(attachment);
        activity.Record(EntityTypes.Attachment, attachment.Id, ActivityActions.AttachmentAdded,
            new { fileName = attachment.FileName },
            teamId: issue.TeamId, projectId: issue.ProjectId, issueId: id);

        await db.SaveChangesAsync(ct);

        var dto = await db.Attachments.AsNoTracking().Where(a => a.Id == attachment.Id)
            .Select(Mapping.AttachmentProjection).FirstAsync(ct);

        await notifier.AttachmentChanged(ChangeKind.Created, dto, issue.TeamId);
        return Results.Created($"/api/v1/attachments/{attachment.Id}", dto);
    }

    private static async Task<IResult> DeleteAttachmentAsync(
        Guid attachmentId,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var attachment = await db.Attachments.Include(a => a.Issue).FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null)
        {
            return ApiResults.NotFound("That attachment");
        }

        var teamId = attachment.Issue.TeamId;
        var permission = await access.GetPermissionAsync(teamId, ct);

        if (permission == TeamPermission.None)
        {
            return ApiResults.NotFound("That attachment");
        }

        if (attachment.UploadedById != current.Id && permission < TeamPermission.Write)
        {
            return ApiResults.Forbidden("Only the uploader or a team member can remove this attachment.");
        }

        var dto = await db.Attachments.AsNoTracking().Where(a => a.Id == attachmentId)
            .Select(Mapping.AttachmentProjection).FirstAsync(ct);

        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(ct);

        await notifier.AttachmentChanged(ChangeKind.Deleted, dto, teamId);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateRelationAsync(
        Guid id,
        CreateIssueRelationRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var issue = await db.Issues.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (issue is null)
        {
            return ApiResults.NotFound("That issue");
        }

        if (await ApiResults.RequireTeamAsync(access, issue.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        if (request.TargetIssueId == id)
        {
            return ApiResults.BadRequest("An issue cannot be related to itself.");
        }

        var target = await db.Issues.AsNoTracking()
            .Include(i => i.Team)
            .Include(i => i.State)
            .FirstOrDefaultAsync(i => i.Id == request.TargetIssueId, ct);

        if (target is null)
        {
            return ApiResults.NotFound("The target issue");
        }

        // Cross-team relations are allowed, but only between teams the caller can actually see.
        if (await ApiResults.RequireTeamAsync(access, target.TeamId, TeamPermission.Read, ct) is { } targetDenied)
        {
            return targetDenied;
        }

        if (await db.IssueRelations.AnyAsync(
                r => r.SourceIssueId == id && r.TargetIssueId == request.TargetIssueId && r.Type == request.Type, ct))
        {
            return ApiResults.Conflict("These issues are already related in that way.");
        }

        var relation = new IssueRelation
        {
            SourceIssueId = id,
            TargetIssueId = request.TargetIssueId,
            Type = request.Type
        };

        db.IssueRelations.Add(relation);
        activity.Record(EntityTypes.IssueRelation, relation.Id, ActivityActions.RelationAdded,
            new { type = request.Type.ToString(), target = request.TargetIssueId },
            teamId: issue.TeamId, projectId: issue.ProjectId, issueId: id);

        await db.SaveChangesAsync(ct);

        var dto = new IssueRelationDto(
            relation.Id,
            relation.Type,
            true,
            target.Id,
            $"{target.Team.Key}-{target.Number}",
            target.Title,
            target.State.Type);

        await notifier.IssueRelationChanged(ChangeKind.Created, dto, id, issue.TeamId);
        return Results.Created($"/api/v1/issues/{id}/relations/{relation.Id}", dto);
    }

    private static async Task<IResult> DeleteRelationAsync(
        Guid id,
        Guid relationId,
        PlannerDbContext db,
        ITeamAccess access,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var relation = await db.IssueRelations
            .Include(r => r.SourceIssue)
            .Include(r => r.TargetIssue).ThenInclude(t => t.Team)
            .Include(r => r.TargetIssue).ThenInclude(t => t.State)
            .FirstOrDefaultAsync(r => r.Id == relationId, ct);

        if (relation is null || (relation.SourceIssueId != id && relation.TargetIssueId != id))
        {
            return ApiResults.NotFound("That relation");
        }

        if (await ApiResults.RequireTeamAsync(access, relation.SourceIssue.TeamId, TeamPermission.Write, ct)
            is { } denied)
        {
            return denied;
        }

        var dto = new IssueRelationDto(
            relation.Id,
            relation.Type,
            relation.SourceIssueId == id,
            relation.TargetIssueId,
            $"{relation.TargetIssue.Team.Key}-{relation.TargetIssue.Number}",
            relation.TargetIssue.Title,
            relation.TargetIssue.State.Type);

        db.IssueRelations.Remove(relation);
        activity.Record(EntityTypes.IssueRelation, relation.Id, ActivityActions.RelationRemoved, null,
            teamId: relation.SourceIssue.TeamId, issueId: relation.SourceIssueId);

        await db.SaveChangesAsync(ct);

        await notifier.IssueRelationChanged(ChangeKind.Deleted, dto, id, relation.SourceIssue.TeamId);
        return Results.NoContent();
    }

    private static async Task<IResult> ListIssueActivityAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        [AsParameters] PageQuery paging,
        CancellationToken ct)
    {
        var teamId = await db.Issues.Where(i => i.Id == id).Select(i => (Guid?)i.TeamId).FirstOrDefaultAsync(ct);
        if (teamId is null)
        {
            return ApiResults.NotFound("That issue");
        }

        if (await ApiResults.RequireTeamAsync(access, teamId.Value, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        var query = db.ActivityEvents.AsNoTracking().Include(a => a.Actor).Where(a => a.IssueId == id);
        var total = await query.LongCountAsync(ct);

        var events = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip(paging.Skip)
            .Take(paging.NormalizedSize)
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<ActivityEventDto>(
            events.Select(Mapping.ToActivity).ToList(), paging.NormalizedPage, paging.NormalizedSize, total));
    }

    private static async Task<IResult> ListActivityAsync(
        PlannerDbContext db,
        ITeamAccess access,
        [AsParameters] PageQuery paging,
        Guid? teamId,
        Guid? projectId,
        DateTimeOffset? since,
        CancellationToken ct)
    {
        var readable = await access.ReadableTeamIdsAsync(ct);

        var query = db.ActivityEvents.AsNoTracking()
            .Include(a => a.Actor)
            .Where(a => a.TeamId != null && readable.Contains(a.TeamId!.Value));

        if (teamId is { } scopedTeam)
        {
            query = query.Where(a => a.TeamId == scopedTeam);
        }

        if (projectId is { } scopedProject)
        {
            query = query.Where(a => a.ProjectId == scopedProject);
        }

        if (since is { } from)
        {
            query = query.Where(a => a.CreatedAt > from);
        }

        var total = await query.LongCountAsync(ct);

        var events = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip(paging.Skip)
            .Take(paging.NormalizedSize)
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<ActivityEventDto>(
            events.Select(Mapping.ToActivity).ToList(), paging.NormalizedPage, paging.NormalizedSize, total));
    }

    /// <summary>Checks that every optional link on an issue points somewhere real and in-scope: a
    /// milestone from another project or a parent from another team would corrupt the hierarchy.</summary>
    private static async Task<IResult?> ValidateLinksAsync(
        PlannerDbContext db,
        Guid teamId,
        Guid? projectId,
        Guid? milestoneId,
        Guid? parentId,
        Guid? assigneeId,
        CancellationToken ct)
    {
        if (projectId is { } project && !await db.Projects.AnyAsync(p => p.Id == project && p.TeamId == teamId, ct))
        {
            return ApiResults.BadRequest("That project does not exist in this team.");
        }

        if (milestoneId is { } milestone)
        {
            if (projectId is null)
            {
                return ApiResults.BadRequest("An issue can only sit on a milestone if it belongs to the project.");
            }

            if (!await db.Milestones.AnyAsync(m => m.Id == milestone && m.ProjectId == projectId, ct))
            {
                return ApiResults.BadRequest("That milestone does not belong to the issue's project.");
            }
        }

        if (parentId is { } parent && !await db.Issues.AnyAsync(i => i.Id == parent && i.TeamId == teamId, ct))
        {
            return ApiResults.BadRequest("The parent issue does not exist in this team.");
        }

        if (assigneeId is { } assignee &&
            !await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.UserId == assignee, ct))
        {
            return ApiResults.BadRequest("You can only assign issues to members of the issue's team.");
        }

        return null;
    }

    private static async Task<int> CountUsableLabelsAsync(
        PlannerDbContext db,
        Guid teamId,
        IReadOnlyList<Guid> labelIds,
        CancellationToken ct) =>
        await db.Labels.CountAsync(l => labelIds.Contains(l.Id) && (l.TeamId == null || l.TeamId == teamId), ct);

    private static Task<IssueSummary> SummaryAsync(PlannerDbContext db, Guid id, CancellationToken ct) =>
        db.Issues.AsNoTracking().Where(i => i.Id == id).Select(Mapping.IssueSummaryProjection).FirstAsync(ct);
}
