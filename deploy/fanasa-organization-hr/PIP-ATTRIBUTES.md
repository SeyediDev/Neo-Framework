# HR attributes ingestion

The standalone [offline deployment preflight](pip-preflight/README.fa.md) checks a nonsecret provider/consumer plan against this contract. Run it from the repository root before deployment review. It is not part of runtime or CI and never establishes live acceptance, credentials, grants or routes.

The attributes adapter supplies current organization membership and effective appointments to the PIP. It does not claim legal employment. The endpoint is additive; chart, history, commands and their existing policy gates remain unchanged.

## Response and authority

`GET /api/organization-hr/v1/tenants/{tenantId}/attributes?subject={canonical-keycloak-subject}` returns schema 2:

```json
{
  "schemaVersion": 2,
  "tenantId": "11111111-1111-4111-8111-111111111111",
  "subject": "22222222-2222-4222-8222-222222222222",
  "sourceVersion": "hr-r7-unique-observation-id",
  "observedAt": "2026-10-24T10:00:00Z",
  "validUntil": "2026-10-24T10:00:20Z",
  "attributes": {
    "org.member.active": "true",
    "org.assignment.active": "true",
    "org.assignment.count": "1",
    "org.unit.code": "engineering",
    "org.position.code": "engineer",
    "org.role.code": "staff"
  }
}
```

All attribute values are typed strings according to the source definitions. Required fields are `org.member.active` and `org.assignment.active` (boolean), and `org.assignment.count` (integer). Optional `org.unit.code`, `org.position.code`, and `org.role.code` are strings. Scalar unit/position/role fields are omitted when zero or multiple assignments are effective. A known inactive member returns false membership and zero assignments; an unknown subject returns 404. An effective appointment with a missing/inactive position/unit or missing referenced role returns 503, preventing inconsistent authoritative facts from being cached.

Appointments use `From <= now < To`. Observation time is captured inside the same Fabric read snapshot as membership/chart reads. Every observation gets a unique source version. `validUntil` is the earlier of the bounded observation lifetime and the next appointment start/end. PIP freshness is the earlier of that deadline and `observedAt + MaxAgeSeconds`; at the exact deadline it is stale. Schema 2 hashes bind this deadline. Schema 1 retains its original hash bytes and excludes `validUntil` entirely.

The reader endpoint uses the existing signed JWT scheme and real gateway mTLS certificate, with a separate exact reader allowlist. Each reader requires the configured issuer/audience, exact service subject/client, scope, active registered product client, active service membership, tenant allowlist and live tenant `organization.read` grant. The reader boundary directly checks the active organization subscription and its start/end before and after projection, even when the general development `Organization:RequireSubscription` option is false. `OrganizationStore.Read` also preserves its configured subscription check. This bootstrap route does not invoke a policy that depends on the attributes it supplies. A reader grant/token expiration stops subsequent reads; already ingested facts remain bounded by their existing freshness deadline, as with any cached authoritative observation.

## Deployment configuration template

Replace synthetic IDs and host/address placeholders with approved deployment values. This file creates no reader identity, grants, secret, network rule or route. Empty `Readers` disables all attribute readers. Register the dedicated reader as an active client of `organization-fabric`, provision its exact scope and tenant membership/read grant, and supply its client secret through deployment secret injection.

```json
{
  "Organization": {
    "HrAttributes": {
      "ObservationLifetimeSeconds": 60,
      "Readers": [{
        "ClientId": "fanasa-organization-pip-reader",
        "ServiceSubject": "33333333-3333-4333-8333-333333333333",
        "Scope": "organization.hr.attributes.read",
        "TenantIds": ["11111111-1111-4111-8111-111111111111"]
      }]
    }
  },
  "Pip": {
    "OAuth": { "Profiles": [{
      "TenantId": "11111111-1111-4111-8111-111111111111",
      "Reference": "organization-hr-reader",
      "TokenEndpoint": "https://sso.fanasa.net.local/realms/fanasa/protocol/openid-connect/token",
      "ClientId": "fanasa-organization-pip-reader",
      "ClientSecretReference": "organization_hr_client_secret",
      "Scopes": ["organization.hr.attributes.read"],
      "AllowedPrivateAddresses": ["10.20.0.10"],
      "CaFile": "/certs/fanasa-root-ca.crt",
      "TimeoutSeconds": 10
    }] },
    "Transport": { "Bindings": [{
      "TenantId": "11111111-1111-4111-8111-111111111111",
      "Endpoint": "https://api.fanasa.net.local/development/organization-hr/v1/tenants/11111111-1111-4111-8111-111111111111/attributes",
      "SecretReference": "organization-hr-reader",
      "AllowedPrivateAddresses": ["10.20.0.11"],
      "CaFile": "/certs/fanasa-root-ca.crt"
    }] }
  }
}
```

The injected environment key `Pip__Secrets__organization_hr_client_secret` contains the real secret; never place its value in source JSON, Fabric, screenshots or logs. OAuth client credentials are posted to the exact deployment token endpoint. Tokens live only in memory, concurrent refreshes are serialized, and refresh begins before expiry. Returned scope, when supplied, must equal the requested scope set. Static secret references remain supported. Profile references are unique and bound to one tenant; startup rejects cross-tenant use. Configure separate profiles for separate tenants.

For systemd injection, environment variable names must use ASCII letters, digits and underscores and must not start with a digit; use `organization_hr_client_secret` as the secret reference. Do not put `__` inside an environment-backed secret reference: ASP.NET converts it to the `:` configuration separator, so the original reference no longer resolves.

Each endpoint/profile may use its own CA file. Certificate hostname, validity, chain and serverAuth purpose are verified. AIA certificate downloading is disabled. The custom CA replaces system roots only for that exact binding. Restart after changing bindings, credentials or CA files to invalidate token/client caches. Public addresses are eligible; private addresses require an exact deployment-owned RFC1918/ULA exception. Loopback/link-local addresses, redirects, proxy use and DNS results outside this boundary remain rejected. Network policy must explicitly permit both the token endpoint and gateway; do not relax global egress.

Register the PIP source disabled, with schema 2 and exactly the six definitions above (the last three optional), the same endpoint/reference/tenant as the binding, and explicit canonical subject IDs. A useful starting choice is `MaxAgeSeconds=60`, `PollIntervalSeconds=30`, `TimeoutSeconds=10`, `MaxResponseBytes=65536`. Successful setup preview verifies a fresh correlated observation; enable only after reviewing that evidence. A known transition earlier than the polling interval can create a short stale period, during which policy evaluation fails closed until synchronization supplies a new observation.

## Additive gateway snippet

Review and merge the following locations into the existing `api.fanasa.net.local` server after provisioning the reader. This is a documentation snippet, not an automatically applied Nginx change. Keep the current chart/history/commands locations and the `organization-hr-proxy.conf` TLS/header policy.

```nginx
location = /_fanasa_hr_attributes_access {
    internal;
    proxy_method GET;
    proxy_pass_request_body off;
    proxy_set_header Content-Length "";
    proxy_pass https://127.0.0.1:5193/api/organization-hr/v1/tenants/$hr_attributes_tenant/attributes-access;
    include /certs/organization-hr-proxy.conf;
}

location ~ "^/development/organization-hr/v1/tenants/(?<hr_attributes_tenant>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/attributes$" {
    if ($request_method != GET) { return 405; }
    auth_request /_fanasa_hr_attributes_access;
    limit_req zone=fanasa_hr_development burst=10 nodelay;
    limit_req_status 429;
    proxy_pass https://127.0.0.1:5193/api/organization-hr/v1/tenants/$hr_attributes_tenant/attributes$is_args$args;
    include /certs/organization-hr-proxy.conf;
}
```

Forwarding the original query is required for subject correlation. The application rejects unknown/duplicate query keys and a tenant query that differs from the route. `attributes-access` is an internal gateway authorization subrequest; external callers cannot access the Nginx internal location. Both subrequest and data request enforce the dedicated reader boundary. Run `nginx -t` before any authorized rollout, then test valid/expired tokens, missing gateway certificate, cross-tenant subject, exact appointment end, source disablement and PDP denial on stale data.
