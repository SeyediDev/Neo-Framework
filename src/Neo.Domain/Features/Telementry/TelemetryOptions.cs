namespace Neo.Domain.Features.Telementry;

public class TelemetryOptions
{
    public string ApplicationName { get; set; } = "Neo.Application";
    public string ApplicationVersion { get; set; } = "1.0.0";
    public string? JaegerExporterHost { get; set; }
    public int JaegerExporterPort { get; set; } = 6831;
    public string? ZipkinExporterUri { get; set; }
    public string? OtlpExporterEndpoint { get; set; }
    public string OtlpExporterProtocol { get; set; } = "grpc"; // "grpc" or "http/protobuf"
    public bool InSpanExceptionSetStackTrace { get; set; } = false;
}

public class TelemetryAttributeValue
{
    public static string http { get; } = "http";
    public static string cqrs { get; } = "cqrs";
    public static string domain_service { get; } = "domain.service";
}