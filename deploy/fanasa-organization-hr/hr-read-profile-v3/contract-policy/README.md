# Isolated HR read policy implementation

This candidate extends a pinned copy of the existing contract-policy evaluator,
projection validator, projector, PEP, and trusted snapshot provider. It does not
replace the accepted HR deployment or register services in the running host.
`source-pins.json` records the SHA256 of each original file before candidate edits.
No `bin` or `obj` files were copied from the original implementation.

The newly designed profile is schema `3.0.0`, profile
`fanasa.organization-hr-read/1`, and template `organization-hr-read/1`. It supports
only `organization.hr.chart.read` and `organization.hr.history.read`, both mapped
explicitly to scope `organization.hr.read` and operation group
`organization.hr.read`. The grant role is `HrReader`. The required scope is not
the action string. The observed existing product key is `organization-fabric`;
its audience is `fanasa-organization-fabric`. This profile grants no commands.

The decision and PEP freshness ceiling is five seconds. Host-side fresh gates,
current registry correlation, direct TLS, and admission completion remain required;
a local Permit alone cannot release a response. Allocation limits reuse the v2
limits schema, including its independent positive `configRevision`. Projection
events reject changes to limits under an unchanged revision and reject revision
regression. The copied projector owns no persistence or parallel allocation store.

Selection uses all four keys: consumer, profile, product, and contract. Duplicate
matches throw instead of choosing the first row. The trusted snapshot provider
requires this selector for v3 and correlates it with the returned projection.
It retains the previous null-selector behavior for v1/v2. All tenant, membership,
grant, status, time-window, version, zone, entitlement, and obligation gates still
apply. Unknown input schema versions are rejected before profile evaluation;
malformed input retains the existing v1 Indeterminate response envelope for error
protocol compatibility. Unknown versions are never validated as v1 projections.

HR ownership is explicit. `ownership` contains `kind`, `owner`, and a canonical
nonzero `registeredProductId` UUID. `Platform` requires owner `platform` and
`ownerTenantId: null`; `Tenant` requires an owner UUID equal to `ownerTenantId`.
The host must compare the product UUID and ownership against the current central
registry. A string `platform` is never treated as a tenant UUID. No real owner
tenant, contract, allocation, or grant was fabricated or provisioned here. The
legacy signed gateway context has not been extended by this policy library;
wire ownership and counterpart integration are separate host responsibilities.

Original v1/v2 schemas are byte-identical to their pinned sources. The optional
ownership field is omitted for legacy models, preserving legacy serialization.
The original regression program and fixtures are copied under `tests` and
`fixtures`; their source hashes are pinned too. Original source files and the
accepted deployment are unchanged.

The copied original regression project retains CS8601 as warnings: six legacy
fixture assignments read a known non-null owner tenant, while the extended DTO
now represents platform ownership with a nullable owner tenant. The original
test source is unchanged and all assertions still run. Other warnings remain
errors in the candidate.

Run the full original regression suite against this copied core:

```powershell
dotnet run --project contract-policy/tests/OriginalRegression/OriginalRegression.csproj
```

The candidate's main test executable also calls `ProfileTests.RunAsync`. Its
synthetic Platform/Tenant ownership UUIDs, contracts, roles, grants and limits
are explicitly local fixtures. Passing them demonstrates profile behavior;
it does not demonstrate APISIX, live registry provisioning, TLS or deployment
acceptance.
