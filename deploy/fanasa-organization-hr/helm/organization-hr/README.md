# Development HR Helm candidate

This chart prepares the Organization HR API for the dedicated
`fanasa-organization-hr-development` namespace. It renders one single-replica
Deployment with `Recreate`, one ClusterIP Service on TLS5193 and one bounded
NetworkPolicy. It installs no namespace, PVC, Secret, identity, grant, Gateway
route or Argo Application. Empty required values deliberately fail rendering.
There is no staging/production path or arbitrary command, environment, raw YAML,
source PVC or ingress input.

The two application containers share the Pod network. Web executes
`/app/web/Fanasa.AccessManagement.Web.dll`; native workbench executes
`/app/workbench/Fanasa.ContractPolicy.Workbench.dll`. Their independent immutable
OCI references must be bound to the same verified release bundle and matching
policy-engine assembly in the separate adapter receipt. Annotation hashes and
image digests identify a candidate; this chart does not verify a signer, produce
central trusted CI evidence or authorize promotion.

Only the HR HTTPS listener is reachable through the Service. Human web5191 and
native workbench4187 must remain loopback listeners. No Service, container port,
ingress or network allowance exposes them. The existing human guide and SSO
remain on the current systemd deployment. Moving the human host requires a
separate, tested HTTPS/trusted-proxy adapter; the current Web source trusts
forwarded headers only from loopback.

## Existing runtime references

Values contain references and redacted release identities, never secret values.
`configuration.existingSecret` supplies the complete JSON file under
`configuration.key` and the root-controlled deployment receipt under
`configuration.metadataKey`. The former mounts at
`/app/web/appsettings.Development.json`; both files also mount read-only under
`/runtime-config`, with metadata at `/runtime-config/deployment.json`.
`configurationSha256` is SHA256 of the **exact raw JSON bytes**, including
whitespace and final newline, not a normalized object. That digest is passed to
the initialization and live readiness verifier. Changing this value triggers a
new Pod. Configuration uses `subPath`; secret changes require a reviewed rollout.

The adapted external JSON configuration must explicitly use these paths and
boundaries, enforced by `/adapter/runtime_config.py` in the web image:

| Configuration | Required candidate value |
| --- | --- |
| `Kestrel:Endpoints:Human:Url` | `http://127.0.0.1:5191` |
| `Kestrel:Endpoints:OrganizationHr:Url` | `https://0.0.0.0:5193` |
| `Fabric:DataDirectory` | `/state/web/fabric` |
| `Platform:RegistryPath` | `/state/web/platform-registry.db` |
| `Fabric:DataProtectionKeysPath` | `/state/web/keys` |
| `Organization:DataDirectory` | `/state/web/organization` |
| `Accounting:DataDirectory` | `/state/web/accounting` |
| `Organization:RequireSubscription` | `true`, explicitly set |
| `Organization:HrApi:DeploymentMetadataPath` | `/runtime-config/deployment.json` |
| `Authentication:Authority` and HR authority | `https://sso.fanasa.net.local/realms/fanasa` |
| `Platform:CaFile` | `/tls/platform-ca.crt` |
| HR server certificate and key | `/tls/server.crt`, `/tls/server.key` |

The configuration must retain the approved HR service client, subject, audience,
distinct scopes, actual Gateway certificate pin and exact host allowlist, including
`organization-hr-upstream.fanasa.net.local`. HR is explicitly enabled; legacy import,
automatic bootstrap enrollment, payments and outbox dispatch are disabled in this
candidate. The chart does not synthesize those settings or create an identity.
Only the existing human SSO client secret belongs in runtime configuration;
Keycloak administrator credentials and HR service client secrets are unnecessary
for server-side token verification and must not be mounted.

`tls.existingSecret` mounts read-only at `/tls` and must supply `server.crt`,
`server.key`, `ca.crt`, `platform-ca.crt` and `ca-bundle.crt`. Web and initialization
set `SSL_CERT_FILE=/tls/ca-bundle.crt`. The trust bundle must retain necessary public
roots and the reviewed internal root. Native workbench receives neither runtime
configuration nor TLS secrets. Secret files are readable by nonroot1654 within the
web/init containers; the chart never mounts them into the native container.

`persistence.existingClaim` must already contain the explicitly selected distinct
web and workbench subdirectories, writable by UID/GID1654. They mount at
`/state/web` and `/state/workbench`; native `WORKBENCH_DATA_DIR` is fixed to the
latter. Existing `fabric/fabric.db`, `platform-registry.db`, `workbench.db`, their
necessary SQLite sidecars and retained data-protection `key*.xml` must be supplied
through a separately accepted, consistent snapshot/migration process. Do not attach
an empty claim. This chart creates or copies no state and changes no ownership:
there is no `fsGroup`, chmod/chown init or database migration command. Read-write
subPath mounts separate application storage, not an authorization boundary against
an administrator who can mount the full claim.

## Readiness and networking

Initialization runs the bundled config/state verifier before either application
starts. It reads retained state through read-only mounts and copies each bounded
SQLite database with its WAL/SHM sidecars to private scratch for validation,
sequentially, at most64MiB per set. Its own `/tmp` emptyDir is128Mi with a160Mi
ephemeral-storage limit and is not mounted into the application containers.
Kubernetes[v1.34 source](https://raw.githubusercontent.com/kubernetes/kubernetes/v1.34.0/pkg/volume/emptydir/empty_dir.go)
creates emptyDir directories with mode0777, permitting UID1654 to use scratch
without a PVC ownership change. Actual cluster storage behavior still needs
acceptance. Application startup/readiness runs its `--check-live` mode, which binds the
config bytes and validates private web HTTP plus actual HR TLS/server identity.
Static `/health/live` is used only for web liveness. The workbench exec probe calls
`/health` over loopback and requires storage integrity; it deliberately does not
require a policy Permit or `ready=true` from `/api/workbench/readiness`. A healthy
Pod is not acceptance of HR JWT/tenant authorization, Gateway forwarding, state
preservation, backup or rollback.

Ingress permits only TCP5193 from one peer containing both the explicit Gateway
namespace selector and nonempty Pod labels. Egress permits only DNS TCP/UDP53 to
the explicit namespace/Pod selector and HTTPS443 to a single IPv4 `/32` identity
endpoint. The CIDR is an IP constraint, not a hostname guarantee; TLS identity
validation remains mandatory. NodeLocal DNS, IPv6, HTTP proxies, private registry
delivery and alternative identity endpoints need their own reviewed adapters.
Registry image pulls are performed by the node and are not Pod egress. CNI
enforcement, Gateway selectors, actual NAT/source addresses and all other additive
NetworkPolicies require cluster acceptance; rendering alone proves none of them.

## Local acceptance and later activation

Tests render synthetic non-operational references using local Helm, verify the
bounded resource/port/mount/probe contract, and reject missing or injectable values.
They require Helm3 and PyYAML6.0.3, with no kubeconfig or cluster calls:

```sh
python3 -m pip install --requirement tests/requirements.txt
python3 tests/test_chart.py --helm /path/to/helm
```

For a real, reviewed candidate, local rendering is:

```sh
helm lint --strict . --namespace fanasa-organization-hr-development --values /restricted/candidate-values.json
helm template hr-candidate . --namespace fanasa-organization-hr-development --values /restricted/candidate-values.json
```

A future installation requires an approved dedicated Argo project and admission
boundary, valid OCI/receipt/signature policy, prepared existing state/secrets,
server-side strict dry-run, available capacity and enforced connectivity. The
shared Developer Portal chart/project allowlist does not admit this chart.
Successful local lint/render is an adapter result, not an installed Application,
Argo sync/health, migration or published Gateway route. The accepted systemd
deployment and its original receipt remain independently recorded.

## Host Gateway TCP bridge

`gatewayRelay.enabled=true` adds a restricted, single-replica TCP relay Deployment,
ClusterIP Service on5193, script ConfigMap and NetworkPolicy. The relay uses the
same verified web image and forwards opaque bytes to this release's HR Service.
It mounts no identity, key, certificate, runtime configuration or persistent state.
The external Nginx Gateway remains the TLS client; both peers validate the original
certificates end to end. An actual mutual-TLS transport test checks both peer
certificates and binary payload preservation through the relay.

Relay mode requires `network.gateway.namespace` to equal the HR namespace and
the exact Pod labels `app.kubernetes.io/name=organization-hr-gateway-relay` and
`app.kubernetes.io/instance=<Helm release name>`. `gatewayRelay.nodeSourceCidr`
must be the single reviewed host `/32`. Relay egress permits this release's HR
Pods on5193 and selected DNS Pods only. Both services remain ClusterIP. The
external Gateway changes only its HR include after live authorization acceptance.

Kubernetes allows traffic from a Pod's node independently of NetworkPolicy rules;
the host `/32` is not a claim that the node administrator is isolated. Application
mTLS and JWT/tenant/scope checks remain required. Relay probes establish only the
local TCP listener; end-to-end acceptance is a separate deployment gate. The relay
caps simultaneous connections at32, upstream connect at5 seconds, idle transport
at35 seconds and total connection life at120 seconds.

Run the isolated transport acceptance with Python3 and OpenSSL:
`python3 -B -m unittest discover -s tests -p test_gateway_relay.py`.
