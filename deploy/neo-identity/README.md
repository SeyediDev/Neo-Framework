# Neo Identity deployment

This stack provides the independent identity boundary for Fanasa SaaS:

- Keycloak is the local identity broker and authorization server.
- PostgreSQL is dedicated to Keycloak and is not shared with orchestration data.
- The `fanasa` login theme is mounted from `keycloak/themes/fanasa` and keeps the
  Keycloak login experience aligned with the Fanasa brand book.
- The Keycloak HTTP port is bound to `127.0.0.1:18080` by default; publish it
  through the existing HTTPS reverse proxy only after the public hostname and
  TLS certificate are ready.

## Install on the VPS

Copy this directory to `/opt/neo-identity`, create a mode `0600` `.env`, and run:

```sh
docker compose --env-file .env up -d
docker compose --env-file .env ps
curl --fail http://127.0.0.1:18080/realms/master/.well-known/openid-configuration
```

After the first realm bootstrap, select the custom theme for the `fanasa` realm
from **Realm settings → Themes → Login theme → fanasa**, or apply it with the
Keycloak Admin API as part of the server bootstrap. Restarting the container is
not enough to change an already-created realm's `loginTheme` setting.

Required secrets:

```dotenv
KEYCLOAK_DB_PASSWORD=generated-long-secret
KEYCLOAK_ADMIN_USERNAME=admin
KEYCLOAK_ADMIN_PASSWORD=generated-long-secret
KEYCLOAK_HTTP_PORT=18080
```

The bootstrap admin password is only read when the Keycloak database is first
created. Store it in the server secret file and rotate it after first login.
Do not commit `.env`, export it in CI, or put it in application settings.

The initial VPS setup also creates the `fanasa` realm, a local `admin` user and the
`platform-admin` realm role. This is a bootstrap account only; after the Fanasa
broker is configured, prefer centrally managed human identities and disable or
rotate the bootstrap credential.

## Fanasa central SSO

After the local service is healthy, configure a broker connection in the Neo
realm. The exact values must come from Fanasa security administration:

- OIDC issuer/discovery URL or SAML metadata URL;
- client ID and secret/certificate registration;
- allowed redirect/logout URLs for the Neo hostname;
- stable subject/email claims;
- tenant, organization and human-role claims;
- logout and MFA requirements.

Until these values are supplied, the local admin login is intentionally the only
enabled login. Neo applications must trust the realm issuer, not call Keycloak's
admin API and not own passwords.

## Identity model

Keep these records in the separate Identity/Tenant domain:

- `HumanIdentity`: immutable external subject plus normalized profile;
- `Tenant`: SaaS customer boundary;
- `TenantMembership`: identity-to-tenant relationship;
- `RoleAssignment`: tenant-scoped application role;
- `ExternalIdentityLink`: provider and subject mapping.

The orchestration domain consumes stable identity and authorization claims through
an adapter. It must not contain Keycloak entities or provider-specific code.
