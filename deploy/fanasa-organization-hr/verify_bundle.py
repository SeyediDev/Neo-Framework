#!/usr/bin/env python3
"""Validate the entire HR release archive before bounded trusted deployment or extraction."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import sys
import tarfile

MAX_ARCHIVE = 512 * 1024 * 1024
MAX_EXPANDED = 1024 * 1024 * 1024
MAX_FILES = 20000
MAX_JSON = 4 * 1024 * 1024
TOP_FILES = {"release-manifest.json", "ci-verification.json"}
TOP_DIRECTORIES = {"web", "workbench", "evidence"}
MIN_ORGANIZATION_UI_TESTS = 44
SUITES = {"tests-Organization.Hr.Tests", "tests-Organization.Bearer.Tests", "tests-Organization.Tests",
          "tests-Organization.Calendar.Tests", "tests-Organization.Temporal.Tests",
          "tests-ConsumerApplicationCatalog.Tests", "tests-PublishedCatalog.Tests", "tests-ConsumerUsage.Api.Tests",
          "tests-PaygMetering.Tests", "tests-BillingLifecycle.Tests", "tests-Billing.Api.Tests",
          "tests-Workbench.Boundary.Tests", "tests-ProductRegistration.Tests", "tests-native-workbench",
          "tests-organization-temporal-ui"}
REPOSITORIES = {"https://github.com/SeyediDev/Neo-Framework.git",
                "https://gitlab.fanasa.net.local/javadsayedi/organization-hr-api.git"}


class BoundedTarInfo(tarfile.TarInfo):
    # Python consumes PAX/GNU metadata before yielding a file header. Bound it before allocation.
    def _bound_metadata(self, archive):
        if self.size < 0 or self.size > MAX_JSON:
            raise ValueError("TAR extended metadata exceeds its bounded size.")
        archive._hr_metadata_bytes = getattr(archive, "_hr_metadata_bytes", 0) + self.size
        archive._hr_metadata_count = getattr(archive, "_hr_metadata_count", 0) + 1
        if archive._hr_metadata_bytes > 4 * MAX_JSON or archive._hr_metadata_count > MAX_FILES:
            raise ValueError("Aggregate TAR metadata exceeds its bounded size.")

    def _proc_pax(self, archive):
        self._bound_metadata(archive)
        return super()._proc_pax(archive)

    def _proc_gnulong(self, archive):
        self._bound_metadata(archive)
        return super()._proc_gnulong(archive)


def digest(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def object_pairs(items: list[tuple]) -> dict:
    result = {}
    for key, value in items:
        if key in result:
            raise ValueError("Duplicate JSON field in release evidence.")
        result[key] = value
    return result


def read_json(data: bytes) -> dict:
    result = json.loads(data.decode("utf-8-sig"), object_pairs_hook=object_pairs)
    if not isinstance(result, dict):
        raise ValueError("A JSON object is required.")
    return result


def safe_name(name: str) -> str:
    path = PurePosixPath(name)
    if (not name or "\\" in name or ":" in name or any(ord(char) < 32 for char in name)
            or path.is_absolute() or name != path.as_posix() or any(part in {".", "..", ""} for part in name.split("/"))
            or path.parts[0] not in TOP_DIRECTORIES and name not in TOP_FILES):
        raise ValueError("Archive path is outside the allowed release layout.")
    if path.suffix.lower() in {".key", ".pem", ".pfx", ".p12", ".env", ".db", ".sqlite"} or any(
            part in {"App_Data", "data", ".users", ".vps", "keys"} for part in path.parts):
        raise ValueError("Credentials or persistent runtime data cannot be a release artifact.")
    if path.name.casefold() != "appsettings.json" and path.name.casefold().startswith("appsettings.") and path.suffix.lower() == ".json":
        raise ValueError("Environment-specific settings cannot be a release artifact.")
    return name


def verify(archive: Path, expected_hash: str | None, expected_release: str | None,
           receipt: Path | None = None, extract_to: Path | None = None) -> dict:
    if not archive.is_file() or archive.is_symlink() or archive.stat().st_size > MAX_ARCHIVE:
        raise ValueError("Release archive is absent, linked or exceeds the size limit.")
    actual_hash = digest(archive)
    receipt_value = read_json(receipt.read_bytes()) if receipt else None
    if receipt_value:
        if receipt_value.get("archive") != archive.name:
            raise ValueError("Receipt names a different release archive.")
        if expected_hash and receipt_value.get("sha256") != expected_hash:
            raise ValueError("Receipt and trusted archive hash disagree.")
        expected_hash = receipt_value.get("sha256")
        if expected_release and receipt_value.get("release") != expected_release:
            raise ValueError("Receipt and expected release disagree.")
        expected_release = receipt_value.get("release")
    if not expected_hash or not re.fullmatch(r"[a-f0-9]{64}", expected_hash) or actual_hash != expected_hash:
        raise ValueError("Exact trusted archive SHA256 is required and must match.")
    with tarfile.open(archive, "r:gz", tarinfo=BoundedTarInfo) as bundle:
        indexed = {}
        expanded = 0
        # Stop as each header arrives; getmembers() would allocate an unbounded header list first.
        for item in bundle:
            expanded += item.size
            if len(indexed) >= MAX_FILES or expanded > MAX_EXPANDED:
                raise ValueError("Archive expansion exceeds bounded release limits.")
            name = safe_name(item.name)
            if name in indexed or not item.isfile() or item.size < 0 or item.mode & 0o7000:
                raise ValueError("Duplicate, linked, special or privileged archive entry.")
            if name in TOP_FILES and item.size > MAX_JSON:
                raise ValueError("Release/CI JSON evidence exceeds its bounded size.")
            indexed[name] = item
        if not TOP_FILES.issubset(indexed):
            raise ValueError("Release and CI manifests are required.")
        manifest_data = bundle.extractfile(indexed["release-manifest.json"]).read()
        manifest = read_json(manifest_data)
        release = manifest.get("release")
        if (manifest.get("schemaVersion") != "fanasa.organization-hr.release.v1"
                or not isinstance(release, str) or not re.fullmatch(r"hr-development-[a-f0-9]{12}-[a-f0-9]{12}", release)
                or expected_release != release or manifest.get("environment") != "development"
                or manifest.get("component") != "organization-hr" or manifest.get("framework") != "net10.0"
                or manifest.get("webService") != "fanasa-access-management@development.service"
                or manifest.get("workbenchService") != "fanasa-policy-workbench.service"
                or manifest.get("workbenchStorage") != "SQLite" or manifest.get("workbenchDatabaseSchema") != 1
                or manifest.get("workbenchRuntimeIntegrity") is not True
                or manifest.get("runtimeSecretsInArtifact") is not False
                or manifest.get("cdMode") != "bounded-systemd-development"
                or manifest.get("databaseMigration") != "none"):
            raise ValueError("Manifest does not describe the bounded HR development release contract.")
        if receipt_value and hashlib.sha256(manifest_data).hexdigest() != receipt_value.get("manifestSha256"):
            raise ValueError("Release manifest differs from the authoritative receipt.")
        file_hashes = {}
        for item in manifest.get("files", []):
            name = safe_name(item["path"])
            if name in file_hashes or name not in indexed or name == "release-manifest.json":
                raise ValueError("Manifest contains duplicate, missing or self-referential file entries.")
            if item.get("size") != indexed[name].size or not re.fullmatch(r"[a-f0-9]{64}", item["sha256"]):
                raise ValueError("Invalid manifested file size or hash.")
            stream = bundle.extractfile(indexed[name])
            value = hashlib.sha256()
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                value.update(chunk)
            if value.hexdigest() != item["sha256"]:
                raise ValueError("Manifested file SHA256 mismatch.")
            file_hashes[name] = item["sha256"]
        if set(indexed) != set(file_hashes) | {"release-manifest.json"}:
            raise ValueError("Archive contains unmanifested content.")
        required = {"web/Fanasa.AccessManagement.Web.dll", "web/Fanasa.ContractPolicy.dll", "web/Workbench/index.html",
                    "web/wwwroot/workbench/workbench.js", "workbench/Fanasa.ContractPolicy.Workbench.dll",
                    "workbench/Fanasa.ContractPolicy.dll", "workbench/runtimes/linux-x64/native/libe_sqlite3.so"}
        if not required.issubset(indexed) or "web/wwwroot/workbench/index.html" in indexed:
            raise ValueError("Required engine, SQLite runtime or protected UI shell is missing/unsafe.")
        if file_hashes["web/Fanasa.ContractPolicy.dll"] != file_hashes["workbench/Fanasa.ContractPolicy.dll"]:
            raise ValueError("Web and workbench policy engine binaries differ.")
        if len([name for name in indexed if name.startswith("workbench/Schemas/") and name.endswith(".schema.json")]) != 10:
            raise ValueError("All ten policy schemas are required.")
        ci_data = bundle.extractfile(indexed["ci-verification.json"]).read()
        ci = read_json(ci_data)
        if (hashlib.sha256(ci_data).hexdigest() != manifest.get("verificationSha256")
                or ci.get("schemaVersion") != "fanasa.organization-hr.ci.v1" or ci.get("status") != "passed"
                or ci.get("buildIsolation") != "verified-source-copy"
                or ci.get("source") != manifest.get("source") or ci.get("environment") != "development"):
            raise ValueError("Successful CI evidence for this exact source is required.")
        gates = {}
        gate_list = ci.get("gates", [])
        if not isinstance(gate_list, list) or any(not isinstance(g, dict) for g in gate_list):
            raise ValueError("Invalid CI gate collection.")
        for gate in gate_list:
            if not isinstance(gate.get("name"), str) or not isinstance(gate.get("log"), str):
                raise ValueError("Invalid CI gate identity.")
            name = gate["name"]
            log = f"evidence/{gate['log']}"
            log_hash = gate.get("logSha256")
            if (name in gates or type(gate.get("exitCode")) is not int or gate["exitCode"] != 0 or log not in file_hashes
                    or not isinstance(log_hash, str) or not re.fullmatch(r"[a-f0-9]{64}", log_hash)
                    or file_hashes[log] != log_hash):
                raise ValueError("Invalid, duplicate or changed CI gate evidence.")
            gates[name] = gate
        executable_suites = SUITES - {"tests-native-workbench", "tests-organization-temporal-ui"}
        if not SUITES.issubset(gates) or any(type(gates[name].get("passedChecks")) is not int or gates[name]["passedChecks"] < 1 for name in executable_suites):
            raise ValueError("All required HR/organization/calendar/temporal/bearer/boundary/product-registration/financial suites must have passing checks.")
        native_counts = [gates["tests-native-workbench"].get(field) for field in ("passedTests", "failedTests", "skippedTests")]
        if any(type(count) is not int for count in native_counts) or native_counts[0] < 15 or native_counts[1] != 0 or native_counts[2] != 0:
            raise ValueError("Native workbench receipt requires at least 15 passing tests and zero failures or skips.")
        temporal_ui = gates["tests-organization-temporal-ui"]
        temporal_counts = [temporal_ui.get(field) for field in ("passedTests", "failedTests", "skippedTests")]
        if (any(type(count) is not int for count in temporal_counts) or temporal_counts[0] < MIN_ORGANIZATION_UI_TESTS
                or temporal_counts[1] != 0 or temporal_counts[2] != 0):
            raise ValueError(f"Organization temporal UI receipt requires at least {MIN_ORGANIZATION_UI_TESTS} passing tests and zero failures or skips.")
        source = ci["source"]
        if (source.get("repository") not in REPOSITORIES
                or not re.fullmatch(r"[a-f0-9]{40}", source.get("gitBaseCommit", ""))
                or source.get("kind") not in {"git-commit", "working-tree-snapshot"}
                or source.get("dirty") != (source.get("kind") == "working-tree-snapshot")):
            raise ValueError("Invalid exact source identity.")
        encoded = json.dumps(source["files"], sort_keys=True, separators=(",", ":")).encode()
        if hashlib.sha256(encoded).hexdigest() != source.get("snapshotSha256"):
            raise ValueError("Source snapshot evidence changed.")
        if release != f"hr-development-{source['gitBaseCommit'][:12]}-{source['snapshotSha256'][:12]}":
            raise ValueError("Release identity is not bound to the source snapshot.")
        if receipt_value and (receipt_value.get("sourceCommit") != source["gitBaseCommit"]
                              or receipt_value.get("sourceKind") != source["kind"]
                              or receipt_value.get("sourceSnapshotSha256") != source["snapshotSha256"]):
            raise ValueError("Receipt source differs from the verified source.")
        if extract_to is not None:
            if extract_to.exists():
                raise ValueError("Extraction requires a fresh destination; no release can be overwritten.")
            extract_to.mkdir(parents=True, mode=0o755)
            # Entries are manually copied after verification. No tar extraction, links or modes are trusted.
            for name, item in indexed.items():
                destination = extract_to / name
                destination.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
                with bundle.extractfile(item) as incoming, destination.open("xb") as outgoing:
                    for chunk in iter(lambda: incoming.read(1024 * 1024), b""):
                        outgoing.write(chunk)
                destination.chmod(0o644)
    return {"release": release, "sha256": actual_hash, "sourceCommit": source["gitBaseCommit"],
            "sourceKind": source["kind"], "sourceSnapshotSha256": source["snapshotSha256"],
            "filesVerified": len(file_hashes), "requiredSuites": len(SUITES), "status": "verified"}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--receipt", type=Path)
    parser.add_argument("--expected-hash")
    parser.add_argument("--expected-release")
    parser.add_argument("--extract-to", type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.archive, args.expected_hash, args.expected_release, args.receipt, args.extract_to)))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, tarfile.TarError) as error:
        print(f"HR release verification refused: {error}", file=sys.stderr)
        sys.exit(1)
