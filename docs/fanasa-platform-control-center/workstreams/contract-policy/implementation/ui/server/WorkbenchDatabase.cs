using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fanasa.ContractPolicy.Workbench;

public sealed record WorkbenchStorageHealth(bool Healthy, string Provider, int SchemaVersion,
    long Revision, string CheckedAt, bool LegacyImported, IReadOnlyList<string> Errors,
    IReadOnlyList<string> Checks, object Counts);

internal sealed record WorkbenchDatabaseSnapshot(WorkbenchState State, long MutationVersion, bool LegacyImported);

/// <summary>One database transaction contains the accepted definitions, product effect, receipt and meter.</summary>
internal sealed class WorkbenchDatabase(string directory)
{
    internal const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "workbench.db");
    public bool Exists => File.Exists(_path);

    private SqliteConnection Open(bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 5
        }.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys=ON;");
        return connection;
    }

    public void Initialize(WorkbenchState state, bool legacyImported)
    {
        using var connection = Open();
        Execute(connection, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        using var transaction = connection.BeginTransaction();
        if (Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM sqlite_master WHERE type='table';")) != 0)
            throw new InvalidDataException("Refusing to reseed an existing database.");
        Execute(connection, transaction, Schema);
        WriteRows(connection, transaction, state, 1, legacyImported);
        transaction.Commit();
    }

    public WorkbenchDatabaseSnapshot Read()
    {
        using var connection = Open(true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = Read(connection, transaction);
        transaction.Commit();
        return snapshot;
    }

    public long Write(WorkbenchState state, long expectedMutationVersion, string expectedStateFingerprint,
        Action<WorkbenchState> validate, Action? beforeWrite = null)
    {
        using var connection = Open();
        Execute(connection, null, "PRAGMA synchronous=FULL;");
        using var transaction = connection.BeginTransaction();
        var previous = Read(connection, transaction);
        validate(previous.State);
        if (previous.MutationVersion != expectedMutationVersion || Fingerprint(previous.State) != expectedStateFingerprint)
            throw new WorkbenchError(409, "storage-conflict", "پایگاه داده در درخواست هم‌زمان تغییر کرده است؛ وضعیت تازه را بخوانید.", previous.State.Revision);
        validate(state);
        var nextVersion = checked(previous.MutationVersion + 1);
        beforeWrite?.Invoke();
        WriteRows(connection, transaction, state, nextVersion, previous.LegacyImported);
        beforeWrite?.Invoke();
        transaction.Commit();
        return nextVersion;
    }

    private static WorkbenchDatabaseSnapshot Read(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (Convert.ToInt32(Scalar(connection, transaction, "PRAGMA user_version;")) != SchemaVersion)
            throw new InvalidDataException("Unsupported workbench database schema.");
        using (var command = Command(connection, transaction, "PRAGMA integrity_check;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (reader.GetString(0) != "ok") throw new InvalidDataException("SQLite integrity check failed.");
        using (var command = Command(connection, transaction, "PRAGMA foreign_key_check;"))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) throw new InvalidDataException("SQLite foreign key check failed.");
        ValidateNormalizedColumns(connection, transaction);

        WorkbenchState state; long mutationVersion; bool imported; string digest;
        using (var command = Command(connection, transaction, "SELECT json, mutation_version, legacy_imported, state_hash, revision FROM state_metadata WHERE singleton=1;"))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException("Database state metadata missing.");
            state = Deserialize<WorkbenchState>(reader.GetString(0));
            mutationVersion = reader.GetInt64(1); imported = reader.GetBoolean(2); digest = reader.GetString(3);
            if (reader.GetInt64(4) != state.Revision) throw new InvalidDataException("Metadata revision mismatch.");
        }
        state.TrustedContext = Deserialize<Fanasa.ContractPolicy.AccessEvaluationContext>(
            (string?)Scalar(connection, transaction, "SELECT json FROM trusted_context WHERE singleton=1;")
            ?? throw new InvalidDataException("Trusted context missing."));
        state.Policies = ReadRows<PolicyDocument>(connection, transaction, "policies");
        state.Drafts = ReadRows<PolicyDocument>(connection, transaction, "drafts");
        state.Resources = ReadRows<UnitRecord>(connection, transaction, "resources");
        state.Decisions = ReadRows<DecisionRun>(connection, transaction, "decisions");
        state.Operations = ReadRows<Operation>(connection, transaction, "operations");
        state.History = ReadRows<AuditEntry>(connection, transaction, "audit");
        state.Executions = [];
        using (var command = Command(connection, transaction, "SELECT decision_id,json,position FROM executions ORDER BY position;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (reader.GetInt64(2) != state.Executions.Count) throw new InvalidDataException("Execution receipt order is invalid.");
                state.Executions.Add(reader.GetString(0), Deserialize<ExecutionResult>(reader.GetString(1)));
            }
        if (!string.Equals(digest, Fingerprint(state), StringComparison.Ordinal))
            throw new InvalidDataException("Durable state checksum mismatch.");
        return new(state, mutationVersion, imported);
    }

    private static void ValidateNormalizedColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        // CHECK constraints protect normal writes; independently verify them on every read.
        // A maintenance connection can explicitly disable checks before editing a database.
        var mirrors = new (string Table, string Valid)[]
        {
            ("state_metadata", "singleton=1 AND revision>0 AND mutation_version>0 AND legacy_imported IN (0,1) AND length(state_hash)=64 AND json_valid(json) AND revision IS json_extract(json,'$.revision')"),
            ("trusted_context", "singleton=1 AND json_valid(json)"),
            ("policies", "id IS json_extract(json,'$.policyId') AND version>0 AND version IS json_extract(json,'$.policyVersion') AND status IN ('Published','Retired') AND status IS json_extract(json,'$.status')"),
            ("drafts", "id IS json_extract(json,'$.policyId') AND draft_id IS json_extract(json,'$.draftId') AND version>0 AND version IS json_extract(json,'$.policyVersion') AND json_extract(json,'$.status') IS 'Draft'"),
            ("resources", "id IS json_extract(json,'$.resourceId') AND tenant_id IS json_extract(json,'$.tenantId') AND product_id IS json_extract(json,'$.productId') AND save_count>=0 AND save_count IS json_extract(json,'$.saveCount')"),
            ("decisions", "id IS json_extract(json,'$.decisionId') AND scenario_id IS json_extract(json,'$.scenarioId')"),
            ("operations", "id IS json_extract(json,'$.operationId') AND decision_id IS json_extract(json,'$.decisionId') AND resource_id IS json_extract(json,'$.resourceId') AND meter_units=1 AND meter_units IS json_extract(json,'$.meterUnits')"),
            ("executions", "decision_id IS json_extract(json,'$.operation.decisionId') AND operation_id IS json_extract(json,'$.operation.operationId') AND json_extract(json,'$.accepted') IS 1"),
            ("audit", "id IS json_extract(json,'$.id') AND revision>0 AND revision IS json_extract(json,'$.revision')")
        };
        foreach (var (table, valid) in mirrors)
            if (Convert.ToInt64(Scalar(connection, transaction, $"SELECT COUNT(*) FROM {table} WHERE NOT COALESCE(json_valid(json) AND ({valid}),0);")) != 0)
                throw new InvalidDataException("Normalized database columns disagree with their validated JSON record: " + table);
        foreach (var table in new[] { "state_metadata", "trusted_context" })
            if (Convert.ToInt64(Scalar(connection, transaction, $"SELECT COUNT(*) FROM {table};")) != 1)
                throw new InvalidDataException("Database singleton record count is invalid: " + table);
    }

    private static void WriteRows(SqliteConnection connection, SqliteTransaction transaction, WorkbenchState state, long mutationVersion, bool imported)
    {
        Execute(connection, transaction, "DELETE FROM executions; DELETE FROM operations; DELETE FROM decisions; DELETE FROM resources; DELETE FROM audit; DELETE FROM drafts; DELETE FROM policies; DELETE FROM trusted_context; DELETE FROM state_metadata;");
        var metadata = new WorkbenchState
        {
            Revision = state.Revision, ProjectionState = state.ProjectionState,
            Admitted = state.Admitted, PeriodId = state.PeriodId, Tokens = state.Tokens,
            LastRefillAt = state.LastRefillAt, LastAdmittedAt = state.LastAdmittedAt
        };
        Execute(connection, transaction, "INSERT INTO state_metadata VALUES(1,$revision,$mutation,$imported,$hash,$json);",
            ("$revision", state.Revision), ("$mutation", mutationVersion), ("$imported", imported ? 1 : 0),
            ("$hash", Fingerprint(state)), ("$json", Serialize(metadata)));
        Execute(connection, transaction, "INSERT INTO trusted_context VALUES(1,$json);", ("$json", Serialize(state.TrustedContext)));
        for (var i = 0; i < state.Policies.Count; i++)
        {
            var p = state.Policies[i];
            Execute(connection, transaction, "INSERT INTO policies VALUES($id,$version,$status,$position,$json);", ("$id", p.PolicyId), ("$version", p.PolicyVersion), ("$status", p.Status), ("$position", i), ("$json", Serialize(p)));
        }
        for (var i = 0; i < state.Drafts.Count; i++)
        {
            var p = state.Drafts[i];
            Execute(connection, transaction, "INSERT INTO drafts VALUES($id,$draft,$version,$position,$json);", ("$id", p.PolicyId), ("$draft", p.DraftId!), ("$version", p.PolicyVersion), ("$position", i), ("$json", Serialize(p)));
        }
        for (var i = 0; i < state.Resources.Count; i++)
        {
            var r = state.Resources[i];
            Execute(connection, transaction, "INSERT INTO resources VALUES($id,$tenant,$product,$count,$position,$json);", ("$id", r.ResourceId), ("$tenant", r.TenantId), ("$product", r.ProductId), ("$count", r.SaveCount), ("$position", i), ("$json", Serialize(r)));
        }
        for (var i = 0; i < state.Decisions.Count; i++)
        {
            var d = state.Decisions[i];
            Execute(connection, transaction, "INSERT INTO decisions VALUES($id,$scenario,$position,$json);", ("$id", d.DecisionId), ("$scenario", d.ScenarioId), ("$position", i), ("$json", Serialize(d)));
        }
        for (var i = 0; i < state.Operations.Count; i++)
        {
            var o = state.Operations[i];
            Execute(connection, transaction, "INSERT INTO operations VALUES($id,$decision,$resource,$units,$position,$json);", ("$id", o.OperationId), ("$decision", o.DecisionId), ("$resource", o.ResourceId), ("$units", o.MeterUnits), ("$position", i), ("$json", Serialize(o)));
        }
        var index = 0;
        foreach (var (id, receipt) in state.Executions)
            Execute(connection, transaction, "INSERT INTO executions VALUES($decision,$operation,$position,$json);", ("$decision", id), ("$operation", receipt.Operation!.OperationId), ("$position", index++), ("$json", Serialize(receipt)));
        for (var i = 0; i < state.History.Count; i++)
        {
            var a = state.History[i];
            Execute(connection, transaction, "INSERT INTO audit VALUES($id,$revision,$position,$json);", ("$id", a.Id), ("$revision", a.Revision), ("$position", i), ("$json", Serialize(a)));
        }
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidDataException("Missing JSON record.");
    private static string Fingerprint(WorkbenchState state) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state, Json)));
    private static List<T> ReadRows<T>(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = Command(connection, transaction, $"SELECT json,position FROM {table} ORDER BY position;");
        using var reader = command.ExecuteReader(); var result = new List<T>();
        while (reader.Read())
        {
            if (reader.GetInt64(1) != result.Count) throw new InvalidDataException("Database entity order is invalid: " + table);
            result.Add(Deserialize<T>(reader.GetString(0)));
        }
        return result;
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    { using var command = Command(connection, transaction, sql); return command.ExecuteScalar(); }
    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); command.ExecuteNonQuery(); }

    private const string Schema = """
        PRAGMA user_version=1;
        CREATE TABLE state_metadata(singleton INTEGER PRIMARY KEY CHECK(singleton=1), revision INTEGER NOT NULL CHECK(revision>0), mutation_version INTEGER NOT NULL CHECK(mutation_version>0), legacy_imported INTEGER NOT NULL CHECK(legacy_imported IN (0,1)), state_hash TEXT NOT NULL CHECK(length(state_hash)=64), json TEXT NOT NULL CHECK(json_valid(json) AND json_extract(json,'$.revision')=revision));
        CREATE TABLE trusted_context(singleton INTEGER PRIMARY KEY CHECK(singleton=1), json TEXT NOT NULL CHECK(json_valid(json)));
        CREATE TABLE policies(id TEXT PRIMARY KEY, version INTEGER NOT NULL CHECK(version>0), status TEXT NOT NULL CHECK(status IN ('Published','Retired')), position INTEGER NOT NULL UNIQUE, json TEXT NOT NULL CHECK(json_valid(json) AND json_extract(json,'$.policyId')=id AND json_extract(json,'$.policyVersion')=version AND json_extract(json,'$.status')=status));
        CREATE TABLE drafts(id TEXT PRIMARY KEY, draft_id TEXT NOT NULL UNIQUE, version INTEGER NOT NULL CHECK(version>0), position INTEGER NOT NULL UNIQUE, json TEXT NOT NULL CHECK(json_valid(json) AND json_extract(json,'$.policyId')=id AND json_extract(json,'$.draftId')=draft_id AND json_extract(json,'$.policyVersion')=version AND json_extract(json,'$.status')='Draft'));
        CREATE TABLE resources(id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, product_id TEXT NOT NULL, save_count INTEGER NOT NULL CHECK(save_count>=0), position INTEGER NOT NULL UNIQUE, json TEXT NOT NULL CHECK(json_valid(json) AND json_extract(json,'$.resourceId')=id AND json_extract(json,'$.tenantId')=tenant_id AND json_extract(json,'$.productId')=product_id AND json_extract(json,'$.saveCount')=save_count));
        CREATE TABLE decisions(id TEXT PRIMARY KEY, scenario_id TEXT NOT NULL, position INTEGER NOT NULL UNIQUE, json TEXT NOT NULL CHECK(json_valid(json) AND json_extract(json,'$.decisionId')=id AND json_extract(json,'$.scenarioId')=scenario_id));
        CREATE TABLE operations(id TEXT PRIMARY KEY, decision_id TEXT NOT NULL UNIQUE REFERENCES decisions(id), resource_id TEXT NOT NULL REFERENCES resources(id), meter_units INTEGER NOT NULL CHECK(meter_units=1), position INTEGER NOT NULL UNIQUE, json TEXT NOT NULL CHECK(json_valid(json) AND json_extract(json,'$.operationId')=id AND json_extract(json,'$.decisionId')=decision_id AND json_extract(json,'$.resourceId')=resource_id AND json_extract(json,'$.meterUnits')=meter_units));
        CREATE TABLE executions(decision_id TEXT PRIMARY KEY REFERENCES decisions(id), operation_id TEXT NOT NULL UNIQUE REFERENCES operations(id), position INTEGER NOT NULL UNIQUE, json TEXT NOT NULL CHECK(json_valid(json) AND json_extract(json,'$.accepted')=1 AND json_extract(json,'$.operation.decisionId')=decision_id AND json_extract(json,'$.operation.operationId')=operation_id));
        CREATE TABLE audit(id TEXT PRIMARY KEY, revision INTEGER NOT NULL CHECK(revision>0), position INTEGER NOT NULL UNIQUE, json TEXT NOT NULL CHECK(json_valid(json) AND json_extract(json,'$.id')=id AND json_extract(json,'$.revision')=revision));
        """;
}
