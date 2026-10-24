"""Install one pinned binary wheel into this disposable test project's .tools only."""
from __future__ import annotations
import hashlib
import importlib
import json
from pathlib import Path
import platform
import stat
import struct
import sys
import urllib.request
import zipfile

VERSION = "2.6"
ROOT = Path(__file__).resolve().parent
TOOLS = ROOT / ".tools"
sys.dont_write_bytecode = True

def main():
    if sys.platform != "win32" or struct.calcsize("P") != 8:
        raise RuntimeError("Installer is pinned to Windows x64.")
    tag = f"cp{sys.version_info.major}{sys.version_info.minor}"
    filename = f"lupa-{VERSION}-{tag}-{tag}-win_amd64.whl"
    metadata_url = f"https://pypi.org/pypi/lupa/{VERSION}/json"
    with urllib.request.urlopen(metadata_url, timeout=30) as response:
        metadata = json.load(response)
    matches = [item for item in metadata["urls"] if item["filename"].lower() == filename]
    if len(matches) != 1:
        raise RuntimeError("No exact pinned binary wheel for this Python ABI.")
    entry = matches[0]
    wheel = TOOLS / "wheels" / entry["filename"]
    destination = TOOLS / "python"
    if ROOT not in wheel.resolve().parents or ROOT not in destination.resolve().parents:
        raise RuntimeError("Disposable dependency scope invalid.")
    wheel.parent.mkdir(parents=True, exist_ok=True)
    destination.mkdir(parents=True, exist_ok=True)
    if not wheel.exists():
        with urllib.request.urlopen(entry["url"], timeout=45) as response:
            content = response.read(10 * 1024 * 1024 + 1)
        if len(content) > 10 * 1024 * 1024 or hashlib.sha256(content).hexdigest() != entry["digests"]["sha256"]:
            raise RuntimeError("Wheel size/hash differs from release metadata.")
        wheel.write_bytes(content)
    digest = hashlib.sha256(wheel.read_bytes()).hexdigest()
    if digest != entry["digests"]["sha256"]:
        raise RuntimeError("Cached test wheel SHA256 invalid.")
    files = []
    with zipfile.ZipFile(wheel) as archive:
        for member in archive.infolist():
            if member.is_dir():
                continue
            relative = Path(member.filename)
            if relative.is_absolute() or ".." in relative.parts or stat.S_ISLNK(member.external_attr >> 16):
                raise RuntimeError("Wheel member rejected.")
            target = destination / relative
            if destination not in target.resolve().parents or any(path.is_symlink() for path in [target, *target.parents]):
                raise RuntimeError("Wheel member scope rejected.")
            raw = archive.read(member)
            expected = hashlib.sha256(raw).hexdigest()
            if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() != expected:
                raise RuntimeError("Existing disposable module differs from wheel.")
            target.parent.mkdir(parents=True, exist_ok=True)
            if not target.exists():
                target.write_bytes(raw)
            files.append({"path": str(relative).replace("\\", "/"), "sha256": expected})
    sys.path.insert(0, str(destination))
    lupa = importlib.import_module("lupa")
    if lupa.__version__ != VERSION:
        raise RuntimeError("Unexpected imported binding version.")
    lua = lupa.LuaRuntime(register_eval=False, register_builtins=False)
    evidence = {"schemaVersion": "fanasa.pilot-lua-test-dependency.v1", "testOnly": True,
        "package": "lupa", "version": VERSION, "wheel": entry["filename"],
        "wheelSha256": digest, "wheelBytes": wheel.stat().st_size, "releaseMetadataUrl": metadata_url,
        "wheelUrl": entry["url"], "pythonVersion": platform.python_version(), "luaVersion": lua.eval("_VERSION"),
        "installedFiles": files, "installationScope": str(destination), "permanentRuntimeChanged": False}
    (TOOLS / "dependency-lock.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: evidence[key] for key in ["testOnly", "version", "wheel", "wheelSha256", "pythonVersion", "luaVersion"]}))

if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(json.dumps({"passed": False, "failureClass": type(error).__name__}), file=sys.stderr)
        raise SystemExit(1)

