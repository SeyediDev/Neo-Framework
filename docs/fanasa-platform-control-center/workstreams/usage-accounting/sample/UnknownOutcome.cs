using System.Text.Json;
using Fanasa.AccessManagement.Web.Persistence;

namespace UsageAccounting.Sample;

public sealed record BackendCompletion(string BusinessKey, JsonElement Event);
public sealed record BackendAttempt(string BusinessKey, string Fingerprint, string Status);

// A durable file stands in for a backend status endpoint; all data are synthetic and local.
public sealed class FixtureBackend(string path)
{
    public int Executions { get; private set; }
    public int Queries { get; private set; }
    public void ExecuteThenLoseResponse(FixtureEvent message)
    {
        Executions++;
        if (File.Exists(path)) throw new InvalidOperationException("The business operation already exists; query status instead.");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, new BackendCompletion(message.BusinessKey, message.Payload)); stream.Flush(true); }
        throw new TimeoutException("The response was lost after the backend committed.");
    }
    public BackendCompletion? Query(string sameBusinessKey)
    {
        Queries++;
        if (!File.Exists(path)) return null;
        var completion = JsonSerializer.Deserialize<BackendCompletion>(File.ReadAllText(path))!;
        return completion.BusinessKey == sameBusinessKey ? completion : null;
    }
}

public sealed class UnknownOutcomeGate(FabricDatabase database)
{
    private const string Attempts = "proposed.backend.attempt.v1";
    public void ExecuteOnce(FixtureEvent message, FixtureBackend backend)
    {
        database.Transaction(() =>
        {
            if (database.Read<BackendAttempt>(Attempts, message.BusinessKey) is { } prior)
                throw new InvalidOperationException("Blind replay blocked; status is " + prior.Status + ". Reconcile this same business key.");
            database.Put(Attempts, message.BusinessKey, new BackendAttempt(message.BusinessKey, message.OperationFingerprint, "in-flight")); return true;
        });
        try { backend.ExecuteThenLoseResponse(message); }
        catch (TimeoutException)
        {
            database.Transaction(() =>
            { database.Put(Attempts, message.BusinessKey, new BackendAttempt(message.BusinessKey, message.OperationFingerprint, "unknown")); return true; });
            throw;
        }
    }
    public bool Reconcile(FixtureEvent admitted, FixtureBackend backend, ProposedUsageIntake intake)
    {
        var attempt = database.Read<BackendAttempt>(Attempts, admitted.BusinessKey) ?? throw new InvalidOperationException("No pending attempt.");
        if (attempt.Fingerprint != admitted.OperationFingerprint) throw new PayloadConflict("Reconciliation binding changed.");
        var completion = backend.Query(admitted.BusinessKey);
        if (completion is null) return false; // Absence in a non-authoritative observation is still unknown.
        var measured = new FixtureEvent(completion.Event);
        if (measured.BusinessKey != admitted.BusinessKey || measured.OperationFingerprint != attempt.Fingerprint)
            throw new PayloadConflict("Backend returned a different operation receipt.");
        // Intake commits or durably retains pending evidence independently. Wrapping it in an
        // outer transaction would roll back its pending record when IntegrationPending is thrown.
        intake.Accept(measured);
        database.Transaction(() =>
        {
            database.Put(Attempts, admitted.BusinessKey, attempt with { Status = "completed-and-accounted" }); return true;
        });
        return true;
    }
}
