namespace Fanasa.ContractPolicy;

/// <summary>
/// Composes the policy module once at host startup. No host registrations,
/// database, network clients or background process are created here.
/// </summary>
public sealed class ContractPolicyModule
{
    public ContractSchemaValidator Schemas { get; }
    public ContractProjectionValidator Projections { get; }
    public ContractPolicyEvaluator Evaluator { get; }
    public ContractProjectionProjector Projector { get; }
    public ContractPolicyPep Pep { get; }

    public ContractPolicyModule(string schemaDirectory)
    {
        Schemas = new ContractSchemaValidator(schemaDirectory);
        Projections = new ContractProjectionValidator(Schemas);
        Evaluator = new ContractPolicyEvaluator(Projections);
        Projector = new ContractProjectionProjector(Projections);
        Pep = new ContractPolicyPep(Schemas);
    }

    public static ContractPolicyModule FromOutputDirectory() =>
        new(Path.Combine(AppContext.BaseDirectory, "Schemas"));
}
