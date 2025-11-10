namespace Neo.Domain.Features.Integrations;

public class ExternalApiInvocationResult
{
    public ExternalApiInvocationResult(int statusCode, string? content, IReadOnlyDictionary<string, string[]> headers, string? error = null, TimeSpan? duration = null)
    {
        StatusCode = statusCode;
        Content = content;
        Headers = headers;
        Error = error;
        Duration = duration;
    }

    public int StatusCode { get; }
    public string? Content { get; }
    public IReadOnlyDictionary<string, string[]> Headers { get; }
    public string? Error { get; }
    public TimeSpan? Duration { get; }

    public bool IsSuccessStatusCode => StatusCode is >= 200 and <= 299;
}
