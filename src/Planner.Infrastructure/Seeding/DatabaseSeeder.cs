using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Planner.Domain.Common;
using Planner.Domain.Entities;
using Planner.Contracts.Enums;
using Planner.Domain.Identity;

namespace Planner.Infrastructure.Seeding;

/// <summary>Brings a fresh database to a usable state: roles, a bootstrap owner, and optional demo
/// content. Every step is idempotent, so it is safe to run on every container start.</summary>
public sealed class DatabaseSeeder(
    PlannerDbContext db,
    UserManager<AppUser> users,
    RoleManager<AppRole> roles,
    IOptions<PlannerSeedOptions> options,
    ILogger<DatabaseSeeder> logger)
{
    private readonly PlannerSeedOptions _options = options.Value;

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedRolesAsync();
        var owner = await SeedOwnerAsync();

        if (_options.SeedDemoData && owner is not null)
        {
            await SeedDemoAsync(owner, cancellationToken);
        }
    }

    private async Task SeedRolesAsync()
    {
        foreach (var (name, description) in PlannerRoles.All)
        {
            if (await roles.RoleExistsAsync(name))
            {
                continue;
            }

            var result = await roles.CreateAsync(new AppRole(name, description));
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Could not create role {name}: {Describe(result)}");
            }

            logger.LogInformation("Seeded role {Role}", name);
        }
    }

    private async Task<AppUser?> SeedOwnerAsync()
    {
        var existing = await users.FindByEmailAsync(_options.OwnerEmail);
        if (existing is not null)
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(_options.OwnerPassword))
        {
            logger.LogWarning(
                "No bootstrap owner password configured (Planner:Seed:OwnerPassword). Skipping owner creation");
            return null;
        }

        var owner = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = _options.OwnerEmail,
            Email = _options.OwnerEmail,
            EmailConfirmed = true,
            DisplayName = _options.OwnerDisplayName
        };

        var result = await users.CreateAsync(owner, _options.OwnerPassword);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not create the bootstrap owner: {Describe(result)}");
        }

        await users.AddToRoleAsync(owner, PlannerRoles.Owner);
        logger.LogInformation("Seeded bootstrap owner {Email}", _options.OwnerEmail);
        return owner;
    }

    private async Task SeedDemoAsync(AppUser owner, CancellationToken cancellationToken)
    {
        if (await db.Teams.AnyAsync(cancellationToken))
        {
            return;
        }

        var developer = await EnsureUserAsync("dana@planner.local", "Dana Developer", PlannerRoles.Member);
        var designer = await EnsureUserAsync("sam@planner.local", "Sam Designer", PlannerRoles.Member);

        var team = new Team
        {
            Key = "ENG",
            Name = "Engineering",
            Description = "Platform and product engineering.",
            Color = "#6E79F1"
        };

        db.Teams.Add(team);

        var states = WorkflowStateDefaults.CreateFor(team.Id);
        db.WorkflowStates.AddRange(states);

        db.TeamMembers.AddRange(
            new TeamMember { TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Lead },
            new TeamMember { TeamId = team.Id, UserId = developer.Id, Role = TeamRole.Member },
            new TeamMember { TeamId = team.Id, UserId = designer.Id, Role = TeamRole.Member });

        var bug = new Label { TeamId = team.Id, Name = "bug", Color = "#EB5757" };
        var feature = new Label { TeamId = team.Id, Name = "feature", Color = "#4CB782" };
        db.Labels.AddRange(bug, feature);

        var project = new Project
        {
            TeamId = team.Id,
            Name = "Avalonia Desktop Client",
            Summary = "Cross-platform desktop client for the on-prem planner.",
            Description = "Ship a native client that talks to the REST API and stays live over SignalR.",
            Status = ProjectStatus.InProgress,
            Health = ProjectHealth.OnTrack,
            LeadUserId = owner.Id,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            TargetDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(60)),
            Rank = Rank.First
        };
        db.Projects.Add(project);

        var alpha = new Milestone
        {
            ProjectId = project.Id,
            Name = "Alpha",
            Description = "Read-only board with live updates.",
            TargetDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(21)),
            Status = MilestoneStatus.Active,
            Rank = Rank.First
        };
        var beta = new Milestone
        {
            ProjectId = project.Id,
            Name = "Beta",
            Description = "Full editing, offline queue, attachments.",
            TargetDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(50)),
            Rank = Rank.Between(Rank.First, null)
        };
        db.Milestones.AddRange(alpha, beta);

        db.Documents.Add(new Document
        {
            TeamId = team.Id,
            ProjectId = project.Id,
            Title = "Client architecture brief",
            Content = "# Client architecture\n\nMVVM over a SignalR-backed cache. The API stays the source of truth.",
            CreatedById = owner.Id
        });

        var todo = states.First(s => s.Type == WorkflowStateType.Unstarted);
        var inProgress = states.First(s => s.Type == WorkflowStateType.Started);

        var number = 0;
        string? rank = null;
        Issue NewIssue(string title, WorkflowState state, IssuePriority priority, Guid? assignee, Milestone milestone)
        {
            number++;
            return new Issue
            {
                TeamId = team.Id,
                Number = number,
                Title = title,
                StateId = state.Id,
                Priority = priority,
                AssigneeId = assignee,
                CreatorId = owner.Id,
                ProjectId = project.Id,
                MilestoneId = milestone.Id,
                Rank = rank = Rank.Between(rank, null),
                StartedAt = state.Type == WorkflowStateType.Started ? DateTimeOffset.UtcNow : null
            };
        }

        var issues = new[]
        {
            NewIssue("Render the board view", inProgress, IssuePriority.High, developer.Id, alpha),
            NewIssue("Subscribe to the SignalR team channel", todo, IssuePriority.Urgent, developer.Id, alpha),
            NewIssue("Design the issue detail panel", todo, IssuePriority.Medium, designer.Id, alpha),
            NewIssue("Offline write queue", todo, IssuePriority.Low, null, beta)
        };

        db.Issues.AddRange(issues);
        db.IssueLabels.AddRange(
            new IssueLabel { IssueId = issues[0].Id, LabelId = feature.Id },
            new IssueLabel { IssueId = issues[1].Id, LabelId = bug.Id });

        team.IssueCounter = number;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded demo data: team {TeamKey} with {IssueCount} issues", team.Key, number);
    }

    private async Task<AppUser> EnsureUserAsync(string email, string displayName, string role)
    {
        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            return existing;
        }

        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName
        };

        // Demo accounts share the owner password so the sample install has one credential to remember.
        var result = await users.CreateAsync(user, _options.OwnerPassword);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not create demo user {email}: {Describe(result)}");
        }

        await users.AddToRoleAsync(user, role);
        return user;
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));
}
