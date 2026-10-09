# Read-only offline executor inspection

`neo-agent gateway-sandbox-health <private-manifest.json> --allow-docker-inspection`
is an explicit operator diagnostic, not a runner, collector, API/MCP tool or
authorization to start an agent. It calls only `docker info --format {{json .}}`
and `docker container inspect -- <exact 64-hex container ID>` on a local rootless
socket. No container creation/start/exec/stop/removal, migration, task ownership,
queue activation, model request or access grant occurs.

The gateway's `IGatewaySandbox` still defaults to `DisabledGatewaySandbox`.
This probe is deliberately **not** registered as that implementation. Passing a
diagnostic must never release a journal reservation or substitute for real diff,
build/test evidence and the trusted executor lifecycle.

## Operator manifest

Use an already installed, trusted Docker executable and a dedicated, existing
empty Docker configuration directory. Executable/config-directory paths must be
absolute with no symbolic-link/reparse ancestors. The directory must remain
operator-controlled and empty; no authentication caches are read or copied.
The child environment is cleared, with only process bootstrap variables retained,
and explicit `DOCKER_HOST`/`DOCKER_CONFIG`. Remote/TCP/SSH contexts are unsupported.

```json
{
  "executable": "/usr/bin/docker",
  "socket": "unix:///run/user/1001/docker.sock",
  "emptyDockerConfigDirectory": "/run/fanasa-probe-empty",
  "expected": {
    "scope": {
      "organizationId": "<authorized organization GUID>",
      "workspaceId": "<authorized workspace GUID>",
      "projectId": "<authorized project GUID>",
      "runId": "<exact reserved run GUID>"
    },
    "containerId": "<64 lowercase hex, not name/short ID>",
    "engineId": "<reviewed daemon ID>",
    "engineVersion": "<reviewed exact daemon version>",
    "bindingFingerprint": "<64 uppercase hex from the operator binding>",
    "sandboxId": "<operator sandbox identity>",
    "imageId": "sha256:<64 lowercase hex from container Image>",
    "revision": "<40-64 hex revision>",
    "repositoryUrl": "https://<approved repository>",
    "maxMemoryBytes": 536870912,
    "maxNanoCpus": 1000000000,
    "maxPids": 128
  }
}
```

Replace every placeholder; this illustrative document is not an installed or
accepted configuration. `imageId` is Docker's content image ID (`inspect.Image`),
**not** the registry manifest digest used in an image reference. Reconcile both
with the approved immutable image during installation; this probe does not pull
or resolve images. Repository/revision labels do not verify checkout contents.

Manifest is at most 64 KiB, strict JSON with unknown/duplicate fields rejected.
Expectation scope, image/container identities, repository URL and resource
budgets are validated before starting a process. Expected values must come from
the trusted operator's run/binding, never model prose or a task-supplied host path.
No secrets belong in this manifest, labels or executor environment.

## Inspected policy

- Exact daemon/version, Linux, rootless and builtin seccomp; cgroup v2/systemd and
  daemon-reported memory/swap/PID/CFS quota support. These reported features are
  not a stress test proving actual enforcement.
- Exact container/content-image identity and `fanasa.*` labels: `kind=executor-v1`,
  `organization-id`, `workspace-id`, `project-id`, `run-id`, `binding-fingerprint`,
  `sandbox-id`, `revision`, `repository`. GUID labels use lowercase dashed form.
- Numeric UID:GID, each 1000..999999999; non-privileged, read-only root, init,
  no autoremove, no restart, runc/private namespaces, ALL capabilities dropped,
  no added capabilities and only `no-new-privileges` security option.
- Positive bounded memory/CPU/PID limits; explicit no extra swap (`MemorySwap`
  equals `Memory`). Manifest maximums: memory 64 MiB..2 GiB, CPU 0.1..2 CPUs,
  PID 1..256. These ceilings are configuration, not measured resource suitability.
- Offline network `none`, network disabled, no published ports/links/extra hosts;
  no bind mounts, inherited volumes, devices, supplementary groups or sysctls.
- Exactly one writable named `/workspace` volume, derived as
  `fanasa-executor-<orgN>-<workspaceN>-<projectN>-<runN>`; no host/Docker socket,
  SQL/SSO/root credential or other-tenant mount. Working directory `/workspace`.
- Only HOME, PATH, LANG, LC_ALL, TZ, DOTNET_CLI_TELEMETRY_OPTOUT and DOTNET_NOLOGO
  env names accepted; unique names, HOME `/workspace/.home`, HOME/PATH required.
  This is a deliberately small offline-executor image policy, not a drop-in
  policy for a networked Hermes/OpenCode API container with model credentials.
- A single `/tmp` tmpfs string `rw,noexec,nosuid,nodev,size=64m`, bounded shm.

Unknown or missing required flags, malformed/duplicate JSON and ambiguous
multi-container inspect results fail closed. Optional absent/null empty mount or
host-access lists mean no corresponding access; explicit nonempty lists fail.
Both snapshots are bounded to 1 MiB and JSON depth 32. A single 15-second deadline
covers both Docker client processes and bounded stdout/stderr reads. Failures
discard raw client diagnostics. Timeout terminates only those client processes,
not an executor or the daemon. No shell or user-provided command arguments run.

## Results and limits

JSON contains only `policyCompliant`, `executorExited`, nullable `exitCode`,
`observedAtUtc` and bounded stable `reasons`, never raw inspect/env/paths/labels/credentials.
Exit 0 means the inspected configuration matches this narrow policy; 3 means a
policy/schema mismatch, 2 invalid invocation/configuration, 1 probe unavailable,
130 caller cancellation. Neither exit 0 nor executor exit means task success.

Running and exited are distinct. Exited requires non-running state, PID zero,
valid nonfuture RFC3339 start/finish timestamps (including Docker's nanoseconds,
parsed independently of host culture), exit code 0..255 and no paused/restarting/
dead/OOM/error flags. Unsettled or mismatched snapshots never report a trusted
exit. Exit code 0 is only a process code, not build/test acceptance. A snapshot is
point-in-time: it cannot fence a later manual restart or prove artifact provenance.

Remaining gates: provision the actual per-run sandbox; enforce and measure limits;
connect each engine's tools to that sandbox; trusted revision/diff/build/test and
executor-exit collection bound to the journal lease; permissions/reconciliation;
model credentials/egress broker; a real pilot through callback to Review. The
offline volume/home must not be reused across runs or tenants. No second queue,
task catalog or scheduler is introduced by this diagnostic.

## Verification and sources

`DockerSandboxPolicyTests` use reviewed-shape synthetic Docker JSON, not a live
daemon. CLI argument/manifest tests launch the real provisioning process, reject
unsafe input before Docker/SQL and verify sanitized output. A local Docker command
was not available during implementation; no live rootless/container verification,
VPS install, native execution or full sandbox acceptance is claimed.
Separate authorized read-only SSH observation of the VPS default daemon reported
AppArmor, builtin seccomp and cgroup namespaces, but no rootless feature. This
does not prove that another rootless daemon/socket is absent. No container or
daemon configuration changed, and no execution environment was provisioned.

Fields reviewed against [Docker Engine API 1.51 schema](https://docs.docker.com/reference/api/engine/version/v1.51.yaml).
Also see official [rootless mode](https://docs.docker.com/engine/security/rootless/)
and [seccomp](https://docs.docker.com/engine/security/seccomp/) documentation.
This API schema review does not certify an installed daemon/version or rule out
container/kernel escapes; stronger isolation remains an operator risk decision.
