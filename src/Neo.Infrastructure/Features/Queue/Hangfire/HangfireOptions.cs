namespace Neo.Infrastructure.Features.Queue.Hangfire;

internal sealed class HangfireOptions
{
    public string Storage { get; set; } = "SqlServer"; // یا Redis
    public string ConnectionString { get; set; } = string.Empty;
    public string RedisPrefix { get; set; } = "hangfire:neo";
    public string[] Queues { get; set; } = ["default"];
    public int WorkerCount { get; set; } = 20;

    public DashboardOptions Dashboard { get; set; } = new();

    internal sealed class DashboardOptions
    {
        public bool Enabled { get; set; } = true;
        public string Path { get; set; } = "/hangfire";
        public string Authorization { get; set; } = "LocalOnly";
    }
}
