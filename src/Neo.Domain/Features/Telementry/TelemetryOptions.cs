namespace Neo.Domain.Features.Telementry;

public class TelemetryOptions
{
    public string ApplicationName { get; set; } = "Neo.Application";
    public string ApplicationVersion { get; set; } = "1.0.0";
    public string JaegerExporterHost { get; set; } = "localhost";
    public int JaegerExporterPort { get; set; } = 6831;
    public string ZipkinExporterUri { get; set; } = "http://localhost:9411/api/v2/spans";
    public bool InSpanExceptionSetStackTrace { get; set; } = false;
}

public class TelemetryAttributeValue
{
    public static string http { get; } = "http";
    public static string cqrs { get; } = "cqrs";
    public static string domain_service { get; } = "domain.service";
}