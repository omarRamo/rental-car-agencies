namespace CarRental.Application.Common.Paging;

/// <summary>A page of a list: 1-based page number, size clamped to [1, <see cref="MaxPageSize"/>].</summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 200;

    public PageRequest(int page = 1, int pageSize = DefaultPageSize)
    {
        Page = Math.Max(1, page);
        PageSize = Math.Clamp(pageSize, 1, MaxPageSize);
    }

    public int Page { get; }

    public int PageSize { get; }

    public int Skip => (Page - 1) * PageSize;
}

/// <summary>A page of items and the total count of the unpaged list.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public static class PagedResult
{
    public static PagedResult<T> Empty<T>(PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new([], 0, request.Page, request.PageSize);
    }
}
