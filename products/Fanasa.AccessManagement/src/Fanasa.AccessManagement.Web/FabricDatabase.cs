using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fanasa.AccessManagement.Web.Persistence;

public sealed record FabricOutbox(long Id, string Key, string Kind, string Payload, DateTimeOffset RecordedAt);

/// <summary>SQLite local-volume transactions; nested domain commands share the same unit of work.</summary>
public sealed class FabricDatabase : IDisposable
{
    private readonly string connectionString;
    private readonly ThreadLocal<(SqliteConnection Connection, SqliteTransaction Transaction)?> context = new();
    public FabricDatabase(string path)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 15, Pooling = false }.ToString();
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS fabric_documents(kind TEXT NOT NULL,key TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(kind,key)); CREATE TABLE IF NOT EXISTS fabric_outbox(id INTEGER PRIMARY KEY AUTOINCREMENT,key TEXT NOT NULL UNIQUE,kind TEXT NOT NULL,payload TEXT NOT NULL,recorded TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;"; command.ExecuteNonQuery();
        return connection;
    }
    public T Transaction<T>(Func<T> action)
    {
        if (context.Value is { } nested)
        {
            var savepoint = "fabric_" + Guid.NewGuid().ToString("N"); nested.Transaction.Save(savepoint);
            try { var result = action(); nested.Transaction.Release(savepoint); return result; }
            catch { nested.Transaction.Rollback(savepoint); nested.Transaction.Release(savepoint); throw; }
        }
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        context.Value = (connection, transaction);
        try { var result = action(); transaction.Commit(); return result; }
        finally { context.Value = null; }
    }
    private T WithConnection<T>(Func<SqliteConnection, SqliteTransaction?, T> action)
    {
        if (context.Value is { } current) return action(current.Connection, current.Transaction);
        using var connection = Open(); return action(connection, null);
    }
    public T? Read<T>(string kind, string key) => WithConnection((connection, transaction) =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT json FROM fabric_documents WHERE kind=$kind AND key=$key";
        command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("Invalid persisted document.") : default;
    });
    public T[] All<T>(string kind) => WithConnection((connection, transaction) =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT json FROM fabric_documents WHERE kind=$kind ORDER BY key"; command.Parameters.AddWithValue("$kind", kind);
        using var reader = command.ExecuteReader(); var result = new List<T>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0)) ?? throw new InvalidDataException());
        return result.ToArray();
    });
    public void Put<T>(string kind, string key, T value)
    {
        if (!context.Value.HasValue) throw new InvalidOperationException("Writes require a transaction.");
        WithConnection((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO fabric_documents(kind,key,json) VALUES($kind,$key,$json) ON CONFLICT(kind,key) DO UPDATE SET json=excluded.json";
            command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value));
            return command.ExecuteNonQuery();
        });
    }
    public void Emit<T>(string key, string kind, T payload, DateTimeOffset recordedAt)
    {
        if (!context.Value.HasValue) throw new InvalidOperationException("Outbox writes require a transaction.");
        WithConnection((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO fabric_outbox(key,kind,payload,recorded) VALUES($key,$kind,$payload,$recorded)";
            command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload)); command.Parameters.AddWithValue("$recorded", recordedAt.ToString("O"));
            return command.ExecuteNonQuery();
        });
    }
    public FabricOutbox[] Outbox(long after = 0, int take = 100) => WithConnection((connection, transaction) =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,key,kind,payload,recorded FROM fabric_outbox WHERE id>$after ORDER BY id LIMIT $take";
        command.Parameters.AddWithValue("$after", after); command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 100));
        using var reader = command.ExecuteReader(); var result = new List<FabricOutbox>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture)));
        return result.ToArray();
    });
    public void Dispose() => context.Dispose();
}
