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

Source of truth: `https://github.com/SeyediDev/Neo-Framework`, branch `develop`,
directory `deploy/neo-identity`. Keep a sparse checkout on the server at
`/opt/neo-framework`; deploy the theme from that checkout, not an untracked
local file. Preserve `.env` and backups outside Git. The HTTPS override is
versioned as `compose.https.yml`; set `KEYCLOAK_PUBLIC_URL` in the server `.env`.
Store the installed Git commit in `/opt/neo-identity/deployed-revision`.

```sh
git clone --filter=blob:none --sparse --branch develop https://github.com/SeyediDev/Neo-Framework.git /opt/neo-framework
git -C /opt/neo-framework sparse-checkout set deploy/neo-identity
```

The compact login layout is loaded from `css/layout-v3.css` to avoid serving the
old layout from Keycloak's gzip cache. Verify the rendered page's scroll height
at a 1366×636 viewport after deployment; do not hide vertical overflow, since
MFA and error pages may legitimately need scrolling.

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

## Fanasa login branding

The login theme uses the official horizontal Persian logo and SVG favicon, the
brand colors (#1F4B9A, #149A8E, #F6F1E7), subtle mihrab outlines and locally
hosted Vazirmatn. Font files retain the SIL OFL license. Asset provenance is in
`keycloak/themes/fanasa/provenance.json`.

The `template.ftl` override comes from the deployed Keycloak **26.4.7** bundled
`keycloak.v2` template; only the logo and favicon markup is changed. Compare it
with the bundled template when upgrading Keycloak. Password, error, session and
alternate authentication flows remain inherited.

For the Persian Fanasa installation set `loginTheme=fanasa`,
`internationalizationEnabled=true`, `supportedLocales=["fa"]` and
`defaultLocale=fa`. This prevents an English browser preference from selecting
English; translations also support English if explicitly enabled later.
The authenticated account/admin consoles have independent themes.

Back up the mounted theme before updating it. Restart Keycloak to clear template
caches and clear only `/opt/keycloak/data/tmp/kc-gzip-cache` if old static assets
are still served. Keep production theme caching enabled. When HTTPS overrides
exist, include both compose files when recreating the service:

```sh
docker compose --env-file .env -f docker-compose.yml -f compose.https.yml up -d
```

Validated on the VPS: desktop 1440px and mobile 390px login, Persian labels and
RTL, official logo, local font, password visibility, invalid-password feedback,
contract administrator/viewer permissions and shared login to the portal.

## Current local HTTPS endpoints

## Work Management session policy

`work-session-policy.json` defines the installed Work Management policy: two-hour
realm SSO idle, twelve-hour realm maximum, fifteen-minute access tokens for the
Work Management client (other clients' token lifetimes are unchanged), and a
twelve-hour absolute Web ticket. `apply-work-session-policy.py` previews by default;
use `--apply` after authenticating kcadm for client/Web settings only. By default
the client idle/max is bounded by the existing realm settings. Changing shared
realm timeouts requires explicit authorization and the additional `--include-realm`
flag; do not use it without approval covering all connected clients.
It backs up settings, merges client
attributes and writes the Web environment setting without exporting secrets.
Realm SSO settings affect all clients unless a client has a shorter override.
Deploy a Web version supporting `WebAuthentication__SessionLifetimeMinutes`
and restart only the Web to activate it. Existing tickets retain their earlier
deadline until reauthentication. The in-memory ticket store still logs users out
on Web restart; this policy does not introduce offline or never-expiring tokens.

## Explicit administrator enrollment

For a user-authorized platform administrator, create an enabled Keycloak account
and assign `platform-admin`. Generate its temporary password on the server and
require `UPDATE_PASSWORD`; never store the password in Git. The Access client
uses `platform-permission-mapper.json` to emit the administrator-only user
attribute `fanasa_permissions` as `permission`; set `platform.admin` only on
explicitly authorized administrator accounts. Ordinary users must not have
write access to this attribute.
Merge `platform-permission-profile-attribute.json` into the existing realm
`users/profile.attributes` list without replacing other attribute definitions.
Keycloak discards an unmanaged attribute unless its profile policy allows it;
verify the saved attribute with the Admin API before reporting admin access.

After authenticating `kcadm` inside the container, run the versioned maintenance
script to enroll that verified identity in existing active tenants:

```sh
python3 deploy/neo-identity/enroll-platform-admin.py --subject <verified-keycloak-subject> --username <verified-username>
```

The script backs up the databases, respects seat limits, writes durable tenant
memberships/grants and emits audit events. It does not create new tenants,
reactivate offboarded members, or assign administrator privileges inside
third-party products. Those products must map their own roles explicitly.

- `https://sso.fanasa.net.local`: Keycloak and the Fanasa OIDC issuer
  `https://sso.fanasa.net.local/realms/fanasa`.
- `https://platform.fanasa.net.local`: مرکز راهبری پلتفرم فن‌آسا
  (`Fanasa Platform Control Center`), the new canonical application host.
- `https://access.fanasa.net.local`: legacy compatibility host for that application.
- `https://panel.fanasa.net.local`: compatibility redirect to `access`.

The SSO hostname is distinct from the user management application. Configure
Keycloak `KC_HOSTNAME`, every application's OIDC Authority/issuer, and the local
hosts mappings together. Access management redirects and post-logout redirects
are registered on the `platform` host, retaining the previous `access` callbacks
during migration. Previous Keycloak bookmarks under
`access/realms/` and `access/admin/` redirect to `sso`; all active integrations
use the new issuer directly. The existing wildcard certificate covers both names.
When updating the issuer, users may need to sign in again.

For the application-host migration (not an issuer change), authenticate `kcadm`
in the Keycloak container and run `migrate-platform-host.py` first in preview mode,
then with `--apply`. It preserves legacy routing and callbacks, backs up routing
and environment files on the VPS, verifies nginx configuration, and rolls back
on deployment errors. Add only the new hostname to client DNS/hosts; do not change
certificate trust. Verify `/login` redirects with the new exact callback before
reporting the new link as deployed. Central SSO remains `sso.fanasa.net.local`.

Both .NET applications send `client_id` in the OIDC logout request while keeping
`SaveTokens=false`. Verified on the renamed hosts: fresh login, logout
confirmation, registered callback and removal of the application session cookie.
