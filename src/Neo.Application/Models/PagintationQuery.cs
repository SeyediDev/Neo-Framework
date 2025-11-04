namespace Neo.Application.Models;

public record PaginationQuery
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}

public record PaginationResponse<T>
{
    public List<T>? Items { get; set; }
    public bool HasNext { get; set; }
}

public static class PaginationExtensions
{
    public static IEnumerable<T> Pagination<T>(this IEnumerable<T> list, int pageNumber, int pageSize = 10)
    {
        return list.Skip((pageNumber - 1) * pageSize).Take(pageSize);
    }

    public static void SetPagination<T>(this PaginationResponse<T> response, PaginationQuery query)
    {
        response.Items = response.Items?.Pagination(query.PageNumber, query.PageSize + 1).ToList();
        if (response.Items?.Count > query.PageSize)
        {
            response.HasNext = true;
            response.Items.Remove(response.Items.Last());
        }
    }
}
