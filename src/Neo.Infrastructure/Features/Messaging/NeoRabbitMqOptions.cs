namespace Neo.Infrastructure.Features.Messaging;

/// <summary>Explicit RabbitMQ connection and bounded consumer settings. No broker is enabled implicitly.</summary>
public sealed class NeoRabbitMqOptions
{
    public string Host { get; set; } = "localhost";
    public ushort Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string EndpointPrefix { get; set; } = "neo";
    public ushort PrefetchCount { get; set; } = 16;
    public int ConcurrentMessageLimit { get; set; } = 4;
    public int StartupTimeoutSeconds { get; set; } = 30;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Contains("://", StringComparison.Ordinal)
            || Port == 0 || string.IsNullOrWhiteSpace(VirtualHost)
            || string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
            throw new ArgumentException("RabbitMq requires a host name, port, virtual host, username and password. Use environment configuration for credentials.");
        if (string.IsNullOrWhiteSpace(EndpointPrefix) || EndpointPrefix.Length > 64
            || EndpointPrefix.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("RabbitMq EndpointPrefix must contain 1-64 lowercase letters, digits or hyphens.");
        if (PrefetchCount == 0 || ConcurrentMessageLimit < 1 || ConcurrentMessageLimit > PrefetchCount
            || StartupTimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("RabbitMq requires 1 <= ConcurrentMessageLimit <= PrefetchCount and a startup timeout of 1-300 seconds.");
    }
}
