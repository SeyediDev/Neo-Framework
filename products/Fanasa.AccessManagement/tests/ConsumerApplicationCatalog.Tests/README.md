# Consumer application catalog acceptance

Run from the repository root:

```powershell
dotnet run --project products/Fanasa.AccessManagement/tests/ConsumerApplicationCatalog.Tests/ConsumerApplicationCatalog.Tests.csproj
```

Disposable loopback Kestrel hosts the production ConsumerApplicationsApi and PlatformApi. Synthetic RSA-signed JWTs pass through real PlatformBearer middleware. Isolated SQLite fixtures represent existing Fabric documents and historical registry offer/grant rows. No remote database or identity is contacted.

Coverage: default-off and absent-schema behavior; service client/scope/type; active memberships; consumer and developer branches without fallback; different-owner SaaS and owner-only dedicated products; grant/offer/subscription validity; transport redaction; rejected query/header inputs; and unchanged schema plus every table in both databases after successful and denied requests. No distributed revocation guarantee is claimed between the separate snapshots.

The executable prints PASS lines and a final assertion count. Source files are not execution receipts; compilation and execution must be recorded separately.
