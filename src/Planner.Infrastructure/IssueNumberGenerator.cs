using Microsoft.EntityFrameworkCore;

namespace Planner.Infrastructure;

public interface IIssueNumberGenerator
{
    Task<int> NextAsync(Guid teamId, CancellationToken cancellationToken = default);
}

/// <summary>Hands out the next per-team issue number with a single atomic UPDATE .. RETURNING.
/// Read-then-write in application code would hand two concurrent creates the same number, and the
/// unique (team_id, number) index would then reject one of them.</summary>
public sealed class IssueNumberGenerator(PlannerDbContext db) : IIssueNumberGenerator
{
    public async Task<int> NextAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        // The statement is not composable, so it is materialised as-is rather than through Single(),
        // which would try to wrap it in a LIMIT. SqlQuery<int> expects the column to be called "Value".
        var numbers = await db.Database
            .SqlQuery<int>(
                $"""UPDATE teams SET issue_counter = issue_counter + 1 WHERE id = {teamId} RETURNING issue_counter AS "Value" """)
            .ToListAsync(cancellationToken);

        return numbers.Count == 1
            ? numbers[0]
            : throw new InvalidOperationException($"Team {teamId} does not exist.");
    }
}
