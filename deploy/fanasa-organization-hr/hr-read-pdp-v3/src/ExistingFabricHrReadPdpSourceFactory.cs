using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.ContractPolicy;

namespace Fanasa.HrReadPdp;

/// <summary>Adapts only an existing host Fabric instance. This factory accepts no database path and writes no authority data.</summary>
public sealed class ExistingFabricHrReadPdpSourceFactory : IHrReadPdpSourceFactory
{
    private readonly FabricDatabase database;
    private readonly ContractProjectionValidator validator;
    private readonly IHrReadPdpHostRecords? records;
    private readonly TimeProvider time;
    public ExistingFabricHrReadPdpSourceFactory(FabricDatabase existingDatabase, ContractProjectionValidator validator,
        IHrReadPdpHostRecords? records = null, TimeProvider? time = null)
    {
        database = existingDatabase ?? throw new ArgumentNullException(nameof(existingDatabase));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.records = records; this.time = time ?? TimeProvider.System;
    }
    public IAccessAttributeSnapshotSource Create(HrReadPdpRequest request, AuthenticatedHrReadPdpCaller caller,
        TrustedAccessSnapshotQuery query) => new HrReadPdpFabricSource(database, validator, request, query, caller, records, time);
}
