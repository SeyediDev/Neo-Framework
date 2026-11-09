# Mobile self catalog boundary checks

Run from the Neo repository root:

```powershell
dotnet run --project products/Fanasa.AccessManagement/tests/MobileApplicationCatalog.Tests/MobileApplicationCatalog.Tests.csproj --no-launch-profile
```

The test uses production controllers through loopback Kestrel and real JWT middleware. All RSA keys, subjects, central registrations, memberships, offers, grants and databases are disposable synthetic fixtures. Each HTTP check proves that both SQLite schemas and all persisted rows remain unchanged.

Coverage includes disabled configuration, bearer-only authentication, signature/audience/expiry, exact approved public client, current scope/redirect registration, self-only subject extraction, denied delegation queries and headers, cross-tenant denial, redacted SaaS metadata, immediate membership and grant withdrawal, unavailable metadata and absent historical schema. Public application URLs at the approved apex and one-label hosts are covered alongside rejection of nested and suffix-confusion names. It also checks that the existing service delegation endpoint rejects the public client.

This is local API acceptance. It does not create a real client, change Keycloak, supply an application grant, publish a release, or verify mobile browser SSO, TLS, network reachability or Android App Links.
