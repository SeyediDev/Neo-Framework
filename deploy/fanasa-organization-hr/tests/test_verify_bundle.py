"""Archive security tests use synthetic bytes; they do not build or deploy the product."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("hr_bundle", Path(__file__).parents[1] / "verify_bundle.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

ENVIRONMENT_SETTINGS = (
    "web/appsettings.Development.json", "web/appsettings.Production.json",
    "workbench/appsettings.local.json", "web/AppSettings.Staging.json",
    "web/src/APPSETTINGS.local.json", "workbench/AppSettings.Development.JSON",
)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def encoded(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode()


def fixture(directory, extra=None, omit_suite=None, bad_log=False, temporal_ui_counts=None,
            missing_log=False, omit_log_hash=False, native_counts=None, gate_overrides=None,
            manifested_files=None, isolation_marker="verified-source-copy"):
    files = {"web/Fanasa.AccessManagement.Web.dll": b"synthetic host",
             "web/Fanasa.ContractPolicy.dll": b"synthetic common engine",
             "web/Workbench/index.html": b"protected synthetic shell",
             "web/wwwroot/workbench/workbench.js": b"synthetic ui",
             "workbench/Fanasa.ContractPolicy.Workbench.dll": b"synthetic workbench",
             "workbench/Fanasa.ContractPolicy.dll": b"synthetic common engine",
             "workbench/runtimes/linux-x64/native/libe_sqlite3.so": b"synthetic sqlite native"}
    files.update(manifested_files or {})
    for index in range(10):
        files[f"workbench/Schemas/{index}.schema.json"] = b"{}"
    source_files = [{"path": "synthetic/Program.cs", "sha256": sha(b"synthetic source")}]
    source = {"repository": "https://github.com/SeyediDev/Neo-Framework.git", "gitBaseCommit": "a" * 40,
              "kind": "git-commit", "dirty": False, "files": source_files,
              "snapshotSha256": sha(json.dumps(source_files, sort_keys=True, separators=(",", ":")).encode())}
    gates = []
    for suite in sorted(MODULE.SUITES - {omit_suite}):
        log = suite + ".log"
        content = b"PASS synthetic check\n"
        files["evidence/" + log] = content
        gate = {"name": suite, "log": log, "exitCode": 0, "logSha256": sha(content)}
        if suite == "tests-native-workbench":
            gate.update({"passedTests": 15, "failedTests": 0, "skippedTests": 0}
                        if native_counts is None else native_counts)
        elif suite == "tests-organization-temporal-ui":
            gate.update({"passedTests": 44, "failedTests": 0, "skippedTests": 0}
                        if temporal_ui_counts is None else temporal_ui_counts)
            if missing_log:
                gate["log"] = "missing.log"
            if omit_log_hash:
                gate.pop("logSha256")
        else:
            gate["passedChecks"] = 1
        gate.update((gate_overrides or {}).get(suite, {}))
        gates.append(gate)
    ci = {"schemaVersion": "fanasa.organization-hr.ci.v1", "environment": "development",
          "status": "passed", "source": source, "gates": gates}
    if isolation_marker is not None:
        ci["buildIsolation"] = isolation_marker
    files["ci-verification.json"] = encoded(ci)
    if bad_log:
        files["evidence/" + gates[0]["log"]] = b"modified evidence\n"
    release = f"hr-development-{source['gitBaseCommit'][:12]}-{source['snapshotSha256'][:12]}"
    manifest = {"schemaVersion": "fanasa.organization-hr.release.v1", "release": release,
                "environment": "development", "component": "organization-hr", "framework": "net10.0",
                "webService": "fanasa-access-management@development.service",
                "workbenchService": "fanasa-policy-workbench.service", "workbenchStorage": "SQLite",
                "workbenchDatabaseSchema": 1, "workbenchRuntimeIntegrity": True,
                "runtimeSecretsInArtifact": False, "cdMode": "bounded-systemd-development",
                "databaseMigration": "none", "verificationSha256": sha(files["ci-verification.json"]),
                "source": source, "files": [{"path": name, "sha256": sha(data), "size": len(data)}
                                             for name, data in files.items()]}
    files["release-manifest.json"] = encoded(manifest)
    archive = directory / (release + ".tar.gz")
    with tarfile.open(archive, "w:gz") as bundle:
        for name, data in files.items():
            member = tarfile.TarInfo(name)
            member.size, member.mode = len(data), 0o644
            bundle.addfile(member, io.BytesIO(data))
        if extra:
            name, kind = extra
            member = tarfile.TarInfo(name)
            member.mode = 0o644
            if kind == "symlink":
                member.type, member.linkname = tarfile.SYMTYPE, "/etc/passwd"
                bundle.addfile(member)
            else:
                member.size = 1
                bundle.addfile(member, io.BytesIO(b"x"))
    receipt = directory / "release.json"
    receipt.write_bytes(encoded({"release": release, "archive": archive.name, "sha256": MODULE.digest(archive),
                                 "manifestSha256": sha(files["release-manifest.json"]),
                                 "sourceCommit": source["gitBaseCommit"], "sourceKind": source["kind"],
                                 "sourceSnapshotSha256": source["snapshotSha256"]}))
    return archive, receipt


class ArchiveTests(unittest.TestCase):
    def test_environment_settings_are_rejected_before_extraction(self):
        self.assertEqual(MODULE.safe_name("web/appsettings.json"), "web/appsettings.json")
        for name in ENVIRONMENT_SETTINGS:
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "Environment-specific"):
                MODULE.safe_name(name)

    def test_rejects_manifested_environment_settings_with_mixed_case_before_extraction(self):
        # Valid hashes and a trusted receipt must not hide forbidden configuration bytes.
        for name in ENVIRONMENT_SETTINGS:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                archive, receipt = fixture(
                    root, manifested_files={name: b'{"SyntheticIgnoredLocalSetting":true}\n'}
                )
                target = root / "extracted"
                with self.assertRaisesRegex(ValueError, "Environment-specific"):
                    MODULE.verify(archive, None, None, receipt, target)
                self.assertFalse(target.exists())

    def test_rejects_cumulative_extended_metadata_before_normal_member(self):
        with tempfile.TemporaryDirectory() as temp:
            archive = Path(temp) / "metadata-sequence.tar.gz"
            record = b"22 comment=hr-padding\n"
            payload = record * (MODULE.MAX_JSON // len(record))
            with tarfile.open(archive, "w:gz") as bundle:
                for index in range(5):
                    member = tarfile.TarInfo("metadata-" + str(index))
                    member.type = tarfile.XHDTYPE
                    member.size = len(payload)
                    bundle.addfile(member, io.BytesIO(payload))
            with self.assertRaisesRegex(ValueError, "Aggregate TAR metadata"):
                MODULE.verify(archive, sha(archive.read_bytes()), "hr-development-" + "a" * 12 + "-" + "b" * 12)

    def test_rejects_oversized_extended_metadata_before_consuming_payload(self):
        for kind in (tarfile.XHDTYPE, tarfile.GNUTYPE_LONGNAME):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temp:
                archive = Path(temp) / "metadata.tar.gz"
                with tarfile.open(archive, "w:gz") as bundle:
                    member = tarfile.TarInfo("metadata")
                    member.type = kind
                    member.size = MODULE.MAX_JSON + 1
                    bundle.addfile(member, io.BytesIO(b"0" * member.size))
                with self.assertRaisesRegex(ValueError, "metadata exceeds"):
                    MODULE.verify(archive, sha(archive.read_bytes()), "hr-development-" + "a" * 12 + "-" + "b" * 12)

    def test_valid_archive_extracts_only_verified_files_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, receipt = fixture(root)
            target = root / "extracted"
            result = MODULE.verify(archive, None, None, receipt, target)
            self.assertEqual(result["status"], "verified")
            self.assertEqual((target / "web/Fanasa.AccessManagement.Web.dll").read_bytes(), b"synthetic host")
            with self.assertRaises(ValueError):
                MODULE.verify(archive, None, None, receipt, target)

    def test_rejects_missing_or_wrong_build_isolation_before_extraction(self):
        for marker in (None, "repository-worktree", "", False):
            with self.subTest(marker=marker), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                archive, receipt = fixture(root, isolation_marker=marker)
                target = root / "extracted"
                with self.assertRaisesRegex(ValueError, "Successful CI evidence"):
                    MODULE.verify(archive, None, None, receipt, target)
                self.assertFalse(target.exists())

    def test_rejects_traversal_symlink_unlisted_and_duplicate_entries(self):
        for entry in [("../escaped", "file"), ("web/linked", "symlink"), ("web/extra", "file"),
                      ("web/Fanasa.AccessManagement.Web.dll", "file")]:
            with self.subTest(entry=entry), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                archive, receipt = fixture(root, extra=entry)
                with self.assertRaises(ValueError):
                    MODULE.verify(archive, None, None, receipt, root / "extracted")
                self.assertFalse((root / "extracted").exists())

    def test_rejects_missing_required_suite_and_changed_log(self):
        new_suites = {"tests-Organization.Calendar.Tests", "tests-Organization.Temporal.Tests", "tests-organization-temporal-ui"}
        for args in [{"omit_suite": suite} for suite in sorted(MODULE.SUITES | new_suites)] + [{"bad_log": True}]:
            with self.subTest(args=args), tempfile.TemporaryDirectory() as temp:
                archive, receipt = fixture(Path(temp), **args)
                with self.assertRaises(ValueError):
                    MODULE.verify(archive, None, None, receipt)

    def test_rejects_temporal_ui_receipt_below_floor_failed_skipped_or_missing_counts(self):
        valid = {"passedTests": 44, "failedTests": 0, "skippedTests": 0}
        invalid = [dict(valid, passedTests=count) for count in [0, 20, 21, 30, 31, 43, 43.5, 44.0, True, False, "44", None]]
        invalid += [dict(valid, failedTests=1), dict(valid, skippedTests=1),
                    dict(valid, failedTests=False), dict(valid, skippedTests="0")]
        invalid += [{name: value for name, value in valid.items() if name != missing} for missing in valid]
        for counts in invalid:
            with self.subTest(counts=counts), tempfile.TemporaryDirectory() as temp:
                archive, receipt = fixture(Path(temp), temporal_ui_counts=counts)
                with self.assertRaisesRegex(ValueError, "temporal UI receipt"):
                    MODULE.verify(archive, None, None, receipt)

    def test_organization_and_pip_ui_receipt_accepts_44_or_more_passes(self):
        for count in [44, 45]:
            with self.subTest(count=count), tempfile.TemporaryDirectory() as temp:
                counts = {"passedTests": count, "failedTests": 0, "skippedTests": 0}
                archive, receipt = fixture(Path(temp), temporal_ui_counts=counts)
                self.assertEqual(MODULE.verify(archive, None, None, receipt)["status"], "verified")

    def test_rejects_noninteger_or_boolean_gate_exit_code(self):
        suite = "tests-Organization.Calendar.Tests"
        for exit_code in (False, True, 0.0, "0", None):
            with self.subTest(exit_code=exit_code), tempfile.TemporaryDirectory() as temp:
                archive, receipt = fixture(Path(temp), gate_overrides={suite: {"exitCode": exit_code}})
                with self.assertRaisesRegex(ValueError, "CI gate evidence"):
                    MODULE.verify(archive, None, None, receipt)

    def test_rejects_noninteger_or_boolean_executable_pass_count(self):
        for suite in ("tests-Organization.Calendar.Tests", "tests-Organization.Temporal.Tests"):
            for count in (True, False, 1.0, "1", None):
                with self.subTest(suite=suite, count=count), tempfile.TemporaryDirectory() as temp:
                    archive, receipt = fixture(Path(temp), gate_overrides={suite: {"passedChecks": count}})
                    with self.assertRaisesRegex(ValueError, "passing checks"):
                        MODULE.verify(archive, None, None, receipt)

    def test_rejects_native_receipt_with_invalid_failed_skipped_or_missing_counts(self):
        valid = {"passedTests": 15, "failedTests": 0, "skippedTests": 0}
        invalid = [dict(valid, passedTests=14), dict(valid, passedTests=15.5),
                   dict(valid, passedTests=True), dict(valid, passedTests="15"),
                   dict(valid, failedTests=1), dict(valid, skippedTests=1),
                   dict(valid, failedTests=False), dict(valid, skippedTests=False),
                   dict(valid, failedTests=0.0), dict(valid, skippedTests=0.0)]
        invalid += [{name: value for name, value in valid.items() if name != missing} for missing in valid]
        for counts in invalid:
            with self.subTest(counts=counts), tempfile.TemporaryDirectory() as temp:
                archive, receipt = fixture(Path(temp), native_counts=counts)
                with self.assertRaisesRegex(ValueError, "Native workbench receipt"):
                    MODULE.verify(archive, None, None, receipt)

    def test_rejects_unbound_gate_log_receipts(self):
        for fields in ({"missing_log": True}, {"omit_log_hash": True},
                       {"missing_log": True, "omit_log_hash": True}):
            with self.subTest(fields=fields), tempfile.TemporaryDirectory() as temp:
                archive, receipt = fixture(Path(temp), **fields)
                with self.assertRaisesRegex(ValueError, "CI gate evidence"):
                    MODULE.verify(archive, None, None, receipt)

    def test_rejects_untrusted_or_mismatched_archive_hash(self):
        with tempfile.TemporaryDirectory() as temp:
            archive, _ = fixture(Path(temp))
            with self.assertRaises(ValueError):
                MODULE.verify(archive, None, None)
            with self.assertRaises(ValueError):
                MODULE.verify(archive, "b" * 64, "hr-development-aaaaaaaaaaaa-bbbbbbbbbbbb")


if __name__ == "__main__":
    unittest.main()
