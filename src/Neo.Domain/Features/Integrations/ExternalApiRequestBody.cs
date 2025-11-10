namespace Neo.Domain.Features.Integrations;

public record ExternalApiRequestBody(string Content, string ContentType = "application/json", string? Charset = "utf-8");
