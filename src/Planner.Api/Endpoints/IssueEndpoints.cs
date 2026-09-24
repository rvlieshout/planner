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
        app.MapIssueFiles();
        app.MapIssueSubscriptions();
        var issues = app.MapGroup("/api/v1/issues").WithTags("Issues");

        issues.MapGet("/", ListAsync)
            .WithSummary("Filter issues across every team the caller can read");

        issues.MapGet("/{id:b58}", GetAsync).WithSummary("An issue with sub-issues, relations and attachments");
        issues.MapGet("/by-key/{key}", GetByKeyAsync).WithSummary("Look an issue up by its human key, e.g. ENG-42");

        issues.MapPost("/", CreateAsync).WithSummary("Create an issue");
        issues.MapPatch("/{id:b58}", UpdateAsync).WithSummary("Update an issue");
        issues.MapPost("/{id:b58}/move", MoveAsync).WithSummary("Move an issue between board columns or ranks");
        issues.MapPost("/{id:b58}/archive", ArchiveAsync).WithSummary("Archive an issue");
        issues.MapPost("/{id:b58}/restore", RestoreAsync).WithSummary("Restore an archived issue");
        issues.MapDelete("/{id:b58}", DeleteAsync).WithSummary("Delete an issue permanently");

        issues.MapGet("/{id:b58}/comments", ListCommentsAsync).WithSummary("Comments on an issue");
        issues.MapPost("/{id:b58}/comments", CreateCommentAsync).WithSummary("Comment on an issue");

        issues.MapPost("/{id:b58}/attachments", CreateAttachmentAsync).WithSummary("Attach a file reference or link");
        issues.MapPost("/{id:b58}/relations", CreateRelationAsync).WithSummary("Relate this issue to another");
        issues.MapDelete("/{id:b58}/relations/{relationId:b58}", DeleteRelationAsync).WithSummary("Remove a relation");
        issues.MapGet("/{id:b58}/activity", ListIssueActivityAsync).WithSummary("Audit trail for one issue");

        var comments = app.MapGroup("/api/v1/comments").WithTags("Comments");
        comments.MapPatch("/{commentId:b58}", UpdateCommentAsync).WithSummary("Edit your own comment");
        comments.MapDelete("/{commentId:b58}", DeleteCommentAsync).WithSummary("Delete a comment");

        app.MapDelete("/api/v1/attachments/{attachmentId:b58}", DeleteAttachmentAsync)
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
        // Two concurrent drops into the same gap can mint the same rank; the number settles the tie the
        // same way the client does.
        "board" => query.OrderBy(i => i.State.Rank).ThenBy(i => i.Rank).ThenBy(i => i.Number),
        "priority" => query.OrderBy(i => i.Priority == IssuePriority.None).ThenBy(i => i.Priority)
            .ThenBy(i => i.Rank),
        "dueDate" => query.OrderBy(i => i.DueDate == null).ThenBy(i => i.DueDate),
        "createdAt" => query.OrderBy(i => i.CreatedAt),
        "-createdAt" => query.OrderByDescending(i => i.CreatedAt),
        "updatedAt" => query.OrderBy(i => i.UpdatedAt),
        "number" => query.OrderBy(i => i.TeamId).ThenBy(i => i.Number),
        // sortOrder is the name this sort had while ranks were numbers; existing callers keep working.
        "rank" or "sortOrder" => query.OrderBy(i => i.Rank).ThenBy(i => i.Number),
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
            .OrderBy(i => i.Rank)
            .ThenBy(i => i.Number)
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
                .ThenBy(s => s.Rank)
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

        var summary = await SummaryAsync(db, issue.Id, ct);
        await notifier.IssueChanged(ChangeKind.Created, summary);

        // Built from `state` rather than `RollupOf`: the issue was constructed, not loaded, so its
        // State navigation is not populated. A new issue is never archived.
        await PublishRollupsAsync(db, notifier, issue.TeamId, null,
            new Rollup(issue.ProjectId, issue.MilestoneId, state.Type, Archived: false), ct);

        return Results.Created($"/api/v1/issues/{issue.Id.ToBase58()}", summary);
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

        // Captured before any field is written, so the rollups the old project and milestone
        // reported can be republished alongside the new ones.
        var rollupBefore = RollupOf(issue);

        var validation = new Validation();
        if (request.Title.TryGet(out var title))
        {
            validation.Required(title, "title").MaxLength(title, 500, "title");
        }

        if (request.Estimate.TryGet(out var estimate))
        {
            validation.Range(estimate, 0, 1000, "estimate");
        }

        if (request.Rank.TryGet(out var rank))
        {
            validation.Required(rank, "rank").RankKey(rank, "rank");
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

        if (projectId != issue.ProjectId)
        {
            var names = await db.Projects.Where(p => p.Id == issue.ProjectId || p.Id == projectId)
                .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

            activity.Record(EntityTypes.Issue, issue.Id, ActivityActions.ProjectChanged,
                Relinked(issue.ProjectId, projectId, names),
                teamId: issue.TeamId, projectId: projectId, issueId: issue.Id);
        }

        if (milestoneId != issue.MilestoneId)
        {
            var names = await db.Milestones.Where(m => m.Id == issue.MilestoneId || m.Id == milestoneId)
                .ToDictionaryAsync(m => m.Id, m => m.Name, ct);

            activity.Record(EntityTypes.Issue, issue.Id, ActivityActions.MilestoneChanged,
                Relinked(issue.MilestoneId, milestoneId, names),
                teamId: issue.TeamId, projectId: projectId, issueId: issue.Id);
        }

        // Everything else that has no verb of its own is one "updated" event naming the fields, so the
        // history says the description changed without storing a second copy of it.
        var fields = new List<string>();
        var newTitle = request.Title.Or(issue.Title)!.Trim();
        if (newTitle != issue.Title) fields.Add("title");
        if (request.Description.Or(issue.Description) != issue.Description) fields.Add("description");
        if (request.Estimate.Or(issue.Estimate) != issue.Estimate) fields.Add("estimate");
        if (request.DueDate.Or(issue.DueDate) != issue.DueDate) fields.Add("dueDate");
        if (parentId != issue.ParentId) fields.Add("parent");

        if (fields.Count > 0)
        {
            activity.Record(EntityTypes.Issue, issue.Id, ActivityActions.Updated,
                new
                {
                    fields,
                    title = fields.Contains("title") ? new { from = issue.Title, to = newTitle } : null
                },
                teamId: issue.TeamId, projectId: projectId, issueId: issue.Id);
        }

        issue.Title = newTitle;
        issue.Description = request.Description.Or(issue.Description);
        issue.Priority = request.Priority.Or(issue.Priority);
        issue.AssigneeId = assigneeId;
        issue.ProjectId = projectId;
        issue.MilestoneId = milestoneId;
        issue.ParentId = parentId;
        issue.Estimate = request.Estimate.Or(issue.Estimate);
        issue.DueDate = request.DueDate.Or(issue.DueDate);
        issue.Rank = request.Rank.Or(issue.Rank)!;

        if (request.LabelIds.TryGet(out var labelIds) && labelIds is not null)
        {
            if (await CountUsableLabelsAsync(db, issue.TeamId, labelIds, ct) != labelIds.Count)
            {
                return ApiResults.BadRequest("One or more labels do not exist or belong to another team.");
            }

            var existing = await db.IssueLabels.Where(l => l.IssueId == issue.Id).ToListAsync(ct);
            var removed = existing.Where(l => !labelIds.Contains(l.LabelId)).ToList();
            var added = labelIds.Where(l => existing.All(e => e.LabelId != l)).Distinct().ToList();

            db.IssueLabels.RemoveRange(removed);

            foreach (var labelId in added)
            {
                db.IssueLabels.Add(new IssueLabel { IssueId = issue.Id, LabelId = labelId });
            }

            // A form that sends the labels it already had is not a change, and would otherwise land in
            // every follower's inbox as one.
            if (added.Count > 0 || removed.Count > 0)
            {
                activity.Record(EntityTypes.Issue, issue.Id, ActivityActions.LabelsChanged,
                    new { labelIds, added, removed = removed.Select(l => l.LabelId).ToList() },
                    teamId: issue.TeamId, projectId: issue.ProjectId, issueId: issue.Id);
            }
        }

        await db.SaveChangesAsync(ct);

        var summary = await SummaryAsync(db, issue.Id, ct);
        await notifier.IssueChanged(ChangeKind.Updated, summary);
        await PublishRollupsAsync(db, notifier, issue.TeamId, rollupBefore, RollupOf(issue), ct);
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

        var rollupBefore = RollupOf(issue);

        var validation = new Validation().RankKey(request.Rank, "rank");
        if (validation.HasErrors)
        {
            return validation.ToResult();
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

        issue.Rank = await ResolveRankAsync(db, issue, request, ct);

        await db.SaveChangesAsync(ct);

        var summary = await SummaryAsync(db, issue.Id, ct);
        await notifier.IssueChanged(ChangeKind.Updated, summary);

        // A drag within a column, or between two columns of the same type, leaves every count where
        // it was: the shapes compare equal and this returns without querying anything.
        await PublishRollupsAsync(db, notifier, issue.TeamId, rollupBefore, RollupOf(issue), ct);

        return Results.Ok(summary);
    }

    /// <summary>
    /// The rank a dropped issue takes: a key between its new neighbours', so a reorder writes one row
    /// instead of renumbering the whole column.
    ///
    /// <para>The anchors say where it was dropped; the neighbours are read from the column as it is now.
    /// The issue follows <c>afterIssueId</c> and takes a key before whatever the database has next —
    /// which is <c>beforeIssueId</c> when the client's view was current, and something else when it was
    /// not, in which case trusting both anchors could produce a key outside the gap or no key at all.
    /// An anchor that is no longer in the target column is ignored. Archived issues are not neighbours:
    /// the board does not show them, and the client computes the same key without them.</para>
    /// </summary>
    private static async Task<string> ResolveRankAsync(
        PlannerDbContext db,
        Issue issue,
        MoveIssueRequest request,
        CancellationToken ct)
    {
        var column = db.Issues
            .Where(i => i.TeamId == issue.TeamId && i.StateId == issue.StateId && i.Id != issue.Id && i.ArchivedAt == null);

        async Task<string?> RankOf(Guid? anchorId) => anchorId is { } anchor
            ? await column.Where(i => i.Id == anchor).Select(i => i.Rank).FirstOrDefaultAsync(ct)
            : null;

        var ranks = column.Select(i => i.Rank);

        if (await RankOf(request.AfterIssueId) is { } after)
        {
            return await Ranks.AfterAsync(ranks, after, ct);
        }

        if (await RankOf(request.BeforeIssueId) is { } before)
        {
            return await Ranks.BeforeAsync(ranks, before, ct);
        }

        if (request.Rank is { } explicitRank)
        {
            return explicitRank;
        }

        // No anchors: drop it at the end of the target column.
        return await Ranks.AppendAsync(ranks, ct);
    }

    /// <summary>The before and after of a re-pointed link, by name as well as id — the feed reads these
    /// across teams, where the client holds no list of another team's projects to look an id up in.</summary>
    private static object Relinked(Guid? from, Guid? to, IReadOnlyDictionary<Guid, string> names) => new
    {
        from = from is { } f ? names.GetValueOrDefault(f) : null,
        to = to is { } t ? names.GetValueOrDefault(t) : null,
        fromId = from,
        toId = to
    };

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
        // State comes along for the rollup shape: an archived issue leaves every count it was in.
        var issue = await db.Issues.Include(i => i.State).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (issue is null)
        {
            return ApiResults.NotFound("That issue");
        }

        if (await ApiResults.RequireTeamAsync(access, issue.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        var rollupBefore = RollupOf(issue);

        issue.ArchivedAt = archivedAt;
        activity.Record(EntityTypes.Issue, issue.Id,
            archivedAt is null ? ActivityActions.Restored : ActivityActions.Archived, null,
            teamId: issue.TeamId, projectId: issue.ProjectId, issueId: issue.Id);

        await db.SaveChangesAsync(ct);

        var summary = await SummaryAsync(db, issue.Id, ct);
        await notifier.IssueChanged(archivedAt is null ? ChangeKind.Restored : ChangeKind.Archived, summary);
        await PublishRollupsAsync(db, notifier, issue.TeamId, rollupBefore, RollupOf(issue), ct);
        return Results.Ok(summary);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var issue = await db.Issues.Include(i => i.State).FirstOrDefaultAsync(i => i.Id == id, ct);
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
        var rollupBefore = RollupOf(issue);

        // Audit rows outlive the issue, so drop the FK-free reference rather than blocking the delete.
        await db.ActivityEvents.Where(a => a.IssueId == id)
            .ExecuteUpdateAsync(a => a.SetProperty(x => x.IssueId, (Guid?)null), ct);

        db.Issues.Remove(issue);
        await db.SaveChangesAsync(ct);

        await notifier.IssueChanged(ChangeKind.Deleted, summary);
        await PublishRollupsAsync(db, notifier, issue.TeamId, rollupBefore, null, ct);
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
        return Results.Created($"/api/v1/comments/{comment.Id.ToBase58()}", dto);
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
        return Results.Created($"/api/v1/attachments/{attachment.Id.ToBase58()}", dto);
    }

    private static async Task<IResult> DeleteAttachmentAsync(
        Guid attachmentId,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        IRealtimeNotifier notifier,
        IConfiguration config,
        IWebHostEnvironment environment,
        ILoggerFactory loggerFactory,
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

        try
        {
            IssueFileEndpoints.DeleteStoredFile(attachment, config, environment);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            loggerFactory.CreateLogger("Planner.Api.Attachments")
                .LogError(error, "Failed to delete stored attachment {AttachmentId}", attachmentId);
            return Results.Problem(
                title: "Attachment removal failed",
                detail: "The server could not delete the uploaded file. The attachment was kept so removal can be retried.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

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
        return Results.Created($"/api/v1/issues/{id.ToBase58()}/relations/{relation.Id.ToBase58()}", dto);
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
            await ActivityFeed.ToDtosAsync(db, events, ct), paging.NormalizedPage, paging.NormalizedSize, total));
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
            await ActivityFeed.ToDtosAsync(db, events, ct), paging.NormalizedPage, paging.NormalizedSize, total));
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

    /// <summary>The only facts about an issue that a project's or milestone's rollup is counted from.
    /// Two of these comparing equal is exactly the condition for "this write moved no rollup".</summary>
    private readonly record struct Rollup(
        Guid? ProjectId,
        Guid? MilestoneId,
        WorkflowStateType StateType,
        bool Archived);

    /// <summary>The rollup shape of a tracked issue. Requires <c>State</c> to be loaded.</summary>
    private static Rollup RollupOf(Issue issue) =>
        new(issue.ProjectId, issue.MilestoneId, issue.State.Type, issue.ArchivedAt is not null);

    /// <summary>
    /// Republishes the projects and milestones whose rollup an issue write moved.
    ///
    /// <para>A rollup is counted from issues when a project or milestone is read — see
    /// <see cref="Mapping.ProjectProjection"/> — so an issue write changes what those two report
    /// without touching either row. Nothing else would tell a client holding one: the sidebar's
    /// counts and the milestone rollups in project settings have no issue list of their own to count,
    /// and a client cannot infer the change either, because an issue moved between projects moves two
    /// rollups while the issue's own envelope names only the one it moved to.</para>
    ///
    /// <para>Both sides of the move are passed so both are republished, and <c>null</c> stands for an
    /// issue that did not exist yet or no longer does. Equal shapes publish nothing, which is what
    /// keeps a board reorder — the most common write there is, and one that cannot change a count —
    /// from costing two queries and a broadcast.</para>
    /// </summary>
    private static async Task PublishRollupsAsync(
        PlannerDbContext db,
        IRealtimeNotifier notifier,
        Guid teamId,
        Rollup? before,
        Rollup? after,
        CancellationToken ct)
    {
        if (before == after)
        {
            return;
        }

        var projectIds = Touched(before?.ProjectId, after?.ProjectId);
        var milestoneIds = Touched(before?.MilestoneId, after?.MilestoneId);

        if (projectIds.Count > 0)
        {
            var projects = await db.Projects.AsNoTracking()
                .Where(p => projectIds.Contains(p.Id))
                .Select(Mapping.ProjectProjection)
                .ToListAsync(ct);

            foreach (var project in projects)
            {
                await notifier.ProjectChanged(ChangeKind.Updated, project);
            }
        }

        if (milestoneIds.Count > 0)
        {
            var milestones = await db.Milestones.AsNoTracking()
                .Where(m => milestoneIds.Contains(m.Id))
                .Select(Mapping.MilestoneProjection)
                .ToListAsync(ct);

            foreach (var milestone in milestones)
            {
                await notifier.MilestoneChanged(ChangeKind.Updated, milestone, teamId);
            }
        }

        // The two ends of a move, minus the nulls, and deduplicated for the common case of an issue
        // that stayed where it was and only changed state.
        static List<Guid> Touched(Guid? before, Guid? after)
        {
            var ids = new List<Guid>(capacity: 2);

            if (before is { } from)
            {
                ids.Add(from);
            }

            if (after is { } to && to != before)
            {
                ids.Add(to);
            }

            return ids;
        }
    }
}
