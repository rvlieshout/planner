using Microsoft.AspNetCore.SignalR;
using Planner.Api.Authorization;
using Planner.Contracts.Auth;
using Planner.Contracts.Issues;
using Planner.Contracts.Projects;
using Planner.Contracts.Realtime;
using Planner.Contracts.Teams;

namespace Planner.Api.Realtime;

/// <summary>Publishes entity changes to connected clients. Endpoints call this after a successful
/// SaveChanges, never before — a client must never be told about a write that then rolls back.</summary>
public interface IRealtimeNotifier
{
    Task TeamChanged(ChangeKind kind, TeamDto team);
    Task TeamMemberChanged(ChangeKind kind, TeamMemberDto member);
    Task WorkflowStateChanged(ChangeKind kind, WorkflowStateDto state);
    Task LabelChanged(ChangeKind kind, LabelDto label, Guid teamId);
    Task ProjectChanged(ChangeKind kind, ProjectDto project);
    Task MilestoneChanged(ChangeKind kind, MilestoneDto milestone, Guid teamId);
    Task DocumentChanged(ChangeKind kind, DocumentSummary document);
    Task IssueChanged(ChangeKind kind, IssueSummary issue);
    Task CommentChanged(ChangeKind kind, CommentDto comment, Guid teamId);
    Task AttachmentChanged(ChangeKind kind, AttachmentDto attachment, Guid teamId);
    Task IssueRelationChanged(ChangeKind kind, IssueRelationDto relation, Guid issueId, Guid teamId);
    Task UserChanged(ChangeKind kind, UserSummary user);
}

public sealed class RealtimeNotifier(IHubContext<PlannerHub, IPlannerClient> hub, CurrentUser currentUser)
    : IRealtimeNotifier
{
    private Guid ActorId => currentUser.IsAuthenticated ? currentUser.Id : Guid.Empty;

    public Task TeamChanged(ChangeKind kind, TeamDto team) =>
        Team(team.Id).TeamChanged(Envelope(kind, EntityTypes.Team, team.Id, team.Id, null, null, team));

    public Task TeamMemberChanged(ChangeKind kind, TeamMemberDto member) =>
        Team(member.TeamId).TeamMemberChanged(
            Envelope(kind, EntityTypes.TeamMember, member.UserId, member.TeamId, null, null, member));

    public Task WorkflowStateChanged(ChangeKind kind, WorkflowStateDto state) =>
        Team(state.TeamId).WorkflowStateChanged(
            Envelope(kind, EntityTypes.WorkflowState, state.Id, state.TeamId, null, null, state));

    public Task LabelChanged(ChangeKind kind, LabelDto label, Guid teamId) =>
        Team(teamId).LabelChanged(Envelope(kind, EntityTypes.Label, label.Id, teamId, null, null, label));

    public Task ProjectChanged(ChangeKind kind, ProjectDto project) =>
        Team(project.TeamId).ProjectChanged(
            Envelope(kind, EntityTypes.Project, project.Id, project.TeamId, project.Id, null, project));

    public Task MilestoneChanged(ChangeKind kind, MilestoneDto milestone, Guid teamId) =>
        Team(teamId).MilestoneChanged(
            Envelope(kind, EntityTypes.Milestone, milestone.Id, teamId, milestone.ProjectId, null, milestone));

    public Task DocumentChanged(ChangeKind kind, DocumentSummary document) =>
        Team(document.TeamId).DocumentChanged(
            Envelope(kind, EntityTypes.Document, document.Id, document.TeamId, document.ProjectId, null, document));

    public Task IssueChanged(ChangeKind kind, IssueSummary issue) =>
        Team(issue.TeamId).IssueChanged(
            Envelope(kind, EntityTypes.Issue, issue.Id, issue.TeamId, issue.ProjectId, issue.Id, issue));

    // Comment-level traffic goes to the issue group only: a board with 200 issues open should not
    // receive every comment typed anywhere in the team.
    public Task CommentChanged(ChangeKind kind, CommentDto comment, Guid teamId) =>
        Issue(comment.IssueId).CommentChanged(
            Envelope(kind, EntityTypes.Comment, comment.Id, teamId, null, comment.IssueId, comment));

    public Task AttachmentChanged(ChangeKind kind, AttachmentDto attachment, Guid teamId) =>
        Issue(attachment.IssueId).AttachmentChanged(
            Envelope(kind, EntityTypes.Attachment, attachment.Id, teamId, null, attachment.IssueId, attachment));

    public Task IssueRelationChanged(ChangeKind kind, IssueRelationDto relation, Guid issueId, Guid teamId) =>
        Issue(issueId).IssueRelationChanged(
            Envelope(kind, EntityTypes.IssueRelation, relation.Id, teamId, null, issueId, relation));

    public Task UserChanged(ChangeKind kind, UserSummary user) =>
        hub.Clients.Group(RealtimeGroups.Organization)
            .UserChanged(Envelope(kind, EntityTypes.User, user.Id, null, null, null, user));

    private IPlannerClient Team(Guid teamId) => hub.Clients.Group(RealtimeGroups.Team(teamId));

    private IPlannerClient Issue(Guid issueId) => hub.Clients.Group(RealtimeGroups.Issue(issueId));

    private EntityChange<T> Envelope<T>(
        ChangeKind kind,
        string entityType,
        Guid id,
        Guid? teamId,
        Guid? projectId,
        Guid? issueId,
        T? entity) =>
        new(kind, entityType, id, teamId, projectId, issueId, ActorId, DateTimeOffset.UtcNow, entity);
}
