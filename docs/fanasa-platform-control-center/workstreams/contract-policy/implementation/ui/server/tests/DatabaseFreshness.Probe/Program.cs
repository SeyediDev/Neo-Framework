using System.Security.Cryptography;
using System.Text.Json;
using Fanasa.ContractPolicy;
using Fanasa.ContractPolicy.Workbench;
using Microsoft.Data.Sqlite;

if (args.Length is not (3 or 4)) throw new ArgumentException("Expected database directory, state JSON, native schema directory and optional revoke/restore mode.");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var state = JsonSerializer.Deserialize<WorkbenchState>(File.ReadAllText(args[1]), json)
    ?? throw new InvalidDataException("Exported test state is missing.");
var module = new ContractPolicyModule(args[2]);
void Validate(WorkbenchState candidate)
{
    Require(module.Projections.Validate(candidate.ProjectionState.Projection).Count == 0, "Test projection is valid.");
    Require(module.Schemas.Validate("request-context.v2.schema.json", ContractJson.ToNode(candidate.TrustedContext)).Count == 0,
        "Test source context is valid.");
}
string Hash(WorkbenchState candidate) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(candidate, json)));
WorkbenchState Clone(WorkbenchState candidate) => JsonSerializer.Deserialize<WorkbenchState>(JsonSerializer.Serialize(candidate, json), json)!;

void Maintain(WorkbenchState maintained)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(Path.GetFullPath(args[0]), "workbench.db"), Pooling = false }.ToString());
    connection.Open();
    using var transaction = connection.BeginTransaction();
    using var source = connection.CreateCommand();
    source.Transaction = transaction;
    source.CommandText = "UPDATE trusted_context SET json=$context WHERE singleton=1;";
    source.Parameters.AddWithValue("$context", JsonSerializer.Serialize(maintained.TrustedContext, json));
    Require(source.ExecuteNonQuery() == 1, "One maintained trusted context exists.");
    using var digest = connection.CreateCommand();
    digest.Transaction = transaction;
    digest.CommandText = "UPDATE state_metadata SET state_hash=$hash WHERE singleton=1;";
    digest.Parameters.AddWithValue("$hash", Hash(maintained));
    Require(digest.ExecuteNonQuery() == 1, "One maintained digest exists.");
    transaction.Commit();
}

Directory.CreateDirectory(args[0]);
Validate(state);
var database = new WorkbenchDatabase(args[0]);
if (args.Length == 4)
{
    Require(args[3] is "revoke" or "restore", "Only coherent revoke/restore maintenance is supported.");
    Require(database.Exists, "Maintenance uses an existing isolated database.");
    var existing = database.Read();
    Require(Hash(existing.State) == Hash(state), "Export matches the current database before maintenance.");
    var changed = Clone(existing.State);
    var active = args[3] == "restore";
    changed.TrustedContext = changed.TrustedContext! with { Grant = changed.TrustedContext!.Grant with { Active = active } };
    Validate(changed);
    Maintain(changed);
    var checkedChange = database.Read();
    Require(checkedChange.MutationVersion == existing.MutationVersion && checkedChange.State.TrustedContext!.Grant.Active == active,
        "Coherent maintenance is readable without a version bump.");
    Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", mode = args[3], mutationVersionRetained = true, active }));
    return;
}
database.Initialize(state, false);
var original = database.Read();
var staleAction = Clone(original.State);
staleAction.Revision++;
var maintained = Clone(original.State);
maintained.TrustedContext = maintained.TrustedContext! with { Grant = maintained.TrustedContext!.Grant with { Active = false } };
Validate(maintained);

// A coherent maintenance write changes the trusted grant and its digest, while accidentally retaining the version.
Maintain(maintained);

var maintenance = database.Read();
Require(maintenance.MutationVersion == original.MutationVersion, "Maintenance did not bump the version.");
Require(!maintenance.State.TrustedContext!.Grant.Active, "Coherent maintenance source change is readable.");
try
{
    database.Write(staleAction, original.MutationVersion, Hash(original.State), Validate);
    throw new InvalidOperationException("A stale action overwrote the coherent maintenance edit.");
}
catch (WorkbenchError error) when (error.Status == 409 && error.Code == "storage-conflict") { }
var preserved = database.Read();
Require(!preserved.State.TrustedContext!.Grant.Active && preserved.State.Revision == original.State.Revision
    && preserved.MutationVersion == original.MutationVersion, "Rejected stale action leaves maintained state untouched.");

var freshAction = Clone(preserved.State);
freshAction.Revision++;
var nextVersion = database.Write(freshAction, preserved.MutationVersion, Hash(preserved.State), Validate);
var afterFresh = database.Read();
Require(nextVersion == original.MutationVersion + 1 && afterFresh.MutationVersion == nextVersion
    && !afterFresh.State.TrustedContext!.Grant.Active, "A freshly read action commits while retaining the grant change.");
Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", checks = 4, provider = "SQLite",
    staleWriteRejected = true, maintenancePreserved = true, freshWriteSucceeded = true }));

static void Require(bool value, string message)
{ if (!value) throw new InvalidOperationException(message); }
