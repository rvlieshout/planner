using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Planner.Domain.Entities;
using Planner.Domain.Identity;

namespace Planner.Infrastructure;

public class PlannerDbContext(DbContextOptions<PlannerDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<WorkflowState> WorkflowStates => Set<WorkflowState>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Issue> Issues => Set<Issue>();
    public DbSet<IssueLabel> IssueLabels => Set<IssueLabel>();
    public DbSet<IssueRelation> IssueRelations => Set<IssueRelation>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Add the Identity passkey store without changing existing identity column lengths.
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserPasskey<Guid>>(passkey =>
        {
            passkey.ToTable("user_passkeys");
            passkey.HasKey(p => p.CredentialId);
            passkey.Property(p => p.CredentialId).HasMaxLength(1024);
            passkey.OwnsOne(p => p.Data).ToJson("data");
            passkey.HasOne<AppUser>().WithMany().HasForeignKey(p => p.UserId).IsRequired();
        });

        // Trigram indexes back the ILIKE title/description search on issues.
        builder.HasPostgresExtension("pg_trgm");

        builder.ApplyConfigurationsFromAssembly(typeof(PlannerDbContext).Assembly);

        RenameIdentityTables(builder);
        NamingConventions.ApplySnakeCase(builder);
    }

    /// <summary>The default AspNetUsers/AspNetRoles names read badly next to the rest of the schema,
    /// and on-prem operators query this database by hand.</summary>
    private static void RenameIdentityTables(ModelBuilder builder)
    {
        builder.Entity<AppUser>().ToTable("users");
        builder.Entity<AppRole>().ToTable("roles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<Guid>>().ToTable("role_claims");
    }

    /// <summary>Stamps UpdatedAt centrally so no endpoint can forget it.</summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<Planner.Domain.Common.Entity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
