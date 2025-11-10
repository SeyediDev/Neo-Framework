namespace Neo.Domain.Features.Integrations;

public class ExternalApiRequest
{
    public IDictionary<string, string?> PathParameters { get; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public IDictionary<string, string?> QueryParameters { get; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public IDictionary<string, string?> Headers { get; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public ExternalApiRequestBody? Body { get; set; }

    public TimeSpan? Timeout { get; set; }
}
