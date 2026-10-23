"""Private trusted CLI lifecycle, not a scheduler/board or callback impersonator.

Run as the dedicated rootless Docker owner. Only operator configuration selects
repo/revision/image/verification. Never mount this service, its state, credentials
or Docker socket into executors. Intent is durable BEFORE a native write; an
uncertain/crashed write is held, never blindly repeated.
"""
import argparse
from contextlib import contextmanager
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import sqlite3
import ssl
import subprocess
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from codex_evidence import EvidenceError, changes, cli_observation, collect, json_object, safe_snapshot, verify_executor


class RuntimeErrorSafe(ValueError):
    pass


def bounded_command(args, *, env, data=None, timeout=10, limit=20 * 1024 * 1024, include_stderr=False):
    """Bound memory while pipes are read, not only after capture_output allocates."""
    process = subprocess.Popen(args, stdin=subprocess.PIPE if data is not None else subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
    out, err, failures = bytearray(), bytearray(), []
    def read(stream, target):
        try:
            while chunk := stream.read(16384):
                if len(target) + len(chunk) > limit:
                    failures.append(True); process.kill(); break
                target.extend(chunk)
        finally:
            stream.close()
    def write():
        try:
            process.stdin.write(data); process.stdin.close()
        except (OSError, ValueError):
            failures.append(True)
    threads = [threading.Thread(target=read, args=(stream, target), daemon=True)
               for stream, target in ((process.stdout, out), (process.stderr, err))]
    if data is not None: threads.append(threading.Thread(target=write, daemon=True))
    for thread in threads: thread.start()
    try:
        process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        failures.append(True); process.kill(); process.wait(timeout=2)
    for thread in threads: thread.join(timeout=2)
    if process.returncode or failures or any(thread.is_alive() for thread in threads) or (include_stderr and len(out) + len(err) > limit):
        raise RuntimeErrorSafe("runtime-command-unconfirmed")
    return bytes(out + err if include_stderr else out)


def acceptance_result(output, exit_code, policy, expected_count):
    """Operator suite must emit one strict report, isolating untrusted child stdout."""
    report = json_object(output, 1024 * 1024)
    if (not isinstance(report, dict) or set(report) != {"protocol", "acceptanceSha256", "testCount", "passed"} or
            report["protocol"] != "fanasa-acceptance/v1" or report["acceptanceSha256"] != policy["acceptanceSha256"] or
            type(report["testCount"]) is not int or report["testCount"] != expected_count or
            type(report["passed"]) is not bool or report["passed"] != (exit_code == 0)):
        raise RuntimeErrorSafe("runtime-validation-report-unverified")
    return dict(passed=report["passed"], testCount=report["testCount"], exitCode=exit_code, output=output)


class Docker:
    def __init__(self):
        if os.getuid() == 0:
            raise RuntimeErrorSafe("rootless-owner-required")
        self.env = {"PATH": "/usr/local/bin:/usr/bin:/bin", "HOME": str(Path.home()),
                    "XDG_RUNTIME_DIR": "/run/user/" + str(os.getuid()),
                    "DOCKER_HOST": "unix:///run/user/" + str(os.getuid()) + "/docker.sock"}

    def call(self, *args, data=None, timeout=10, limit=20 * 1024 * 1024):
        return bounded_command(["docker", *args], env=self.env, data=data, timeout=timeout, limit=limit)

    def start(self, name, data):
        # Private data travels through stdin, never argv/env/logs. The relay's
        # startup line contains the real provider key, but the executor's does not.
        process = subprocess.Popen(["docker", "start", "-ai", name], stdin=subprocess.PIPE,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=self.env)
        written, errors = threading.Event(), []
        def feed():
            try:
                process.stdin.write(data); process.stdin.close()
            except (OSError, ValueError):
                errors.append(True)
            finally:
                written.set()
        threading.Thread(target=feed, daemon=True).start()
        # Reap the attached client without blocking the HTTP lifecycle until exit.
        threading.Thread(target=process.wait, daemon=True).start()
        if not written.wait(5) or errors:
            process.kill()
            raise RuntimeErrorSafe("runtime-start-unconfirmed")
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            observed = json_object(self.call("inspect", name, timeout=2))[0]["State"]
            if observed["Status"] in ("running", "exited"):
                return
            if process.poll() is not None:
                break
            time.sleep(0.05)
        raise RuntimeErrorSafe("runtime-start-unconfirmed")

    def validation_logs(self, name):
        return bounded_command(["docker", "logs", name], env=self.env, timeout=5,
                               limit=1024 * 1024, include_stderr=True)


class Store:
    def __init__(self, path):
        self.path = path
        with self.connect() as db:
            db.execute("CREATE TABLE IF NOT EXISTS runs (id TEXT PRIMARY KEY, body_hash TEXT NOT NULL, phase TEXT NOT NULL, record TEXT NOT NULL)")

    @contextmanager
    def connect(self):
        db = sqlite3.connect(self.path, timeout=5)
        try:
            db.execute("PRAGMA journal_mode=WAL")
            db.execute("PRAGMA synchronous=FULL")
            with db:
                yield db
        finally:
            # sqlite3's transaction context does not close the connection.
            db.close()

    def read(self, rid):
        with self.connect() as db:
            row = db.execute("SELECT body_hash, phase, record FROM runs WHERE id=?", (rid,)).fetchone()
        return None if row is None else (row[0], row[1], json.loads(row[2]))

    def reserve(self, rid, digest, record):
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            old = db.execute("SELECT body_hash FROM runs WHERE id=?", (rid,)).fetchone()
            if old:
                if old[0] != digest:
                    raise RuntimeErrorSafe("runtime-body-conflict")
                return False
            # Independent process-capacity fence, not a second project scheduler.
            # ClosedBeforeExecutor is operator-only reconciliation, never set by
            # HTTP intake/retry. It asserts NO executor was ever started and is
            # NOT an exit/success receipt or permission to callback the product.
            if db.execute("SELECT 1 FROM runs WHERE phase NOT IN ('Frozen','ClosedBeforeExecutor') LIMIT 1").fetchone():
                raise RuntimeErrorSafe("runtime-capacity-held")
            db.execute("INSERT INTO runs VALUES (?,?,?,?)", (rid, digest, "Intent", json.dumps(record)))
            return True

    def save(self, rid, phase, record):
        with self.connect() as db:
            cursor = db.execute("UPDATE runs SET phase=?,record=? WHERE id=?", (phase, json.dumps(record), rid))
            if cursor.rowcount != 1:
                raise RuntimeErrorSafe("runtime-record-missing")


class Runtime:
    def __init__(self, config, docker=None):
        self.config = config
        self.docker = docker or Docker()
        self.lock = threading.RLock()
        self.root = Path(config["statePath"])
        self.root.mkdir(mode=0o700, parents=True, exist_ok=True)
        if self.root.is_symlink() or self.root.stat().st_mode & 0o077:
            raise RuntimeErrorSafe("runtime-state-permissions")
        self.store = Store(self.root / "runs.sqlite")
        self.policy = config["policy"]
        if not re.fullmatch(r"[0-9A-F]{64}", config["bindingFingerprint"]):
            raise RuntimeErrorSafe("runtime-binding-invalid")
        for field in ("organizationId", "workspaceId", "projectId"):
            if uuid.UUID(config[field]).int == 0 or str(uuid.UUID(config[field])) != config[field]:
                raise RuntimeErrorSafe("runtime-scope-invalid")
        if (not re.fullmatch(r"sha256:[0-9a-f]{64}", self.policy["imageDigest"]) or
                not re.fullmatch(r"[0-9a-f]{40,64}", self.policy["revision"]) or
                not re.fullmatch(r"[0-9a-f]{64}", config["seccompSha256"]) or
                not re.fullmatch(r"[0-9a-f]{64}", self.policy["acceptanceSha256"])):
            raise RuntimeErrorSafe("runtime-policy-invalid")

    def scope(self, rid):
        if uuid.UUID(rid).int == 0 or str(uuid.UUID(rid)) != rid:
            raise RuntimeErrorSafe("runtime-run-invalid")
        return {k: self.config[k] for k in ("organizationId", "workspaceId", "projectId")} | dict(runId=rid)

    def receipt(self, rid, state, **fields):
        return dict(protocol="fanasa-codex/v1", scope=self.scope(rid), nativeId="codex_" + uuid.UUID(rid).hex,
                    bindingFingerprint=self.config["bindingFingerprint"], policy=self.policy, state=state,
                    executorExited=False, exitCode=None, modelCompleted=False, containerId=None, usage=None,
                    validation=None) | fields

    def ready(self, rid):
        self.scope(rid)
        info = json_object(self.docker.call("info", "--format", "{{json .}}"))
        if (info.get("ID") != self.config["engineId"] or info.get("ServerVersion") != self.config["engineVersion"] or
                "name=rootless" not in info.get("SecurityOptions", []) or info.get("CgroupVersion") != "2" or
                info.get("CgroupDriver") != "systemd" or
                not all(info.get(k) is True for k in ("MemoryLimit", "SwapLimit", "PidsLimit", "CpuCfsQuota"))):
            raise RuntimeErrorSafe("runtime-daemon-not-ready")
        image = json_object(self.docker.call("image", "inspect", self.policy["imageDigest"]))
        if len(image) != 1 or image[0]["Id"] != self.policy["imageDigest"]:
            raise RuntimeErrorSafe("runtime-image-not-ready")
        profile = json_object(Path(self.config["seccompPath"]).read_bytes(), 256 * 1024)
        digest = hashlib.sha256(json.dumps(profile, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
        if digest != self.config["seccompSha256"]:
            raise RuntimeErrorSafe("runtime-seccomp-changed")
        acceptance = Path(self.config["acceptancePath"])
        if (not acceptance.is_absolute() or acceptance.resolve(strict=True) != acceptance or
                {x.name for x in acceptance.iterdir()} != {"check.py"} or
                (acceptance / "check.py").is_symlink() or
                hashlib.sha256((acceptance / "check.py").read_bytes()).hexdigest() != self.policy["acceptanceSha256"] or
                acceptance.stat().st_mode & 0o022 or (acceptance / "check.py").stat().st_mode & 0o022 or
                type(self.config["acceptanceTestCount"]) is not int or not 1 <= self.config["acceptanceTestCount"] <= 100000):
            raise RuntimeErrorSafe("runtime-acceptance-changed")
        return self.receipt(rid, "Ready")

    def helper(self, volume, mode, data=None):
        return self.docker.call("run", "--rm", "-i", "--network", "none", "--read-only", "--user", "10000:10000",
                                "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--memory", "128m",
                                "--memory-swap", "128m", "--cpus", "0.25", "--pids-limit", "32",
                                "--mount", "type=volume,src=" + volume + ",dst=/workspace" + (",readonly" if mode == "snapshot" else ""),
                                self.policy["imageDigest"], "python", "-B", "/opt/fanasa-codex/codex_workspace.py", mode,
                                data=data, timeout=20)

    def submit(self, rid, raw):
        body = json_object(raw, 100 * 1024)
        expected_keys = {"protocol", "scope", "nativeId", "bindingFingerprint", "policy", "prompt", "instructions", "provider", "model"}
        if (not isinstance(body, dict) or set(body) != expected_keys or body["protocol"] != "fanasa-codex/v1" or
                body["scope"] != self.scope(rid) or body["nativeId"] != "codex_" + uuid.UUID(rid).hex or
                body["bindingFingerprint"] != self.config["bindingFingerprint"] or body["policy"] != self.policy or
                body["provider"] != self.config["provider"] or body["model"] != self.config["model"] or
                not isinstance(body["prompt"], str) or not 0 < len(body["prompt"].encode()) <= 65536 or
                body["instructions"] is not None and (not isinstance(body["instructions"], str) or len(body["instructions"].encode()) > 16384)):
            raise RuntimeErrorSafe("runtime-submission-invalid")
        digest = hashlib.sha256(raw).hexdigest()
        with self.lock:
            existing = self.store.read(rid)
            if existing:
                if existing[0] != digest:
                    raise RuntimeErrorSafe("runtime-body-conflict")
                if existing[1] in ("Intent", "Starting", "Uncertain", "Validating"):
                    raise RuntimeErrorSafe("runtime-reconciliation-required")
                return self.observe(rid)
            self.ready(rid)
            # Read only the immutable operator-approved local repository. Git is
            # never run with task-supplied args, a shell, hooks or network auth.
            repository = Path(self.config["repositoryPath"])
            baseline = bounded_command(["git", "-C", str(repository), "archive", "--format=tar", self.policy["revision"]],
                                       timeout=10, limit=16 * 1024 * 1024,
                                       env={"PATH": "/usr/bin:/bin", "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null"})
            safe_snapshot(baseline)
            prefix = "fanasa-codex-" + uuid.UUID(rid).hex
            record = dict(scope=self.scope(rid), nativeId=body["nativeId"], bindingFingerprint=body["bindingFingerprint"],
                          policy=self.policy, provider=self.config["provider"], model=self.config["model"],
                          volume=prefix + "-workspace", network=prefix + "-private", egress=prefix + "-egress",
                          proxy=prefix + "-model", executor=prefix, engineId=self.config["engineId"],
                          engineVersion=self.config["engineVersion"], seccompSha256=self.config["seccompSha256"],
                          containerId=None, stopRequested=False, frozen=None)
            if not self.store.reserve(rid, digest, record):
                raise RuntimeErrorSafe("runtime-concurrent-intake")
            try:
                private = self.root / uuid.UUID(rid).hex
                private.mkdir(mode=0o700)
                (private / "baseline.tar").write_bytes(baseline)
                self.docker.call("volume", "create", "--label", "fanasa.run-id=" + rid, record["volume"])
                self.helper(record["volume"], "seed", baseline)
                self.docker.call("network", "create", "--internal", "--label", "fanasa.run-id=" + rid, record["network"])
                self.docker.call("network", "create", "--label", "fanasa.run-id=" + rid, record["egress"])
                client_key = secrets.token_urlsafe(32)
                self.docker.call("create", "-i", "--name", record["proxy"], "--read-only", "--user", "10000:10000",
                                 "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--memory", "128m",
                                 "--memory-swap", "128m", "--cpus", "0.25", "--pids-limit", "32", "--log-driver", "none",
                                 "--network", record["network"], self.policy["imageDigest"],
                                 "python", "-B", "/opt/fanasa-codex/codex_model_proxy.py", "--stdin")
                self.docker.call("network", "connect", record["egress"], record["proxy"])
                profile = json_object(Path(self.config["providerProfilePath"]).read_bytes(), 64 * 1024)
                startup = dict(config=dict(clientKey=client_key, model=self.config["model"], expiresAt=time.time() + 300, maxRequests=20), profile=profile)
                self.docker.start(record["proxy"], json.dumps(startup).encode() + b"\n")
                startup = profile = None
                proxy = json_object(self.docker.call("inspect", record["proxy"]))[0]
                address = proxy["NetworkSettings"]["Networks"][record["network"]]["IPAddress"]
                import ipaddress
                if not ipaddress.ip_address(address).is_private:
                    raise RuntimeErrorSafe("runtime-proxy-network-invalid")
                config = '\n'.join(['model = ' + json.dumps(self.config["model"]), 'model_provider = "fanasa-run"',
                                    'approval_policy = "never"', 'sandbox_mode = "workspace-write"', 'model_reasoning_effort = "low"',
                                    '[model_providers.fanasa-run]', 'name = "Fanasa per-run relay"',
                                    'base_url = ' + json.dumps("http://" + address + ":8080/v1"), 'wire_api = "responses"',
                                    'requires_openai_auth = false', 'supports_websockets = false',
                                    'request_max_retries = 0', 'stream_max_retries = 0',
                                    'experimental_bearer_token = ' + json.dumps(client_key)]) + '\n'
                labels = {"kind": "codex-executor-v1", "binding-fingerprint": record["bindingFingerprint"],
                          "sandbox-id": self.policy["sandboxId"], "revision": self.policy["revision"], "repository": self.policy["repositoryUrl"]}
                labels.update({key: record["scope"][prop] for key, prop in (("organization-id", "organizationId"),
                              ("workspace-id", "workspaceId"), ("project-id", "projectId"), ("run-id", "runId"))})
                args = ["create", "-i", "--name", record["executor"], "--init", "--read-only", "--user", "10000:10000",
                        "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--security-opt", "seccomp=" + self.config["seccompPath"],
                        "--memory", "768m", "--memory-swap", "768m", "--cpus", "1", "--pids-limit", "128",
                        "--network", record["network"], "--dns", "127.0.0.1", "--log-driver", "local", "--log-opt", "max-size=5m", "--log-opt", "max-file=1", "--log-opt", "compress=false",
                        "--tmpfs", "/tmp:rw,nosuid,nodev,size=128m", "--tmpfs", "/state:rw,nosuid,nodev,size=128m,uid=10000,gid=10000",
                        "--env", "HOME=/state", "--env", "CODEX_HOME=/state/codex", "--mount", "type=volume,src=" + record["volume"] + ",dst=/workspace"]
                for key, value in labels.items():
                    args.extend(["--label", "fanasa." + key + "=" + value])
                args.extend([self.policy["imageDigest"], "python", "-B", "/opt/fanasa-codex/codex_workspace.py", "launch"])
                record["containerId"] = self.docker.call(*args).decode().strip()
                self.store.save(rid, "Starting", record)
                verify_executor(json_object(self.docker.call("info", "--format", "{{json .}}")),
                                json_object(self.docker.call("inspect", record["containerId"])), record, require_exit=False)
                self.verify_network(record)
                prompt = (body["instructions"] or "") + "\n\n" + body["prompt"]
                self.docker.start(record["executor"], json.dumps(dict(config=config, prompt=prompt)).encode())
                self.store.save(rid, "Running", record)
                return self.observe(rid)
            except Exception:
                self.store.save(rid, "Uncertain", record)
                raise RuntimeErrorSafe("runtime-reconciliation-required") from None

    def observe(self, rid):
        with self.lock:
            row = self.store.read(rid)
            if row is None or row[2]["containerId"] is None:
                raise RuntimeErrorSafe("runtime-run-unavailable")
            _, phase, record = row
            if record["frozen"] is not None:
                return record["frozen"]
            c = json_object(self.docker.call("inspect", record["containerId"]))[0]
            if c["Id"] != record["containerId"] or c["Image"] != self.policy["imageDigest"] or c["Config"]["Labels"].get("fanasa.run-id") != rid:
                raise RuntimeErrorSafe("runtime-executor-changed")
            state = c["State"]
            receipt = self.receipt(rid, "Uncertain", containerId=record["containerId"])
            if phase in ("Intent", "Starting", "Uncertain", "Validating"):
                return receipt
            if state["Running"]:
                return receipt | dict(state="StopRequested" if record["stopRequested"] else "Running")
            if state["Status"] != "exited":
                return receipt
            raw = self.docker.call("logs", record["containerId"], limit=4 * 1024 * 1024)
            try:
                observation = cli_observation(raw, record["provider"], record["model"])
            except EvidenceError:
                observation = dict(modelCompleted=False, usage=None)
            status = "Cancelled" if record["stopRequested"] else "Completed" if state["ExitCode"] == 0 and observation["modelCompleted"] else "Failed"
            return receipt | dict(state=status, executorExited=True, exitCode=state["ExitCode"], **observation)

    def verify_network(self, record):
        network = json_object(self.docker.call("network", "inspect", record["network"]))
        c = json_object(self.docker.call("inspect", record["containerId"]))[0]
        proxy = json_object(self.docker.call("inspect", record["proxy"]))[0]
        if (len(network) != 1 or network[0]["Internal"] is not True or network[0]["Driver"] != "bridge" or
                set(network[0].get("Containers", {})) - {record["containerId"], proxy["Id"]} or
                set(c["NetworkSettings"]["Networks"]) != {record["network"]} or c["HostConfig"].get("Dns") != ["127.0.0.1"]):
            raise RuntimeErrorSafe("runtime-network-unverified")

    def evidence(self, rid):
        with self.lock:
            row = self.store.read(rid)
            if row is None:
                raise RuntimeErrorSafe("runtime-run-unavailable")
            record = row[2]
            if record["frozen"] is not None:
                return record["frozen"]
            preliminary = self.observe(rid)
            if not preliminary["executorExited"]:
                return preliminary
            self.verify_network(record)
            if preliminary["state"] != "Completed":
                # Still prove scope, isolation and actual exit before allowing a
                # failure/stop callback to release the journal's capacity.
                verify_executor(json_object(self.docker.call("info", "--format", "{{json .}}")),
                                json_object(self.docker.call("inspect", record["containerId"])), record)
                record["frozen"] = preliminary
                self.store.save(rid, "Frozen", record)
                try: self.docker.call("stop", "--time", "2", record["proxy"])
                except RuntimeErrorSafe: pass  # Frozen exit/evidence never changes on cleanup failure.
                return preliminary
            self.store.save(rid, "Validating", record)
            try:
                private = self.root / uuid.UUID(rid).hex
                snapshot = self.helper(record["volume"], "snapshot")
                safe_snapshot(snapshot)
                (private / "snapshot.tar").write_bytes(snapshot)
                validation_volume = record["executor"] + "-validation"
                self.docker.call("volume", "create", "--label", "fanasa.run-id=" + rid, validation_volume)
                self.helper(validation_volume, "seed", snapshot)
                # Operator-owned acceptance suite; not copied from task/model.
                acceptance = str(Path(self.config["acceptancePath"]).resolve(strict=True))
                if hashlib.sha256((Path(acceptance) / "check.py").read_bytes()).hexdigest() != self.policy["acceptanceSha256"]:
                    raise RuntimeErrorSafe("runtime-acceptance-changed")
                check_name = record["executor"] + "-check"
                self.docker.call("create", "--name", check_name, "--init", "--network", "none", "--read-only", "--user", "10000:10000",
                                 "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--memory", "256m", "--memory-swap", "256m",
                                 "--cpus", "1", "--pids-limit", "64", "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=64m",
                                 "--mount", "type=volume,src=" + validation_volume + ",dst=/workspace,readonly",
                                 "--mount", "type=bind,src=" + acceptance + ",dst=/acceptance,readonly", self.policy["imageDigest"],
                                 "timeout", "--signal=TERM", "--kill-after=2s", "15s", "python", "-I", "-B", "/acceptance/check.py")
                self.docker.call("start", check_name)
                self.docker.call("wait", check_name, timeout=20)
                checked = json_object(self.docker.call("inspect", check_name))[0]
                if checked["State"]["Running"] or checked["HostConfig"]["NetworkMode"] != "none" or checked["Image"] != self.policy["imageDigest"]:
                    raise RuntimeErrorSafe("runtime-validation-unverified")
                output = self.docker.validation_logs(check_name)
                if not output.strip(): raise RuntimeErrorSafe("runtime-validation-output-missing")
                validation = acceptance_result(output, checked["State"]["ExitCode"], self.policy, self.config["acceptanceTestCount"])
                receipt = collect(record, self.docker.call("info", "--format", "{{json .}}"),
                                  self.docker.call("inspect", record["containerId"]),
                                  self.docker.call("logs", record["containerId"], limit=4 * 1024 * 1024),
                                  (private / "baseline.tar").read_bytes(), snapshot, validation)
                (private / "test-output.txt").write_bytes(output)
                _, manifest = changes((private / "baseline.tar").read_bytes(), snapshot)
                (private / "change-manifest.json").write_bytes(manifest)
                # No automatic deletion: exact frozen bytes and artifacts stay
                # private for audit/reconciliation. Cleanup needs separate policy.
                record["frozen"] = receipt
                self.store.save(rid, "Frozen", record)
                try: self.docker.call("stop", "--time", "2", record["proxy"])
                except RuntimeErrorSafe: pass
                return receipt
            except Exception:
                self.store.save(rid, "Uncertain", record)
                raise RuntimeErrorSafe("runtime-validation-unconfirmed") from None

    def stop(self, rid):
        with self.lock:
            row = self.store.read(rid)
            if row is None or row[2]["containerId"] is None:
                raise RuntimeErrorSafe("runtime-stop-unavailable")
            record = row[2]
            if record["frozen"] is not None:
                return record["frozen"]
            record["stopRequested"] = True
            self.store.save(rid, "Running", record)
            self.docker.call("stop", "--time", "5", record["containerId"])
            return self.observe(rid)


def handler_type(runtime, transport_key):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_): pass

        def respond(self, status, body):
            raw = json.dumps(body, separators=(",", ":")).encode()
            self.send_response(status); self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(raw))); self.end_headers(); self.wfile.write(raw)

        def dispatch(self):
            try:
                self.connection.settimeout(10)
                auth = self.headers.get_all("Authorization", [])
                if len(auth) != 1 or not hmac.compare_digest(auth[0], "Bearer " + transport_key):
                    self.respond(401, dict(code="runtime-denied")); return
                run_headers = self.headers.get_all("X-Fanasa-Run", [])
                binding = self.headers.get_all("X-Fanasa-Binding", [])
                if len(run_headers) != 1 or binding != [runtime.config["bindingFingerprint"]]:
                    raise RuntimeErrorSafe("runtime-scope-denied")
                rid = run_headers[0]; runtime.scope(rid)
                native_id = "codex_" + uuid.UUID(rid).hex
                if self.command == "GET":
                    if self.path == "/v1/codex/ready/" + native_id: result = runtime.ready(rid)
                    elif self.path == "/v1/codex/runs/" + native_id: result = runtime.observe(rid)
                    elif self.path == "/v1/codex/runs/" + native_id + "/evidence": result = runtime.evidence(rid)
                    else: raise RuntimeErrorSafe("runtime-route-denied")
                    self.respond(200, result); return
                lengths = self.headers.get_all("Content-Length", [])
                if (self.headers.get_all("Idempotency-Key", []) != [uuid.UUID(rid).hex] or
                        len(lengths) != 1 or not lengths[0].isdigit() or not 0 < int(lengths[0]) <= 100 * 1024 or
                        self.headers.get("Transfer-Encoding") or self.headers.get("Content-Encoding") or
                        self.headers.get("Content-Type", "").split(";", 1)[0].strip() != "application/json"):
                    raise RuntimeErrorSafe("runtime-body-invalid")
                raw = self.rfile.read(int(lengths[0]))
                if len(raw) != int(lengths[0]): raise RuntimeErrorSafe("runtime-body-incomplete")
                if self.path == "/v1/codex/runs": result = runtime.submit(rid, raw)
                elif self.path == "/v1/codex/runs/" + native_id + "/stop": result = runtime.stop(rid)
                else: raise RuntimeErrorSafe("runtime-route-denied")
                self.respond(202, result)
            except (RuntimeErrorSafe, EvidenceError):
                self.respond(409, dict(code="runtime-reconciliation-required"))
            except Exception:
                self.respond(503, dict(code="runtime-unavailable"))
        do_GET = dispatch
        do_POST = dispatch
    return Handler


if __name__ == "__main__":
    os.umask(0o077)
    parser = argparse.ArgumentParser(); parser.add_argument("--config", required=True); args = parser.parse_args()
    config = json_object(Path(args.config).read_bytes(), 64 * 1024)
    if config.get("enabled") is not True:
        raise RuntimeErrorSafe("runtime-disabled")
    runtime = Runtime(config)
    transport = Path(config["transportKeyPath"]).read_text().strip()
    if not 32 <= len(transport) <= 1024 or any(c.isspace() for c in transport):
        raise RuntimeErrorSafe("runtime-auth-invalid")
    server = ThreadingHTTPServer(("127.0.0.1", config["port"]), handler_type(runtime, transport))
    tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); tls.minimum_version = ssl.TLSVersion.TLSv1_2
    tls.load_cert_chain(config["tlsCertPath"], config["tlsKeyPath"])
    server.socket = tls.wrap_socket(server.socket, server_side=True)
    server.daemon_threads = True; server.serve_forever()
