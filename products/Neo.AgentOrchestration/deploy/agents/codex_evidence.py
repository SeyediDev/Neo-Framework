"""Trusted Codex JSONL/immutable-snapshot collection. Never import this into an agent.

The caller supplies Docker inspection from the trusted daemon and bytes collected
after exit, not model-authored JSON/files. This module has no task/SQL/callback
authority and never treats a model's final prose as evidence.
"""
import hashlib
import datetime as dt
import io
import json
import re
import tarfile
import uuid


class EvidenceError(ValueError):
    """Stable sanitized diagnostic; no upstream text or paths."""


def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise EvidenceError("duplicate-json-key")
        result[key] = value
    return result


def json_object(raw, limit=4 * 1024 * 1024):
    if not isinstance(raw, bytes) or len(raw) > limit:
        raise EvidenceError("json-limit")
    try:
        return json.loads(raw.decode("utf-8", errors="strict"), object_pairs_hook=unique)
    except (UnicodeError, json.JSONDecodeError, RecursionError):
        raise EvidenceError("json-invalid") from None


def count(value):
    if value is None:
        return None
    if type(value) is not int or value < 0 or value > 2**63 - 1:
        raise EvidenceError("usage-invalid")
    return value


def cli_observation(stdout, provider, model):
    """One ephemeral exec invocation. Do not parse tool stdout or final prose.

    Cached input and reasoning are included subsets. Missing remains None.
    Failed/incomplete/multiple turns are not reported as known-zero or success.
    """
    if len(stdout) > 4 * 1024 * 1024:
        raise EvidenceError("cli-output-limit")
    events = []
    for line in stdout.splitlines():
        if not line.strip():
            continue
        event = json_object(line, 1024 * 1024)
        if not isinstance(event, dict) or not isinstance(event.get("type"), str):
            raise EvidenceError("cli-event-invalid")
        events.append(event)
        if len(events) > 10000:
            raise EvidenceError("cli-event-limit")
    threads = [x for x in events if x["type"] == "thread.started"]
    turns = [x for x in events if x["type"] == "turn.completed"]
    if len(threads) != 1 or len(turns) > 1:
        raise EvidenceError("cli-correlation-invalid")
    try:
        thread = str(uuid.UUID(threads[0]["thread_id"]))
    except (KeyError, ValueError, AttributeError, TypeError):
        raise EvidenceError("cli-correlation-invalid") from None
    starts = [i for i, x in enumerate(events) if x["type"] == "turn.started"]
    if len(starts) != 1 or events[0]["type"] != "thread.started":
        raise EvidenceError("cli-order-invalid")
    completed = bool(turns) and events.index(turns[0]) > starts[0]
    failed = any(x["type"] in ("error", "turn.failed") for x in events)
    usage = None
    if turns:
        if not completed or events[-1] != turns[0]:
            raise EvidenceError("cli-order-invalid")
        raw = turns[0].get("usage")
        if not isinstance(raw, dict):
            raise EvidenceError("usage-invalid")
        input_tokens = count(raw.get("input_tokens"))
        output_tokens = count(raw.get("output_tokens"))
        cached = count(raw.get("cached_input_tokens"))
        reasoning = count(raw.get("reasoning_output_tokens"))
        if (input_tokens is not None and cached is not None and cached > input_tokens or
                output_tokens is not None and reasoning is not None and reasoning > output_tokens or
                input_tokens is not None and output_tokens is not None and input_tokens + output_tokens > 2**63 - 1):
            raise EvidenceError("usage-invalid")
        usage = dict(turnId=thread, provider=provider, model=model,
                     inputTokens=input_tokens, outputTokens=output_tokens,
                     cachedInputTokens=cached, reasoningTokens=reasoning)
    return dict(modelCompleted=completed and not failed, usage=usage)


def safe_snapshot(raw):
    """Bounded plain-files-only archive. Reject links, traversal and secret stores.

    Used for BOTH baseline and stopped-executor snapshots before any extraction.
    The original private artifact remains outside the executor's mount namespace.
    """
    if not isinstance(raw, bytes) or len(raw) > 16 * 1024 * 1024:
        raise EvidenceError("snapshot-limit")
    files = {}
    try:
        with tarfile.open(fileobj=io.BytesIO(raw), mode="r:") as archive:
            for entry in archive:
                name = entry.name.removeprefix("./")
                parts = name.split("/")
                if (not name or name.startswith("/") or "\\" in name or any(x in ("", ".", "..") for x in parts) or
                        any(ord(x) < 32 for x in name) or len(name) > 500 or ":" in name or name in files):
                    raise EvidenceError("snapshot-path-invalid")
                if any(x.lower() in (".git", ".ssh", ".aws", ".users", ".vps") or
                       x.lower() == ".env" or x.lower().startswith(".env.") or
                       x.lower() in ("auth.json", "credentials.json") for x in parts):
                    raise EvidenceError("snapshot-private-path")
                if entry.isdir():
                    continue
                if not entry.isfile() or entry.size > 4 * 1024 * 1024 or entry.mode & 0o7000:
                    raise EvidenceError("snapshot-entry-invalid")
                source = archive.extractfile(entry)
                if source is None:
                    raise EvidenceError("snapshot-entry-invalid")
                data = source.read(entry.size + 1)
                if len(data) != entry.size:
                    raise EvidenceError("snapshot-entry-invalid")
                files[name] = data
                if len(files) > 10000 or sum(map(len, files.values())) > 12 * 1024 * 1024:
                    raise EvidenceError("snapshot-limit")
    except (tarfile.TarError, EOFError, OSError):
        raise EvidenceError("snapshot-invalid") from None
    if not files:
        raise EvidenceError("snapshot-empty")
    return files


def changes(baseline, current):
    before, after = safe_snapshot(baseline), safe_snapshot(current)
    def modes(raw):
        with tarfile.open(fileobj=io.BytesIO(raw), mode="r:") as archive:
            return {entry.name.removeprefix("./"): bool(entry.mode & 0o111) for entry in archive if entry.isfile()}
    before_modes, after_modes = modes(baseline), modes(current)
    entries = []
    for name in sorted(before.keys() | after.keys()):
        old, new = before.get(name), after.get(name)
        if old != new or before_modes.get(name) != after_modes.get(name):
            entries.append(dict(path=name, before=None if old is None else hashlib.sha256(old).hexdigest(),
                                after=None if new is None else hashlib.sha256(new).hexdigest(),
                                executableBefore=before_modes.get(name), executableAfter=after_modes.get(name)))
    # Private manifest, not code or paths for panel display. Canonical bytes make
    # retries stable. A full downloadable patch needs separate access control.
    manifest = json.dumps(entries, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode()
    return len(entries), manifest


def verify_executor(info, inspected, expected, require_exit=True):
    """Fresh daemon metadata, not a receipt supplied by the model/workspace."""
    try:
        c = inspected[0]
        h, cfg, state = c["HostConfig"], c["Config"], c["State"]
        scope, policy = expected["scope"], expected["policy"]
        labels = cfg["Labels"]
        if (len(inspected) != 1 or info["ID"] != expected["engineId"] or
                info["ServerVersion"] != expected["engineVersion"] or info["OSType"] != "linux" or
                "name=rootless" not in info["SecurityOptions"] or info["CgroupVersion"] != "2" or
                info["CgroupDriver"] != "systemd" or
                not all(info.get(k) is True for k in ("MemoryLimit", "SwapLimit", "PidsLimit", "CpuCfsQuota"))):
            raise EvidenceError("executor-daemon-invalid")
        if (c["Id"] != expected["containerId"] or c["Image"] != policy["imageDigest"] or
                not re.fullmatch("[0-9a-f]{64}", c["Id"]) or
                not re.fullmatch("[1-9][0-9]{3,8}:[1-9][0-9]{3,8}", cfg["User"]) or
                cfg["WorkingDir"] != "/workspace"):
            raise EvidenceError("executor-identity-invalid")
        required = {"kind": "codex-executor-v1", "binding-fingerprint": expected["bindingFingerprint"],
                    "sandbox-id": policy["sandboxId"], "revision": policy["revision"], "repository": policy["repositoryUrl"]}
        required.update({key: scope[prop] for key, prop in
                         (("organization-id", "organizationId"), ("workspace-id", "workspaceId"),
                          ("project-id", "projectId"), ("run-id", "runId"))})
        if any(labels.get("fanasa." + key) != value for key, value in required.items()):
            raise EvidenceError("executor-scope-invalid")
        if (h["Privileged"] is not False or h["ReadonlyRootfs"] is not True or h["AutoRemove"] is not False or
                h["Init"] is not True or h["CapDrop"] != ["ALL"] or h.get("CapAdd") or
                h.get("Binds") or h.get("Devices") or h.get("VolumesFrom") or h.get("GroupAdd") or
                h.get("DeviceRequests") or h.get("DeviceCgroupRules") or h.get("Sysctls") or
                h.get("ExtraHosts") or h.get("PortBindings") or h.get("Links") or
                h["NetworkMode"] != expected["network"] or
                h["RestartPolicy"]["Name"] != "no" or h.get("PidMode") or h.get("UsernsMode")):
            raise EvidenceError("executor-isolation-invalid")
        if (h["Memory"] != 768 * 1024 * 1024 or h["MemorySwap"] != h["Memory"] or
                h["NanoCpus"] != 1000000000 or h["PidsLimit"] != 128):
            raise EvidenceError("executor-resource-invalid")
        # Exact reviewed seccomp text hash, not seccomp=unconfined or a guessed
        # daemon label. Caller pins the profile and its inspect representation.
        security = h["SecurityOpt"]
        profiles = [x[len("seccomp="):] for x in security if x.startswith("seccomp=")]
        if (len(security) != 2 or len(profiles) != 1 or not any(
                x in security for x in ("no-new-privileges", "no-new-privileges:true"))):
            raise EvidenceError("executor-security-invalid")
        profile = json_object(profiles[0].encode(), 256 * 1024)
        if (not isinstance(profile, dict) or profile.get("defaultAction") != "SCMP_ACT_ERRNO" or
                hashlib.sha256(json.dumps(profile, sort_keys=True, separators=(",", ":")).encode()).hexdigest() != expected["seccompSha256"]):
            raise EvidenceError("executor-security-invalid")
        mounts = c["Mounts"]
        if len(mounts) != 1 or mounts[0].get("Type") != "volume" or mounts[0].get("Name") != expected["volume"] or \
                mounts[0].get("Destination") != "/workspace" or mounts[0].get("RW") is not True:
            raise EvidenceError("executor-mount-invalid")
        if h.get("Tmpfs") != {"/tmp": "rw,nosuid,nodev,size=128m", "/state": "rw,nosuid,nodev,size=128m,uid=10000,gid=10000"}:
            raise EvidenceError("executor-temp-invalid")
        env = dict(x.split("=", 1) for x in cfg["Env"])
        # Never expose a provider key to repository-controlled commands. The
        # operator must provision a separate secret-injecting model proxy first.
        if len(env) != len(cfg["Env"]) or set(env) - {"PATH", "HOME", "LANG", "LC_ALL", "CODEX_HOME", "NO_COLOR", "PYTHON_VERSION", "PYTHON_SHA256"}:
            raise EvidenceError("executor-environment-invalid")
        if env.get("CODEX_HOME") != "/state/codex" or env.get("HOME") != "/state":
            raise EvidenceError("executor-environment-invalid")
        if env.get("PYTHON_VERSION") is not None and not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", env["PYTHON_VERSION"]):
            raise EvidenceError("executor-environment-invalid")
        if env.get("PYTHON_SHA256") is not None and not re.fullmatch(r"[0-9a-f]{64}", env["PYTHON_SHA256"]):
            raise EvidenceError("executor-environment-invalid")
        if not require_exit:
            if state["Status"] != "created" or state["Running"] is not False or state["Pid"] != 0:
                raise EvidenceError("executor-prestart-unverified")
            return None
        if (state["Status"] != "exited" or state["Running"] is not False or state["Pid"] != 0 or
                state["Paused"] or state["Restarting"] or state["Dead"] or state["OOMKilled"] or state["Error"] or
                type(state["ExitCode"]) is not int or not 0 <= state["ExitCode"] <= 255):
            raise EvidenceError("executor-exit-unverified")
        started = dt.datetime.fromisoformat(state["StartedAt"].replace("Z", "+00:00"))
        finished = dt.datetime.fromisoformat(state["FinishedAt"].replace("Z", "+00:00"))
        if (started.tzinfo is None or finished.tzinfo is None or started.year < 2020 or finished < started or
                finished > dt.datetime.now(dt.timezone.utc)):
            raise EvidenceError("executor-exit-unverified")
        return state["ExitCode"]
    except (KeyError, IndexError, TypeError, AttributeError, ValueError) as error:
        if isinstance(error, EvidenceError):
            raise
        raise EvidenceError("executor-metadata-invalid") from None


def collect(expected, info_bytes, inspect_bytes, stdout, baseline, snapshot, validation):
    """All inputs from trusted storage/daemon/offline verifier, never agent API.

    Caller must hold its runtime lifecycle lock across snapshot/exit recheck and
    freeze this exact receipt before returning it. This function does not invent
    durable lifecycle, authenticated ingress or callback delivery authority.
    """
    info, inspected = json_object(info_bytes), json_object(inspect_bytes)
    exit_code = verify_executor(info, inspected, expected)
    cli = cli_observation(stdout, expected["provider"], expected["model"])
    receipt = dict(protocol="fanasa-codex/v1", scope=expected["scope"], nativeId=expected["nativeId"],
                   bindingFingerprint=expected["bindingFingerprint"], policy=expected["policy"],
                   state="Completed" if exit_code == 0 and cli["modelCompleted"] else "Failed",
                   executorExited=True, exitCode=exit_code, modelCompleted=cli["modelCompleted"],
                   containerId=expected["containerId"], usage=cli["usage"], validation=None)
    if receipt["state"] == "Completed":
        changed, manifest = changes(baseline, snapshot)
        if (not isinstance(validation, dict) or type(validation.get("passed")) is not bool or
                type(validation.get("testCount")) is not int or not 1 <= validation["testCount"] <= 100000 or
                type(validation.get("exitCode")) is not int or not 0 <= validation["exitCode"] <= 255 or
                not isinstance(validation.get("output"), bytes) or len(validation["output"]) > 1024 * 1024 or
                validation["passed"] != (validation["exitCode"] == 0) or changed == 0):
            raise EvidenceError("validation-unverified")
        receipt["validation"] = dict(passed=validation["passed"], testCount=validation["testCount"],
                                     changedFileCount=changed, changeManifestSha256=hashlib.sha256(manifest).hexdigest(),
                                     testOutputSha256=hashlib.sha256(validation["output"]).hexdigest(),
                                     snapshotSha256=hashlib.sha256(snapshot).hexdigest())
    return receipt
