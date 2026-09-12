# Neo Doctor teaching cases

Each directory is a separate scan root. DirectRegistration, MissingRequester and Healthy are compiled by the Companion tests; they reference the existing TelemetryDemo only for its synthetic requester. InvalidConfig contains configuration used by an exporter integration test. These fixtures are intentionally excluded from ordinary application recommendations.

| Root | Observed runtime behavior | Expected diagnostic |
|---|---|---|
| DirectRegistration | The service returns `found`, but no span reaches its listener | NEO-TEL001 |
| MissingRequester | Resolving IOrders fails because IRequesterUser is absent | NEO-DI001 |
| Healthy | Resolving IOrders succeeds and orders.lookup is observed with Ok status | No findings; runtime is separately verified by the test |
| InvalidConfig | Resolving the trace provider fails on the malformed OTLP URI | NEO-CONFIG001 |

Set NEO_PROJECT_ROOT to the individual directory and call neo_diagnose_project. To run all actual behavior checks from the Neo repository root:

```bash
dotnet test tools/Neo.Companion/tests/Neo.Companion.Tests/Neo.Companion.Tests.csproj --filter FullyQualifiedName~Doctor
```

The cases are teaching libraries rather than standalone hosts. Do not scan their common parent as if all registrations belonged to the same application. Doctor does not model runtime control flow or execute these projects during inspection.
