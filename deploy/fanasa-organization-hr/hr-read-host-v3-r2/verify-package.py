#!/usr/bin/env python3
"""Read-only source, dependency and actual-host input verification. No runtime acceptance."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path

PACKAGE = Path(__file__).resolve().parent


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def file_at(root: Path, relative: str) -> Path:
    if not isinstance(relative, str) or not relative or "\\" in relative:
        raise ValueError("Noncanonical package path")
    candidate = (root / relative).resolve()
    if not candidate.is_relative_to(root.resolve()) or not candidate.is_file():
        raise ValueError("Missing or escaped package input: " + relative)
    return candidate


def verify_files(root: Path, entries: list[dict]) -> int:
    seen: set[str] = set()
    for entry in entries:
        relative, expected = entry["path"], entry["sha256"]
        if relative in seen or not isinstance(expected, str) or len(expected) != 64:
            raise ValueError("Duplicate path or invalid digest")
        seen.add(relative)
        if sha(file_at(root, relative)) != expected:
            raise ValueError("Source bytes changed: " + relative)
    return len(seen)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=PACKAGE.parents[2])
    args = parser.parse_args()
    manifest = json.loads((PACKAGE / "SOURCE-PACKAGE.json").read_text(encoding="utf-8"))
    own = verify_files(PACKAGE, manifest["files"])
    dependencies = 0
    for dependency in manifest["dependencies"]:
        root = PACKAGE.parent / dependency["directory"]
        if sha(root / "SOURCE-PACKAGE.json") != dependency["manifestSha256"]:
            raise ValueError("Dependency manifest changed: " + dependency["directory"])
        dependencies += verify_files(root, json.loads((root / "SOURCE-PACKAGE.json").read_text(encoding="utf-8"))["files"])
    host = verify_files(args.repository.resolve(), manifest["hostInputs"])
    print(f"PASS: {own} host-package files, {dependencies} immutable dependency files, {host} actual-host inputs. No live acceptance.")


if __name__ == "__main__":
    main()
