using Microsoft.EntityFrameworkCore;
using Planner.Domain.Common;

namespace Planner.Api.Common;

/// <summary>
/// Places a row in a hand-ordered sequence by giving it a <see cref="Rank"/> key.
///
/// <para>A sequence is passed as a query over the ranks of its <em>other</em> rows — the row being
/// placed left out — so the neighbour a key is made against is the one the database holds now, not the
/// one a client last saw. A client's anchor says which row to follow; what follows that row is looked
/// up here. That keeps every key strictly between two real neighbours even when the client's view was
/// stale.</para>
/// </summary>
public static class Ranks
{
    /// <summary>A key after every row of the sequence.</summary>
    public static async Task<string> AppendAsync(IQueryable<string> sequence, CancellationToken ct)
    {
        var last = await sequence.OrderByDescending(rank => rank).FirstOrDefaultAsync(ct);
        return Rank.Between(last, null);
    }

    /// <summary>A key immediately after the row ranked <paramref name="after"/>.</summary>
    public static async Task<string> AfterAsync(IQueryable<string> sequence, string after, CancellationToken ct)
    {
        var next = await sequence
            .Where(rank => string.Compare(rank, after) > 0)
            .OrderBy(rank => rank)
            .FirstOrDefaultAsync(ct);

        return Rank.Between(after, next);
    }

    /// <summary>A key immediately before the row ranked <paramref name="before"/>.</summary>
    public static async Task<string> BeforeAsync(IQueryable<string> sequence, string before, CancellationToken ct)
    {
        var previous = await sequence
            .Where(rank => string.Compare(rank, before) < 0)
            .OrderByDescending(rank => rank)
            .FirstOrDefaultAsync(ct);

        return Rank.Between(previous, before);
    }
}
