# Neo Agent Orchestration VPS deployment

The GitHub workflow deploys verified local bundles only:

- `develop` → `development`
- `master` → `production`

Each environment has independent releases, systemd services and environment
files. Activation is atomic through `current`; older releases remain available
for rollback. The manifest is checked and bundles declaring credentials are
rejected.

## One-time host setup

Run `install-host.sh` as root on the VPS after installing the .NET 10 runtime
and `unzip`. It creates the restricted `neo-deploy` account, service units and
deployment directory. It does not create a database, run migrations, seed an
organization, or start an app.

Create `/etc/neo-agent-orchestration/development.env` and
`/etc/neo-agent-orchestration/production.env` with mode `0600`. Required values
include `NEO_ORCHESTRATION_SQL`, `NEO_API_PORT`, `NEO_WEB_PORT`, API JWT authority
and audience, and Web OIDC authority/client settings. Keep credentials out of
GitHub artifacts and repository files.

Database migration and seed remain explicit operations. Deployment does not
silently change a production schema. Verify health and run the reviewed
provisioning command before enabling the corresponding systemd units.

## GitHub environment secrets

Create `development` and `production` environments and add these secrets to each:

`DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY`, `DEPLOY_KNOWN_HOSTS`.

`DEPLOY_USER` is the restricted `neo-deploy` account, not root. Production can
also use a required reviewer in GitHub environment protection rules.

## Development SQL Server on the VPS

The VPS already has Docker. `docker-compose.development.yml` provides an
isolated SQL Server Developer container on loopback port `14333`, with a
separate named volume. It is intentionally development-only; production should
use a separately backed-up SQL Server catalog. Set `MSSQL_SA_PASSWORD` in the
remote `.env` file and run `docker compose --env-file .env -f
docker-compose.development.yml up -d` before the explicit product migration.
