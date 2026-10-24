#!/usr/bin/env python3
"""Read-only closed source inventory verification. No runtime or deployment acceptance."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import stat
from pathlib import Path, PurePosixPath

PACKAGE = Path(__file__).absolute().parent
WEB = PurePosixPath("products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web")
PCC = PurePosixPath("docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/pcc")
GENERATED_DIRECTORIES = frozenset({"bin", "obj", "__pycache__"})
WEB_SOURCE_SUFFIXES = frozenset({".cs", ".cshtml", ".razor", ".resx", ".csproj", ".props", ".targets"})
# HR3 targets suppress environment/local appsettings.*.json from output/publish.
# Never read or hash those local files. Deployment overrides use environment variables.
WEB_PUBLISHED_CONFIGS = frozenset({"appsettings.json"})
ANCESTOR_METADATA = frozenset({
    "directory.build.props", "directory.build.targets", "directory.packages.props",
    "global.json", "nuget.config", ".editorconfig", "directory.build.rsp",
})
DEPENDENCY_MANIFESTS = {
    "hr-read-profile-v3": "9875088ffe6e3efc9553b0ca42873fa52b8a05d23d8101acd084c69e16b82329",
    "hr-read-pdp-v3": "4679302e5a4c409337a00a2cf8784c88a8b62d0a0799fcf37368c2460ed2c414",
}
HOST_INPUT_SCOPE = (
    "Web recursive C#/Razor/resources/project/build files and .editorconfig; "
    "all regular wwwroot assets; Web-root base appsettings.json only; direct linked PCC *.cs; "
    "fixed ancestor build metadata. Generated bin/obj/__pycache__ are excluded. "
    "Environment/local appsettings.*.json are excluded and suppressed by the HR3 build targets. "
    "Inventory contains paths and SHA-256 only, never file contents or live credentials."
)


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def canonical_path(relative: str) -> PurePosixPath:
    if (not isinstance(relative, str) or not relative or "\\" in relative or ":" in relative
            or "\x00" in relative or relative.startswith("/")
            or any(part in {"", ".", ".."} or part.endswith((" ", ".")) for part in relative.split("/"))):
        raise ValueError("Noncanonical package path")
    path = PurePosixPath(relative)
    if path.is_absolute() or path.as_posix() != relative:
        raise ValueError("Noncanonical package path")
    return path


def reject_link(path: Path) -> None:
    try:
        info = path.lstat()
    except OSError as error:
        raise ValueError("Missing inventory input: " + str(path)) from error
    if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
        raise ValueError("Symlink or junction inventory input: " + str(path))


def plain_path(root: Path, relative: str) -> Path:
    path = root.absolute()
    reject_link(path)
    if not path.is_dir():
        raise ValueError("Inventory root is not a directory: " + str(root))
    for part in canonical_path(relative).parts:
        path = path / part
        reject_link(path)
    return path


def file_at(root: Path, relative: str) -> Path:
    candidate = plain_path(root, relative)
    if not candidate.is_file():
        raise ValueError("Inventory input is not a regular file: " + relative)
    return candidate


def inventory_files(root: Path) -> set[str]:
    """Enumerate a closed tree without following symlinks or junctions."""
    root = root.absolute()
    reject_link(root)
    if not root.is_dir():
        raise ValueError("Inventory root is not a directory: " + str(root))
    paths: set[str] = set()

    def walk(directory: Path) -> None:
        for child in directory.iterdir():
            # Generated output directories are outside the declared source scope.
            if child.name in GENERATED_DIRECTORIES and child.is_dir():
                continue
            reject_link(child)
            if child.is_dir():
                walk(child)
            elif child.is_file():
                paths.add(child.relative_to(root).as_posix())
            else:
                raise ValueError("Unsupported inventory input: " + str(child))

    walk(root)
    return paths


def entry_paths(entries: list[dict]) -> set[str]:
    if not isinstance(entries, list) or not entries:
        raise ValueError("Inventory must be a nonempty array")
    seen: set[str] = set()
    aliases: set[str] = set()
    for entry in entries:
        if not isinstance(entry, dict) or "path" not in entry or "sha256" not in entry:
            raise ValueError("Malformed inventory entry")
        relative, expected = entry["path"], entry["sha256"]
        canonical_path(relative)
        if relative.casefold() in aliases or not isinstance(expected, str) or re.fullmatch(r"[0-9a-f]{64}", expected) is None:
            raise ValueError("Duplicate path or invalid lowercase SHA-256 digest")
        seen.add(relative)
        aliases.add(relative.casefold())
    return seen


def require_same_inventory(expected: set[str], actual: set[str], label: str) -> None:
    if expected != actual:
        missing, unlisted = sorted(expected - actual), sorted(actual - expected)
        raise ValueError(f"{label} inventory differs; missing={missing}; unlisted={unlisted}")


def verify_files(root: Path, entries: list[dict], closed: bool = False) -> int:
    seen = entry_paths(entries)
    if closed:
        require_same_inventory(seen, inventory_files(root) - {"SOURCE-PACKAGE.json"}, "Package")
    for entry in entries:
        if sha(file_at(root, entry["path"])) != entry["sha256"]:
            raise ValueError("Source bytes changed: " + entry["path"])
    return len(seen)


def collect_host_inputs(repository: Path) -> set[str]:
    """Collect the fixed host scope; the manifest cannot narrow this boundary."""
    repository = repository.absolute()
    web_root = plain_path(repository, WEB.as_posix())
    web_paths = inventory_files(web_root)
    selected: set[str] = set()
    for relative in web_paths:
        path = PurePosixPath(relative)
        if (path.suffix.lower() in WEB_SOURCE_SUFFIXES or path.name == ".editorconfig"
                or path.parts[0] == "wwwroot"
                or (len(path.parts) == 1 and path.name in WEB_PUBLISHED_CONFIGS)):
            selected.add((WEB / path).as_posix())

    pcc_root = plain_path(repository, PCC.as_posix())
    if not pcc_root.is_dir():
        raise ValueError("PCC compile input root is not a directory")
    for child in pcc_root.iterdir():
        if child.suffix.lower() == ".cs":
            reject_link(child)
            if not child.is_file():
                raise ValueError("Linked PCC input is not a regular file")
            selected.add((PCC / child.name).as_posix())

    for ancestor in (PurePosixPath("."), *reversed(WEB.parents[:-1]), WEB):
        directory = repository if ancestor.as_posix() == "." else plain_path(repository, ancestor.as_posix())
        for child in directory.iterdir():
            if child.name.casefold() in ANCESTOR_METADATA:
                relative = child.relative_to(repository).as_posix()
                file_at(repository, relative)
                selected.add(relative)
    return selected


def verify_host_inventory(repository: Path, entries: list[dict]) -> int:
    require_same_inventory(entry_paths(entries), collect_host_inputs(repository), "Host")
    return verify_files(repository, entries)


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result: dict = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON key: " + key)
        result[key] = value
    return result


def read_manifest(path: Path) -> dict:
    reject_link(path)
    try:
        value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError("Unreadable source manifest: " + str(path)) from error
    if not isinstance(value, dict):
        raise ValueError("Source manifest must be an object")
    return value


def verify_package(package: Path, repository: Path) -> dict:
    package = package.absolute()
    reject_link(package)
    manifest = read_manifest(package / "SOURCE-PACKAGE.json")
    if (manifest.get("schemaVersion") != "fanasa.hr3.actual-host-source-package/1"
            or manifest.get("operationalAcceptance") is not False
            or manifest.get("liveHostRegistration") is not False
            or manifest.get("runtimeDefault") != "disabled"):
        raise ValueError("Invalid source-only package boundary")
    own = verify_files(package, manifest.get("files"), closed=True)
    declared = manifest.get("dependencies")
    if not isinstance(declared, list) or any(not isinstance(item, dict) for item in declared):
        raise ValueError("Missing immutable dependency inventory")
    identities = [(item.get("directory"), item.get("manifestSha256")) for item in declared]
    if (any(not isinstance(directory, str) or not isinstance(digest, str) for directory, digest in identities)
            or len(identities) != len(DEPENDENCY_MANIFESTS)
            or set(identities) != set(DEPENDENCY_MANIFESTS.items())):
        raise ValueError("Dependency identities differ from immutable profile/PDP pins")
    dependencies = 0
    for directory, expected in identities:
        root = package.parent / directory
        manifest_path = file_at(root, "SOURCE-PACKAGE.json")
        if sha(manifest_path) != expected:
            raise ValueError("Dependency manifest changed: " + directory)
        dependencies += verify_files(root, read_manifest(manifest_path).get("files"), closed=True)
    host = verify_host_inventory(repository.absolute(), manifest.get("hostInputs"))
    return {"packageFiles": own, "dependencyFiles": dependencies, "hostInputs": host, "operationalAcceptance": False}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    default_repository = next((parent for parent in PACKAGE.parents if (parent / WEB).is_dir()), PACKAGE.parents[2])
    parser.add_argument("--repository", type=Path, default=default_repository)
    parser.add_argument("--print-host-inventory", action="store_true", help="Print current hashes for an unsealed candidate; does not create or update a manifest")
    args = parser.parse_args()
    if args.print_host_inventory:
        paths = collect_host_inputs(args.repository.absolute())
        print(json.dumps({
            "kind": "unsealed-host-inventory-candidate",
            "operationalAcceptance": False,
            "scope": HOST_INPUT_SCOPE,
            "count": len(paths),
            "hostInputs": [{"path": path, "sha256": sha(file_at(args.repository, path))} for path in sorted(paths)],
        }, indent=2))
        return
    result = verify_package(PACKAGE, args.repository)
    print(f"PASS: {result['packageFiles']} host-package files, {result['dependencyFiles']} immutable dependency files, {result['hostInputs']} closed actual-host inputs. No live acceptance.")


if __name__ == "__main__":
    main()
