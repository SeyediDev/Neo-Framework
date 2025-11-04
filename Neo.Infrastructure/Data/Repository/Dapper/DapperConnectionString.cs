using Microsoft.Extensions.Configuration;

namespace Neo.Infrastructure.Data.Repository.Dapper;

public interface IDapperConnectionString
{
    string ConnectionString { get; }
}

public class DapperConnectionString(IConfiguration configuration, string connectionStringKey) : IDapperConnectionString
{
    public string ConnectionString { get; set; } = configuration.GetConnectionString(connectionStringKey)!;
}
