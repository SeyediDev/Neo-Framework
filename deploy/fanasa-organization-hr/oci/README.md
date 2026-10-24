# Development OCI adapter

This operator-controlled adapter repackages the binaries of an already verified HR CI archive. It does not rebuild source, alter the original systemd release/manifest/receipt, push a registry image, install Argo, or promote a deployment. An authoritative receipt obtained by the operator binds the archive; this is **not a signature** or an independent authentication of CI.

The accepted original CI origin is the clean protected `develop` commit of internal GitLab project 2. `verify_bundle.py` performs bounded archive verification and extraction before the adapter checks that origin. Runtime files containing state, credentials, arbitrary environment-specific appsettings, or nonempty credential fields in generic appsettings are refused. Fresh output paths, original input hashes and a deterministic context inventory prevent reuse of changed contexts. Image recipes copy verified component binaries and adapter probes; original provenance evidence remains outside the images.

Base `appsettings.json` is restricted to inert defaults: empty Authentication authority/client ID/secret, loopback AllowedHosts and optional standard Logging.LogLevel entries. An optional `Pip` section is accepted only as `{"Secrets":{},"Transport":{"Bindings":[]}}`; configuration field casing follows .NET's case-insensitive names, while duplicate/unknown fields, missing fields, nonempty secrets and any binding are refused. Kestrel endpoints, enabled integrations, payment/outbox settings and all other runtime sections are refused, since mounted Development settings merge with that base file. Actual candidate settings are mounted and validated externally. Synthetic smoke directs the platform registry and application stores to its isolated `/state` volume.

New CI bundles reject every environment-specific appsettings file, including the historical public `web/appsettings.Development.json` template with an empty client secret. Archive verification rejects such files before an OCI context is created. Actual candidate Development settings must be mounted and validated externally. The original archive/systemd manifest/receipt is unchanged.

The default base is the official ASP.NET 10 Linux amd64 manifest pinned as:

`mcr.microsoft.com/dotnet/aspnet@sha256:0fa044f682cb7d93a5a90401a00c626c66f7b00b86922be9441f869eae039f80`

The operator resolved it from Microsoft's HTTPS registry, `10.0` index digest `sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4`. Python is installed from the base OS package repositories at image build time; the Python package version is not locked. Consequently a deterministic context does not promise identical OCI image IDs across future builds. The receipt records the actual local image IDs and exact recipes/probes/base digest. Docker image IDs are explicitly **not registry manifest digests**.

```sh
python3 deploy/fanasa-organization-hr/oci/adapter.py prepare \
  --archive /var/lib/fanasa-organization-hr/artifacts/6/hr-development-8f12512ead33-7cd37c93a9b4.tar.gz \
  --receipt /var/lib/fanasa-organization-hr/artifacts/6/release.json \
  --context /var/tmp/hr-oci-context

python3 deploy/fanasa-organization-hr/oci/adapter.py build-smoke \
  --context /var/tmp/hr-oci-context \
  --receipt-output /var/tmp/hr-oci-verified.json
```

The first command also copies the sibling trusted `runtime_config.py` validator to `/adapter/runtime_config.py` and binds its hash. Image working directories are `/app/web` and `/app/workbench`; both run their verified DLL with UID/GID 1654. `/adapter/health.py web` checks loopback port 5191 JSON liveness; `workbench` checks loopback port 4187 JSON and healthy persisted storage. Probes disable environment proxies, use a 3-second timeout, limit response size and fail closed. Web liveness alone does not prove HR listener, JWT, mTLS or SSO readiness; the separate runtime configuration validator supplies the candidate readiness preflight.

`build-smoke` runs each image with a read-only root filesystem, dropped capabilities, no network or published ports, bounded resources and a fresh named synthetic state volume. A short root container can only initialize that volume's ownership; both application runs are nonroot. Synthetic OIDC settings and disabled HR keep operational credentials out of the smoke. Each application database must pass SQLite integrity and foreign-key checks and retain an identical logical digest after container recreation. Containers/volumes are removed afterward. No operational data or secrets are mounted. A successful separate receipt states `local-image-verified`, `registry-not-pushed`, `Argo-not-installed`, and promotion `pending`.

```sh
python3 -m unittest discover -s deploy/fanasa-organization-hr/oci/tests -p 'test_*.py'
```

Boundary tests use synthetic archives and do not invoke Docker. Actual image build/smoke is a separate operator action and must complete before reporting verified local images.
