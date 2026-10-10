#!/usr/bin/env python3
"""Explicit operator install and read-only liveness collection; never dispatches.

Run as root on the Debian VPS after the dedicated rootless daemon is ready.
Runtime instances deliberately have an internal-only network and EMPTY scoped
state/workspace volumes. No model key, repo, host mount or Docker socket is given
to them. This acceptance lane is not a multi-tenant task execution sandbox.
"""
import argparse
import base64
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import pwd
import secrets
import stat
import subprocess
import tempfile
import urllib.error
import urllib.request

ROOT = Path("/opt/fanasa-agent-runtime")
ACCOUNT = "fanasa-agent-runner"
NETWORK = "fanasa-agent-runtime-internal"
ENGINES = ("opencode", "codex", "hermes")
PORTS = {"hermes": 8642, "opencode": 4096}


def checked(command, timeout=60, quiet=False):
    result = subprocess.run(command, capture_output=True, text=True, timeout=timeout, check=False)
    if result.returncode:
        # Installer output does not contain credentials. Never print docker
        # inspection Environment or authenticated upstream bodies.
        raise RuntimeError("Command failed: " + command[0] + " (" + str(result.returncode) + ")")
    return result.stdout.strip()


def docker(*args, timeout=60):
    uid = pwd.getpwnam(ACCOUNT).pw_uid
    return checked(["runuser", "-u", ACCOUNT, "--", "env",
                    "HOME=/home/" + ACCOUNT, "XDG_RUNTIME_DIR=/run/user/" + str(uid),
                    "DOCKER_HOST=unix:///run/user/" + str(uid) + "/docker.sock", "docker", *args], timeout)


def safe_root():
    if os.geteuid() != 0 or ROOT.resolve() != ROOT or ROOT.is_symlink():
        raise RuntimeError("Use the explicit root operator and non-linked installation root.")
    for directory in (ROOT, ROOT / "operator"):
        metadata = directory.stat()
        if metadata.st_uid != 0 or metadata.st_mode & 0o022 or not stat.S_ISDIR(metadata.st_mode):
            raise RuntimeError("Installation/operator directories must be root-owned and not writable by others.")
    p = pwd.getpwnam(ACCOUNT)
    if p.pw_uid == 0 or p.pw_dir != "/home/" + ACCOUNT:
        raise RuntimeError("Unexpected runtime account.")
    info = json.loads(docker("info", "--format", "{{json .}}"))
    if "name=rootless" not in info["SecurityOptions"] or info["CgroupVersion"] != "2" or not all(
            info.get(x) for x in ("MemoryLimit", "SwapLimit", "CpuCfsQuota", "PidsLimit")):
        raise RuntimeError("Rootless resource enforcement is not ready.")
    return p


def atomic_json(path, value):
    fd, name = tempfile.mkstemp(prefix=".runtime-report-", dir=path.parent)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, "w") as stream:
            json.dump(value, stream, separators=(",", ":"))
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def transport_file(engine):
    return ROOT / "private" / (engine + ".env")


def transport(engine, create=False):
    path = transport_file(engine)
    if path.is_symlink():
        raise RuntimeError("Linked secret file is forbidden.")
    if not path.exists() and create:
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        name = "API_SERVER_KEY" if engine == "hermes" else "OPENCODE_SERVER_PASSWORD"
        with os.fdopen(fd, "w") as stream:
            stream.write(name + "=" + secrets.token_hex(32) + "\n")
    if path.stat().st_uid != 0 or path.stat().st_mode & 0o077:
        raise RuntimeError("Secret file must be private and root-owned.")
    text = path.read_text().strip()
    name, value = text.split("=", 1)
    expected_name = "API_SERVER_KEY" if engine == "hermes" else "OPENCODE_SERVER_PASSWORD"
    if name != expected_name or len(value) != 64 or any(c not in "0123456789abcdef" for c in value):
        raise RuntimeError("Invalid transport secret.")
    return name, value


def container_options(engine):
    return ["--name", "fanasa-native-" + engine, "--init", "--user", "10000:10000",
            "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "--memory", "512m", "--memory-swap", "512m", "--cpus", "1", "--pids-limit", "128",
            "--network", NETWORK, "--restart", "unless-stopped", "--log-driver", "local",
            "--log-opt", "max-size=5m", "--log-opt", "max-file=2",
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=64m", "--shm-size", "32m",
            "--label", "fanasa.kind=native-readiness-v1", "--label", "fanasa.engine=" + engine,
            "--mount", "type=volume,src=fanasa-native-" + engine + "-state,dst=/state",
            "--mount", "type=volume,src=fanasa-native-" + engine + "-workspace,dst=/workspace"]


def install():
    account = safe_root()
    build = ROOT / "build"
    pins = json.loads((ROOT / "operator" / "runtime-pins.json").read_text())
    checked(["install", "-m", "0644", "-o", ACCOUNT, "-g", ACCOUNT,
             str(ROOT / "operator" / "runtime.dockerignore"), str(build / ".dockerignore")])
    for name, item in pins["downloads"].items():
        path = build / name
        if not path.exists():
            try:
                checked(["runuser", "-u", ACCOUNT, "--", "curl", "--fail", "--location", "--silent",
                         "--show-error", "--retry", "1", "--connect-timeout", "15", "--max-time", "180",
                         "-o", str(path), item["url"]], 400)
            except RuntimeError:
                if "fallbackUrl" not in item:
                    raise
                checked(["runuser", "-u", ACCOUNT, "--", "curl", "--fail", "--location", "--silent",
                         "--show-error", "--retry", "1", "--connect-timeout", "15", "--max-time", "180",
                         "-o", str(path), item["fallbackUrl"]], 400)
        if path.is_symlink() or hashlib.file_digest(path.open("rb"), "sha256").hexdigest() != item["sha256"]:
            raise RuntimeError("Download checksum mismatch: " + name)
        print("Checksum verified: " + name, flush=True)
    # All archives are immutable official downloads. Existing names/images are
    # not silently overwritten; refuse incompatible installations.
    for engine in ENGINES:
        version = pins["versions"][engine]
        tag = "fanasa/native-" + engine + ":" + version
        found = docker("image", "ls", "--filter", "reference=" + tag, "--format", "{{.ID}}")
        if not found:
            filename = "Hermes.Dockerfile" if engine == "hermes" else "NativeCli.Dockerfile"
            checked(["install", "-m", "0644", "-o", ACCOUNT, "-g", ACCOUNT,
                     str(ROOT / "operator" / filename), str(build / filename)])
            log_fd, log_name = tempfile.mkstemp(prefix="build-" + engine + "-", suffix=".log", dir=ROOT)
            args = ["runuser", "-u", ACCOUNT, "--", "env", "HOME=" + account.pw_dir,
                    "XDG_RUNTIME_DIR=/run/user/" + str(account.pw_uid), "docker", "build", "--progress", "plain",
                    "--build-arg", "ENGINE=" + engine, "--build-arg", "VERSION=" + version,
                    "-f", str(build / filename), "-t", tag, str(build)]
            with os.fdopen(log_fd, "w") as output:
                result = subprocess.run(args, stdout=output, stderr=subprocess.STDOUT, timeout=1800)
            if result.returncode:
                raise RuntimeError("Image build failed: " + engine + "; inspect private build log.")
        inspected = json.loads(docker("image", "inspect", tag))[0]
        if inspected["Config"].get("User") != "10000:10000" or inspected["Config"].get("Labels", {}).get("fanasa.version") != version:
            raise RuntimeError("Image identity mismatch.")
        image = inspected["Id"]
        probe = ["run", "--rm", "--network", "none", "--read-only", "--user", "10000:10000", "--cap-drop", "ALL",
                 "--security-opt", "no-new-privileges", "--memory", "256m", "--memory-swap", "256m",
                 "--cpus", "1", "--pids-limit", "64", "--tmpfs", "/state:rw,nosuid,nodev,size=16m,uid=10000,gid=10000",
                 "--tmpfs", "/tmp:rw,nosuid,nodev,size=32m", "-e", "OPENCODE_DISABLE_MODELS_FETCH=true", image]
        if engine == "hermes":
            output = docker(*probe, "python", "-c", "import hermes_cli,fastapi,uvicorn,sqlite3; assert sqlite3.sqlite_version_info >= (3,51,3); print('hermes-core-ready')")
            if output != "hermes-core-ready":
                raise RuntimeError("Hermes core probe failed.")
        else:
            output = docker(*probe, engine, "--version")
            if version not in output:
                raise RuntimeError("Actual CLI version differs from pin.")
        print("Image ready: " + engine + " " + image, flush=True)
    networks = docker("network", "ls", "--filter", "name=^" + NETWORK + "$", "--format", "{{.ID}}")
    if not networks:
        docker("network", "create", "--internal", "--label", "fanasa.kind=native-readiness-v1", NETWORK)
    network = json.loads(docker("network", "inspect", NETWORK))[0]
    if not network["Internal"] or network.get("Labels", {}).get("fanasa.kind") != "native-readiness-v1":
        raise RuntimeError("Unexpected network policy.")
    (ROOT / "private").mkdir(mode=0o700, exist_ok=True)
    for engine in ("hermes", "opencode"):
        transport(engine, create=True)
        tag = "fanasa/native-" + engine + ":" + pins["versions"][engine]
        image = json.loads(docker("image", "inspect", tag))[0]["Id"]
        name = "fanasa-native-" + engine
        existing = docker("ps", "-a", "--filter", "name=^/" + name + "$", "--format", "{{.ID}}")
        if existing:
            c = json.loads(docker("inspect", existing))[0]
            if c["Image"] != image or c["Config"].get("Labels", {}).get("fanasa.kind") != "native-readiness-v1":
                raise RuntimeError("Existing container differs; explicit reconciliation required.")
            if not c["State"]["Running"]:
                docker("start", existing)
            continue
        # docker client executes as runner and must read only this transport
        # file. Copy to a runner-only file, never print its value or CLI arg.
        private = Path(account.pw_dir) / ".config" / "fanasa-agent-runtime"
        private.mkdir(mode=0o700, exist_ok=True)
        os.chown(private, account.pw_uid, account.pw_gid)
        env = private / (engine + ".env")
        checked(["install", "-m", "0600", "-o", ACCOUNT, "-g", ACCOUNT, str(transport_file(engine)), str(env)])
        # Internal networks intentionally cannot publish a host port. Health is
        # sampled inside the existing container, without enabling model egress.
        args = ["run", "-d", *container_options(engine), "--env-file", str(env)]
        if engine == "hermes":
            args += ["-e", "API_SERVER_ENABLED=true", "-e", "API_SERVER_HOST=0.0.0.0", "-e", "API_SERVER_PORT=8642",
                     image, "python", "-m", "hermes_cli.main", "gateway", "run"]
        else:
            args += ["-e", "OPENCODE_DISABLE_AUTOUPDATE=true", "-e", "OPENCODE_DISABLE_MODELS_FETCH=true",
                     "-e", "OPENCODE_CONFIG_CONTENT=" + json.dumps({"permission": "deny", "autoupdate": False,
                         "enabled_providers": []}), image, "opencode", "serve", "--hostname", "0.0.0.0", "--port", "4096"]
        docker(*args, timeout=90)
        print("Private readiness runtime started: " + engine, flush=True)


def probe_health(engine, key):
    authorization = "Bearer " + key if engine == "hermes" else "Basic " + base64.b64encode(("opencode:" + key).encode()).decode()
    path = "/health/detailed" if engine == "hermes" else "/global/health"
    url = "http://127.0.0.1:" + str(PORTS[engine]) + path
    # Never follows redirects or accepts an unauthenticated success as proof.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs):
            return None
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        with opener.open(urllib.request.Request(url, headers={"Authorization": authorization}), timeout=5) as response:
            raw = response.read(65537)
            if response.status != 200 or len(raw) > 65536:
                return False
            body = json.loads(raw)
            if not isinstance(body, dict):
                return False
            if engine == "opencode" and body.get("healthy") is not True:
                return False
            if engine == "hermes" and (body.get("platform") != "hermes-agent" or not isinstance(body.get("api_server"), dict)):
                return False
        try:
            with opener.open(url, timeout=5) as unauthorized:
                return False
        except urllib.error.HTTPError as error:
            return error.code in (401, 403)
    except (OSError, ValueError, urllib.error.URLError):
        return False


def health(engine):
    # Fixed, read-only probe runs as the native non-root user. The secret stays
    # in its existing environment, never in argv/stdout or a host API request.
    import inspect
    script = ("import os,json,base64,urllib.request,urllib.error\nPORTS=" + repr(PORTS) + "\n" +
              inspect.getsource(probe_health) +
              "\nengine=" + repr(engine) + "\nkey=os.environ.get(" +
              repr("API_SERVER_KEY" if engine == "hermes" else "OPENCODE_SERVER_PASSWORD") +
              ", '')\nprint('true' if key and probe_health(engine,key) else 'false')")
    try:
        return docker("exec", "fanasa-native-" + engine, "python", "-c", script, timeout=15) == "true"
    except (RuntimeError, subprocess.TimeoutExpired):
        return False


def collect():
    safe_root()
    pins = json.loads((ROOT / "operator" / "runtime-pins.json").read_text())
    runtimes = []
    for engine in ENGINES:
        version = pins["versions"][engine]
        tag = "fanasa/native-" + engine + ":" + version
        installed = bool(docker("image", "ls", "--filter", "reference=" + tag, "--format", "{{.ID}}"))
        healthy = installed and engine in PORTS and health(engine)
        runtimes.append({"engine": engine, "version": version if installed else None, "installed": installed,
                         "transportHealthy": healthy, "reason": "native-healthy" if healthy else
                         "cli-installed" if installed and engine == "codex" else "native-unreachable" if installed else "not-installed"})
    report = {"schema": "fanasa-agent-runtime/v1", "observedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(), "runtimes": runtimes}
    atomic_json(ROOT / "status.json", report)
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("install", "collect"))
    command = parser.parse_args().command
    if command == "install":
        install()
    else:
        collect()
