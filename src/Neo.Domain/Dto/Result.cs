namespace Neo.Domain.Dto;

/// <summary>
/// کلاس نتیجه عملیات
/// </summary>
public class Result
{
    internal Result(bool succeeded, IEnumerable<string> errors)
    {
        Succeeded = succeeded;
        Errors = [.. errors];
    }

    public bool Succeeded { get; init; }
    public bool IsSuccess => Succeeded;
    public string[] Errors { get; init; }
    public string ErrorMessage => Errors.Length > 0 ? string.Join(", ", Errors) : string.Empty;

    public static Result Success()
    {
        return new Result(true, Array.Empty<string>());
    }

    public static Result Failure(IEnumerable<string> errors)
    {
        return new Result(false, errors);
    }

    public static Result Failure(string error)
    {
        return new Result(false, [error]);
    }
}

/// <summary>
/// کلاس نتیجه عملیات با داده
/// </summary>
public class Result<T>
{
    internal Result(bool succeeded, T? data, IEnumerable<string> errors)
    {
        Succeeded = succeeded;
        Data = data;
        Errors = [.. errors];
    }

    public bool Succeeded { get; init; }
    public bool IsSuccess => Succeeded;
    public T? Data { get; init; }
    public string[] Errors { get; init; }
    public string ErrorMessage => Errors.Length > 0 ? string.Join(", ", Errors) : string.Empty;

    public static Result<T> Success(T data)
    {
        return new Result<T>(true, data, Array.Empty<string>());
    }

    public static Result<T> Failure(IEnumerable<string> errors)
    {
        return new Result<T>(false, default, errors);
    }

    public static Result<T> Failure(string error)
    {
        return new Result<T>(false, default, [error]);
    }
}
