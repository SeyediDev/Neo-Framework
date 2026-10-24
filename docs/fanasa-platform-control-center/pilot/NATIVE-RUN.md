# Bounded native fixture run

This procedure operates only on a fresh root-owned extraction of the verified API Gateway handoff. It does not edit the owner package or shared wiring. Full three-replica acceptance remains unrun until its separate evidence is produced. The single-gateway command produces smoke evidence, with fullAcceptanceExecuted=false.

The reviewed ZIP SHA256 is a8ad2b872200ff1eacd3631b2ec37a5653e9148c4e07b30ae804288021671f46. Its 24 forward-slash entries are under api-gateway/, expand to 145,747 bytes, and match the immutable handoff manifest. Current executable sources still match; two README files have newer documentation, recorded in [package currentness](package-currentness.json). Extract only after rejecting absolute paths, drive prefixes, empty/traversal components and symlinks. Never extract over an existing application.

## Verified execution on 10 October 2026

[The single-gateway run](native-smoke-orchestration-3.json) passed all 12 checks, exited zero, preserved source, had no OOM, and completed exact-project cleanup. It verified positive OIDC/PDP transport, Redis TLS, one admitted backend mTLS forwarding, selected identity/PDP denials without forwarding, and direct-backend denial without a client certificate. [Postflight](vps-postflight-3.json) confirmed the existing K3s node and selected Portal deployment healthy. Available memory remained below the separate 2 GiB full-acceptance gate.

VPS Docker Hub/auth access timed out. Root instead fetched exactly the official Linux/amd64 Python 3.12.10, Redis 7.4.6 and APISIX 3.14.1 images on Windows, preserving original manifests/configs/layer hashes and verifying uncompressed DiffIDs. The combined archive has 209,377,280 bytes and SHA256 ca1bc7fc668c5a9157bc991801e5bc30b4c7908df2d861a43a4bb9e3ecdbecc0. [Lock](official-images-linux-amd64.lock.json), [export](official-images-export.json), [source audit](official-images-source-audit.json) and [private upload/import evidence](image-delivery.json) are retained. Only these three tags were permitted in both archive formats; existing conflicting tags would abort import. No daemon, registry, trust or firewall configuration changed.

The root-owned orchestrator now requires an `imageLock` for all three tags, inspects cached image identity/platform/ordered RootFS DiffIDs, and pins every service to the actual verified image ID through a generated scratch overlay. It never pulls or builds. Docker 29 on this host reports selected manifest IDs; both verified manifest/config IDs are recognized, with exact platform/layers still required. The older serial-pull procedure below is historical/manual guidance and was not used for this successful run. A new run must revalidate all prerequisites and use a new project and fresh fixture state.

Failed runs preserve bounded service logs privately outside the runner bind before cleanup; evidence exports only safe filenames, hashes and byte counts. Successful runs do not export raw service logs or private runtime overlay/key volumes.

## Capacity and existing-runtime prerequisites

Use the existing healthy K3s node and Docker engine. Do not install or start a daemon, stop existing workloads, change quotas, disable swap, change host trust, or run an unbounded image build.

| Service | Memory and memory+swap limit | CPU limit |
|---|---:|---:|
| Each gateway | 256 MiB | 0.15 |
| Each of three fixtures | 64 MiB | 0.05 |
| Redis | 96 MiB | 0.10 |
| Runner | 96 MiB | 0.15 |
| Certs, separate initialization phase | 64 MiB | 0.20 |

Every container also has cpu_shares=128, pids_limit=64 and Docker logs limited to 5 MiB × 2. Matching memswap_limit prevents these containers from using additional swap. This does not change host swap settings.

The full test's concurrent cap is **1,408 MiB / 1 CPU**. The single-gateway smoke cap is **640 MiB / 0.55 CPU**. Certs runs and exits before either phase starts, so its temporary 64 MiB is excluded from the concurrent test totals. These limits are ceilings, not measured minimum requirements; OOM or startup timeout is an operational failure to record.

Require a fresh MemAvailable of at least **2 GiB for full acceptance**, or **1 GiB for smoke**, and at least **4 GiB free on the extraction and Docker storage filesystems**. Disk allowance is operational headroom, not a measured image minimum. Image pull, extraction, Docker daemon overhead and builds are outside Compose container caps. Recheck headroom during serial pulls and before initialization/start; stop only this fixture if the applicable headroom or existing runtime health is lost.

readonly-native-inventory.py reads selected fields through existing local CLIs, with a 90-second command budget, 12-second reads, 4 MiB output limit and discarded stderr. It reports numeric resources/listener ports, K3s readiness, selected deployment images/readiness and tenant quotas. It emits no environments, keys, raw configs, addresses or listener owners. Its Compose gate is an actual config-only stdin feature probe, not a major-version assumption. Kubernetes quotas do not cap these Docker fixtures. A healthy snapshot is not complete application-health proof.

## Root-owned paths and initial read-only checks

Copy the controls in this directory beside a fresh handoff extraction. Use a unique project name and absolute paths; substitute the selected root-owned run directory:

~~~bash
set -euo pipefail
run_dir=/var/tmp/fanasa-native-REPLACE_WITH_RUN_ID
package_dir="$run_dir/api-gateway"
controls_dir="$run_dir/controls"
evidence_dir="$run_dir/evidence"
pilot_project=fanasa-gateway-native-REPLACE_WITH_LOWERCASE_RUN_ID
export FANASA_NATIVE_CONTROLS_DIR="$controls_dir"
export FANASA_NATIVE_EVIDENCE_DIR="$evidence_dir"
mkdir -p "$evidence_dir"
chmod 700 "$evidence_dir"
compose=(docker compose --project-name "$pilot_project" --project-directory "$package_dir/native"
  -f "$package_dir/native/compose.yaml"
  -f "$controls_dir/compose.resources.yaml"
  -f "$controls_dir/compose.smoke.yaml")
python3 "$controls_dir/readonly-native-inventory.py" > "$evidence_dir/preflight.json"
~~~

The full-run --require-ready option requires every full prerequisite, including 2 GiB available. For smoke inspect preflightGates and require every gate except memoryAtLeast2GiBAvailable, replacing that one with a fresh MemAvailableBytes >= 1073741824. Also inspect extraction filesystem free space directly if it differs from root or Docker storage. Stop when a required read is unavailable or a selected existing deployment/node is unhealthy.

The first project Compose operation is config. Review only selected fields; do not archive a complete resolved config or environment dump:

~~~bash
config_json="$("${compose[@]}" --profile "*" config --format json)"
printf "%s" "$config_json" | python3 -c '
import json, sys
from pathlib import Path
cfg=json.load(sys.stdin)
package=Path(sys.argv[1])
services=cfg["services"]
caps={"certs":(64,.20),"oidc":(64,.05),"pdp":(64,.05),"backend":(64,.05),
      "redis":(96,.10),"runner":(96,.15),"gateway1":(256,.15),"gateway2":(256,.15),
      "gateway3":(256,.15),"gateway-no-pep":(256,.15)}
assert set(services) == set(caps)
assert all(not item.get("ports") and item.get("network_mode") != "host" for item in services.values())
assert set(cfg["networks"]) == {"edge", "dependencies"}
assert all(item["internal"] is True for item in cfg["networks"].values())
for name,item in services.items():
    memory,cpu=caps[name]
    assert int(item["mem_limit"]) == int(item["memswap_limit"]) == memory*1024**2
    assert float(item["cpus"]) == cpu
    assert int(item["pids_limit"]) == 64 and int(item["cpu_shares"]) == 128
    assert item["logging"] == {"driver":"json-file","options":{"max-size":"5m","max-file":"2"}}
    assert all("docker.sock" not in str(mount.get("source","")) for mount in item.get("volumes",[]))
for name in ("certs","oidc","pdp","backend","runner"):
    assert services[name]["image"] == "python:3.12.10-slim-bookworm"
    assert not services[name].get("build")
public=json.loads((package/"native/generated/config.yaml").read_text())
routes=json.loads((package/"native/generated/apisix.yaml").read_text().split("\n#END\n")[0])
settings=json.loads((package/"native/generated/settings.json").read_text())
plugins=routes["services"][0]["plugins"]
upstream=routes["services"][0]["upstream"]
assert public["apisix"]["enable_admin"] is False and public["apisix"]["enable_control"] is False
assert public["apisix"]["node_listen"] == [{"ip":"0.0.0.0","port":9086}]
assert plugins["openid-connect"]["discovery"] == "https://oidc.fixture.invalid/realms/fanasa/.well-known/openid-configuration"
assert plugins["openid-connect"]["introspection_endpoint"] == "https://oidc.fixture.invalid/realms/fanasa/protocol/openid-connect/token/introspect"
assert plugins["fanasa-pep"]["pdpEndpoint"] == "https://pcc-pdp.fixture.invalid:8443/api/gateway/decision"
assert plugins["fanasa-pep"]["redisHost"] == "redis.fixture.invalid"
assert upstream["nodes"] == {"managed-backend.fixture.invalid:8443":1}
assert settings["fixtureOnly"] is True
print(json.dumps({"noHostPorts":True,"internalNetworks":True,"fixtureEndpointsOnly":True,"buildRemoved":True}))
' "$package_dir" > "$evidence_dir/config-review.json"
unset config_json
~~~

The inherited route Host api.fanasa.net.local is an HTTP header inside the fixture network. Candidate source files still contain canonical internal SSO references, but the active generated/runtime overlay must connect only to fixture.invalid services. The root-owned overlay changes tools-image selection and command/resources; owner Lua and acceptance code remain unchanged.

## Images and bounded initialization

The owner's Dockerfile performs apt-get during image build. Compose service caps do not constrain that build. This procedure skips that build, clears build declarations with Compose !reset null, and uses the owner's exact Python base tag directly for tools. Record this image choice; it differs from the custom Dockerfile image.

Pull serially using the existing daemon, with fresh health/headroom checks between pulls. If a pull threatens the applicable headroom, cancel this operation and defer; do not change daemon settings:

~~~bash
docker image pull python:3.12.10-slim-bookworm
docker image pull redis:7.4.6-bookworm
docker image pull apache/apisix:3.14.1-debian
~~~

A bounded, network-none capability probe must prove the base has both Python and OpenSSL. If it fails, stop without apt installation or an unbounded build:

~~~bash
docker run --rm --pull never --network none --init --memory 64m --memory-swap 64m \
  --cpus 0.20 --cpu-shares 128 --pids-limit 64 \
  --log-driver json-file --log-opt max-size=5m --log-opt max-file=2 \
  python:3.12.10-slim-bookworm sh -c "python --version && openssl version" \
  > "$evidence_dir/tools-capability.txt"
~~~

Only then run the unchanged certificate service, by itself. It creates seven RSA-2048 keys sequentially, two-day synthetic certificates and a private runtime overlay in project volumes. The CA key stays in its disposable container. Export no key or private runtime-config volumes:

~~~bash
timeout --signal=TERM --kill-after=15s 180s "${compose[@]}" up --no-build --pull never --exit-code-from certs certs
~~~

Require certs exit code 0 and no OOM before starting anything else. Use --no-deps below to prevent rerunning certificate setup while services are active.

## Single-gateway smoke

Recheck fresh >=1 GiB MemAvailable, disk allowance and existing health. Only these five long-running fixture services may start:

~~~bash
"${compose[@]}" up -d --no-build --pull never --no-deps redis oidc pdp backend gateway1
set +e
timeout --signal=TERM --kill-after=15s 180s "${compose[@]}" run --no-deps --pull never \
  --name "${pilot_project}-runner" runner
runner_exit=$?
set -e
printf "%s\n" "$runner_exit" > "$evidence_dir/runner-exit-code.txt"
~~~

The runner is retained so its OOM/exit evidence can be inspected. gateway2, gateway3 and gateway-no-pep have an inactive full-acceptance-only profile; the smoke runner has no inherited dependency graph. Do not enable the other gateways in the smoke phase.

native-single-gateway-smoke.py verifies Redis TLS PING, OIDC discovery/issuer, PDP TLS readiness, inactive identity denial and no forwarding, positive OIDC/PDP denial, then provisions only absent fresh fixture state. Its one allowed request must consume one admission and reach the backend through mTLS. Direct backend access without a client certificate must subsequently fail. It never resets an existing allocation and never calls the owner's full Acceptance.ready/run/main.

Results are written to native-single-gateway-smoke.json in the root-owned evidence directory. A pass establishes only these single-gateway cases; it leaves three-replica concurrency, the missing-PEP guard, durability/recovery, negative Redis TLS, OIDC cache-expiry outage and production integration pending.

## Separate full acceptance

Defer until a fresh >=2 GiB MemAvailable and all full inventory gates pass. Use a fresh project, extraction/evidence directory and fixture volumes; do not reuse smoke consumption. Repeat config/image/capability/cert initialization review first. Enable the full profile and explicitly start all gateways, then override the runner command to the unchanged owner suite:

~~~bash
"${compose[@]}" --profile full-acceptance-only up -d --no-build --pull never --no-deps \
  redis oidc pdp backend gateway1 gateway2 gateway3 gateway-no-pep
set +e
timeout --signal=TERM --kill-after=15s 600s "${compose[@]}" run --no-deps --pull never \
  --name "${pilot_project}-runner" runner python native/run.py \
  --settings /opt/fanasa-api-gateway/native/generated/settings.json \
  --state /state/native.sqlite --ca /run/native-trust/ca-bundle.pem \
  --output /results/native-verification.json --observations /observations
runner_exit=$?
set -e
printf "%s\n" "$runner_exit" > "$evidence_dir/runner-exit-code.txt"
~~~

The matching limits still cap the full phase at 1,408 MiB / 1 CPU. The runner override selects the owner acceptance suite, not smoke. No full-pass claim is valid from smoke output.

## Evidence and project-scoped stop

During the run save Docker stats snapshots for only this project, plus fresh sanitized host inventories. Before stopping, retain actual image IDs/repository digests, effective container caps and OOM/exit status. These templates intentionally exclude environments, keys and raw configuration:

~~~bash
mapfile -t ids < <("${compose[@]}" --profile full-acceptance-only --profile manual-smoke ps -a -q)
docker inspect --format '{"name":{{json .Name}},"imageId":{{json .Image}},"status":{{json .State.Status}},"oomKilled":{{.State.OOMKilled}},"exitCode":{{.State.ExitCode}},"memoryLimitBytes":{{.HostConfig.Memory}},"memorySwapLimitBytes":{{.HostConfig.MemorySwap}},"cpuNano":{{.HostConfig.NanoCpus}},"cpuShares":{{.HostConfig.CpuShares}},"pidsLimit":{{json .HostConfig.PidsLimit}},"publishedPorts":{{json .HostConfig.PortBindings}}}' \
  "${ids[@]}" > "$evidence_dir/container-evidence.jsonl"
docker stats --no-stream --format '{"name":{{json .Name}},"memory":{{json .MemUsage}},"memoryPercent":{{json .MemPerc}},"cpuPercent":{{json .CPUPerc}},"pids":{{json .PIDs}}}' \
  "${ids[@]}" > "$evidence_dir/memory-snapshot.jsonl"
mapfile -t image_ids < <(docker inspect --format '{{.Image}}' "${ids[@]}" | sort -u)
docker image inspect --format '{"id":{{json .Id}},"repositoryDigests":{{json .RepoDigests}}}' \
  "${image_ids[@]}" > "$evidence_dir/image-evidence.jsonl"
python3 "$controls_dir/readonly-native-inventory.py" > "$evidence_dir/postflight.json"
~~~

Also retain the runner JSON and gateway runtime observations it includes. Record startup/probe/pull failures and timeouts; never reinterpret them as a passing native test. A project-filtered inspection must confirm no published ports and only the intended fixture containers before cleanup. If a timeout leaves the retained runner active, stop that project-owned runner first.

~~~bash
"${compose[@]}" --profile full-acceptance-only --profile manual-smoke down
~~~

This stops only the dedicated Compose project and retains its volumes. Do not add --volumes, prune commands or unrelated container/image removals. Retained volumes contain synthetic state/keys and must stay private. Any later fresh run uses a new project. This is fixture cleanup, not a production rollback.
