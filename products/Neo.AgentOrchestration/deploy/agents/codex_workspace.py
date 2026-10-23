"""Pinned image helper for bounded volume I/O, separate from model evidence."""
import io
import json
import os
from pathlib import Path
import shutil
import sys
import tarfile

from codex_evidence import EvidenceError, safe_snapshot


def seed(raw):
    files = safe_snapshot(raw)
    with tarfile.open(fileobj=io.BytesIO(raw), mode="r:") as tar:
        modes = {entry.name.removeprefix("./"): 0o755 if entry.mode & 0o111 else 0o644 for entry in tar if entry.isfile()}
    root = Path("/workspace")
    if list(root.iterdir()):
        raise EvidenceError("workspace-not-empty")
    for name, data in files.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("xb") as stream:
            stream.write(data)
        path.chmod(modes[name])


def snapshot():
    root = Path("/workspace")
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode="w", format=tarfile.USTAR_FORMAT) as tar:
        for path in sorted(root.rglob("*")):
            relative = path.relative_to(root)
            if any(part in ("__pycache__", ".pytest_cache", ".git", ".home") for part in relative.parts):
                continue
            if path.is_symlink():
                raise EvidenceError("snapshot-entry-invalid")
            if path.is_dir():
                continue
            if not path.is_file() or path.stat().st_size > 4 * 1024 * 1024:
                raise EvidenceError("snapshot-entry-invalid")
            data = path.read_bytes()
            row = tarfile.TarInfo(relative.as_posix()); row.size = len(data); row.mode = 0o755 if path.stat().st_mode & 0o111 else 0o644
            tar.addfile(row, io.BytesIO(data))
            if output.tell() > 16 * 1024 * 1024:
                raise EvidenceError("snapshot-limit")
    raw = output.getvalue()
    safe_snapshot(raw)
    return raw


def launch(raw):
    # Only a disposable relay key is in this config, never the upstream key.
    data = json.loads(raw)
    config = Path("/state/codex/config.toml")
    config.parent.mkdir(parents=True)
    with config.open("x") as stream:
        stream.write(data["config"])
    with open("/tmp/fanasa-prompt", "xb") as prompt:
        prompt.write(data["prompt"].encode())
    fd = os.open("/tmp/fanasa-prompt", os.O_RDONLY)
    os.dup2(fd, 0); os.close(fd)
    timeout = shutil.which("timeout"); codex = shutil.which("codex")
    if not timeout or not codex:
        raise EvidenceError("codex-runtime-incomplete")
    os.execv(timeout, [timeout, "--signal=TERM", "--kill-after=5s", "240s", codex, "exec", "--strict-config",
                      "--ephemeral", "--skip-git-repo-check", "--sandbox", "workspace-write", "--json", "-"])


if __name__ == "__main__":
    mode = sys.argv[1]
    if mode == "seed":
        seed(sys.stdin.buffer.read(16 * 1024 * 1024 + 1))
    elif mode == "snapshot":
        sys.stdout.buffer.write(snapshot())
    elif mode == "launch":
        launch(sys.stdin.buffer.read(128 * 1024 + 1))
    else:
        raise EvidenceError("workspace-mode-invalid")
