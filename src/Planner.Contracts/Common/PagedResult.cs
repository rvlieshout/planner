namespace Planner.Contracts.Common;

/// <summary>Offset paging envelope used by every list endpoint.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasNext => Page * PageSize < TotalCount;
}

/// <summary>Common paging/sorting query parameters, bound from the query string.</summary>
public sealed record PageQuery(int Page = 1, int PageSize = 50)
{
    public const int MaxPageSize = 200;

    public int NormalizedPage => Page < 1 ? 1 : Page;
    public int NormalizedSize => PageSize is < 1 or > MaxPageSize ? 50 : PageSize;
    public int Skip => (NormalizedPage - 1) * NormalizedSize;
}
