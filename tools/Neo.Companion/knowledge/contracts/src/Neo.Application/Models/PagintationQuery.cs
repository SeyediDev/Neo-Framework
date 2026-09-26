namespace Neo.Application.Models;

public record PaginationQuery
{
    public const int MaxPageSize = 200;
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
        ValidatePage(pageNumber, pageSize, int.MaxValue);
        return list.Skip(checked((pageNumber - 1) * pageSize)).Take(pageSize);
    }

    public static void SetPagination<T>(this PaginationResponse<T> response, PaginationQuery query)
    {
        ValidatePage(query.PageNumber, query.PageSize);
        response.Items = response.Items?.Skip(checked((query.PageNumber - 1) * query.PageSize)).Take(query.PageSize + 1).ToList();
        response.HasNext = false;
        if (response.Items?.Count > query.PageSize)
        {
            response.HasNext = true;
            response.Items.Remove(response.Items.Last());
        }
    }

    public static void ValidatePage(int pageNumber, int pageSize, int maxPageSize = PaginationQuery.MaxPageSize)
    {
        if (pageNumber < 1 || pageSize < 1 || pageSize > maxPageSize || (long)(pageNumber - 1) * pageSize > int.MaxValue)
            throw new Neo.Application.Exceptions.ValidationException([new FluentValidation.Results.ValidationFailure("Pagination",
                $"PageNumber must be positive, PageSize must be 1..{maxPageSize}, and the offset must fit Int32.")]);
    }
}
