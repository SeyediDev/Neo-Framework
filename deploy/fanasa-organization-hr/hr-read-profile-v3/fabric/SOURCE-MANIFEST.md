# Local Fabric receiver adapter

This project is a local executable candidate, with no live deployment, central authorization grant, allocation provisioning, APISIX registration or operational acceptance. The default `IHrReadCurrentGuard` denies. Constructing `HrReadFabricAdmission` is evidence construction, not authorization. Host integration must supply a trusted request-scoped guard for existing authoritative records and the shared existing Fabric instance.

`FabricDatabase.cs` is copied byte for byte from `products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/FabricDatabase.cs` in the inspected working tree. SHA-256:

`7de828e9e414b1c9cd2582e50b72bf2270fd43263429690bec91820252eea38b`

The typed organization record shapes and empty-chart/history behavior were inspected in `products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/OrganizationFabric.cs`, SHA-256:

`92205ffb5d9e31bc03fb3fd4f56a1710c86c39951c188acab9d4c73a121af1c3`

These hashes pin working-tree inputs, not a claim that this entire candidate is source release597. `Microsoft.Data.Sqlite` is pinned to the repository's existing version10.0.8 in local central package settings. No other application dependency is copied.

The replay receipt remains `Tier`, `RequestId`, `KeyId`, `ExpiresAt` in the existing `contract-policy.gateway-context.replay.v1` Fabric document bucket, keyed by `tier:requestId`. Key rotation does not change a consumed nonce key. The adapter creates no parallel store, authorization registry, quota table or allocation ledger. Post-claim vetoes, guard exceptions and cancellation retain the committed nonce; a failed guard before the claim writes nothing.

Each operation leaves its synchronous Fabric transaction/snapshot before returning. The nonce claim uses an independent write transaction. The materialized read runs before/after guards in a query-only snapshot. The coordinator serializes complete bounded response bytes after that snapshot closes; a separate final snapshot rechecks current guard state. No `await` or transactional asynchronous provider executes inside these contexts.

The source JSON byte cap is explicit per admission and checked using strict UTF-8 before JSON parsing, typed deserialization and detached materialization. The existing `FabricDatabase.ReadDocument` API has already allocated SQLite's returned string by this point. This candidate therefore does **not** enforce a pre-allocation SQL byte limit; that would require a reviewed change to the shared Fabric read API. The local absolute16MiB source ceiling is an allocation guardrail, not an operational allowance. Response bytes are independently limited by the coordinator.

Storage uses the actual PascalCase typed record schema. Output uses ASP.NET Web JSON camelCase, including nested fields and `roles:null` when roles are absent. An authorized existing tenant with no organization document returns an empty chart at revision0, matching `OrganizationStore.Read`; tenant/subscription existence itself is the guard's responsibility. History is positive Int64, must not exceed current revision, and uses the exact tenant:revision history key. Missing history is404 only after the coordinator's same fresh gate. Mismatched tenant/revision, malformed, incomplete, unknown or recursively duplicate stored fields fail closed. Required constructor parameters and nonnullable members are enforced, including nested names; null array items are rejected. These are bounded structural and target checks, not a replacement for the domain's command validation, graph/relationship invariants, active tenant/subscription checks or authoritative current guard.

`FabricTests` creates fresh temporary SQLite databases. Its explicit fixture guard and fixture authority are test inputs; they do not substitute for authenticated introspection, native TLS, central policy records or live Gateway acceptance. Tests cover persistent replay across reopen/key change, concurrent claim serialization, query-only write rejection, late-veto retention, bounded UTF-8 materialization, actual response shape, Int64 history, and a revocation committed through a second database connection after the original read snapshot ends but before the final new snapshot.
