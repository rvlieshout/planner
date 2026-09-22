using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Planner.Infrastructure.Configurations;

internal static class RankColumns
{
    /// <summary>A <see cref="Domain.Common.Rank"/> key column. Collated <c>"C"</c> because the keys are
    /// ordered byte-wise: under a linguistic collation such as <c>en_US.utf8</c>, <c>a0V</c> and
    /// <c>a0v</c> — or any upper- and lower-case digits — would sort into an order no key was made
    /// for, and <c>ORDER BY rank</c> would quietly disagree with the keys the API hands out.</summary>
    public static PropertyBuilder<string> IsRank(this PropertyBuilder<string> property) =>
        property.UseCollation("C").IsRequired();
}
