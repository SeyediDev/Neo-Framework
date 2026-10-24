"""Synthetic regression checks for isolated build sources and publication preflight."""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("hr_ci_workspace", Path(__file__).parents[1] / "ci.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
SHA = "a" * 40
WEB = "products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web"


def digest(data):
    return hashlib.sha256(data).hexdigest()


class WorkspaceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="hr-ci-workspace-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.repository = self.base / "repository"
        self.output = self.base / "output"
        self.contents = {
            "Directory.Build.props": b"<Project />\n",
            f"{WEB}/Program.cs": b"// synthetic pinned source\n",
            f"{WEB}/appsettings.json": b'{"synthetic":"base"}\n',
            "deploy/fanasa-organization-hr/hr-read-host-v3-r3/appsettings.hr3.disabled.json": b'{"Enabled":false}\n',
        }
        for relative, data in self.contents.items():
            self.write(self.repository, relative, data)
        self.source = {
            "repository": "https://gitlab.fanasa.net.local/javadsayedi/organization-hr-api.git",
            "gitBaseCommit": SHA, "dirty": False, "kind": "git-commit",
            "files": [{"path": name, "sha256": digest(data)} for name, data in sorted(self.contents.items())],
        }
        encoded = json.dumps(self.source["files"], sort_keys=True, separators=(",", ":")).encode()
        self.source["snapshotSha256"] = digest(encoded)

    def write(self, root, relative, data):
        target = root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        return target

    def prepare(self, output=None):
        with patch.object(MODULE, "ROOT", self.repository):
            return MODULE.prepare_workspace(output or self.output, self.source)

    def evidence(self):
        gates = []
        for name in sorted(MODULE.REQUIRED_TEST_GATES):
            data = f"synthetic passing receipt: {name}\n".encode()
            log = self.write(self.output, name + ".log", data)
            gate = {"name": name, "log": log.name, "logSha256": digest(data), "exitCode": 0}
            if name in {"tests-native-workbench", "tests-organization-temporal-ui"}:
                gate.update(passedTests=15 if name == "tests-native-workbench" else 44, failedTests=0, skippedTests=0)
            else:
                gate["passedChecks"] = 1
            gates.append(gate)
        return {"status": "passed", "source": copy.deepcopy(self.source),
                "buildIsolation": "verified-source-copy", "gates": gates}

    def test_exact_copy_excludes_local_configs_and_old_repository_outputs_without_reading_them(self):
        excluded = [
            self.write(self.repository, f"{WEB}/{name}", b"synthetic local data; never read\n")
            for name in ["appsettings.Development.json", "APPSETTINGS.Local.json", "bin/old.dll", "obj/project.assets.json"]
        ]
        original_sha, original_copy = MODULE.sha256, MODULE.shutil.copyfile
        def sha(path):
            self.assertNotIn(path, excluded)
            return original_sha(path)
        def copy_file(source, target, *args, **kwargs):
            self.assertNotIn(source, excluded)
            return original_copy(source, target, *args, **kwargs)
        with patch.object(MODULE, "sha256", side_effect=sha), patch.object(MODULE.shutil, "copyfile", side_effect=copy_file):
            workspace = self.prepare()
        actual = {path.relative_to(workspace).as_posix() for path in workspace.rglob("*") if path.is_file()}
        self.assertEqual(set(self.contents), actual)
        for relative, data in self.contents.items():
            self.assertEqual(data, (workspace / relative).read_bytes())

    def test_stale_workspace_directory_or_file_is_rejected(self):
        for kind in ["directory", "file"]:
            with self.subTest(kind=kind):
                output = self.base / kind
                stale = output / "build-source"
                stale.parent.mkdir()
                stale.mkdir() if kind == "directory" else stale.write_bytes(b"stale")
                with self.assertRaises(RuntimeError):
                    self.prepare(output)

    def test_noncanonical_source_paths_are_rejected(self):
        for relative in ["", ".", "../outside.cs", "/absolute.cs", "C:/absolute.cs", "a\\b.cs",
                         "a//b.cs", "./a.cs", "a/../b.cs", "a./b.cs", "a/b.cs ", "a/b.cs/"]:
            with self.subTest(path=relative), self.assertRaises(RuntimeError):
                MODULE.source_file(self.repository, relative)

    def test_source_change_before_copy_is_rejected(self):
        self.write(self.repository, f"{WEB}/Program.cs", b"changed before copy\n")
        with self.assertRaises(RuntimeError):
            self.prepare()

    def test_workspace_edits_deletions_and_extra_source_or_local_settings_are_rejected(self):
        for change in ["edit", "delete", "extra-source", "extra-settings"]:
            with self.subTest(change=change):
                workspace = self.prepare(self.base / change)
                source = workspace / WEB / "Program.cs"
                if change == "edit":
                    source.write_bytes(b"changed after build\n")
                elif change == "delete":
                    source.unlink()
                else:
                    self.write(workspace, f"{WEB}/" + ("Injected.cs" if change == "extra-source" else "APPSETTINGS.Local.json"), b"unlisted")
                with self.assertRaises(RuntimeError):
                    MODULE.validate_workspace(workspace, self.source)

    def test_generated_outputs_are_allowed_without_widening_the_source_inventory(self):
        workspace = self.prepare()
        for relative in [f"{WEB}/bin/Release/host.dll", f"{WEB}/obj/Generated.cs", ".runs/fixture.db", "__pycache__/cache.pyc"]:
            self.write(workspace, relative, b"synthetic generated output")
        MODULE.validate_workspace(workspace, self.source)

    def test_linked_source_parent_is_rejected_before_copy(self):
        linked_parent = self.repository / "products"
        original = Path.is_symlink
        with patch.object(Path, "is_symlink", autospec=True, side_effect=lambda path: path == linked_parent or original(path)):
            with self.assertRaises(RuntimeError):
                self.prepare()

    def test_duplicate_source_entries_are_rejected(self):
        workspace = self.prepare()
        source = copy.deepcopy(self.source)
        source["files"].append(copy.deepcopy(source["files"][0]))
        with self.assertRaises(RuntimeError):
            MODULE.validate_workspace(workspace, source)

    def test_complete_hash_bound_gate_receipts_pass(self):
        MODULE.validate_test_evidence(self.evidence(), self.output, self.source)

    def test_organization_and_pip_ui_receipt_requires_44_integer_passes(self):
        for count in [0, 20, 21, 30, 31, 43, 43.5, 44.0, True, False, "44", None]:
            evidence = self.evidence()
            next(g for g in evidence["gates"] if g["name"] == "tests-organization-temporal-ui")["passedTests"] = count
            with self.subTest(count=count), self.assertRaisesRegex(RuntimeError, "lacks passing checks"):
                MODULE.validate_test_evidence(evidence, self.output, self.source)
        for count in [44, 45]:
            evidence = self.evidence()
            next(g for g in evidence["gates"] if g["name"] == "tests-organization-temporal-ui")["passedTests"] = count
            MODULE.validate_test_evidence(evidence, self.output, self.source)

    def test_every_required_gate_and_the_isolation_receipt_are_mandatory(self):
        evidence = self.evidence()
        financial_gates = {"tests-PaygMetering.Tests", "tests-BillingLifecycle.Tests", "tests-Billing.Api.Tests"}
        for name in sorted(MODULE.REQUIRED_TEST_GATES | financial_gates):
            changed = copy.deepcopy(evidence)
            changed["gates"] = [gate for gate in changed["gates"] if gate["name"] != name]
            with self.subTest(missing=name), self.assertRaises(RuntimeError):
                MODULE.validate_test_evidence(changed, self.output, self.source)
        for field, value in [("buildIsolation", None), ("status", "failed"), ("source", {})]:
            changed = copy.deepcopy(evidence)
            changed[field] = value
            with self.subTest(field=field), self.assertRaises(RuntimeError):
                MODULE.validate_test_evidence(changed, self.output, self.source)
        evidence["gates"].append(copy.deepcopy(evidence["gates"][0]))
        with self.assertRaises(RuntimeError):
            MODULE.validate_test_evidence(evidence, self.output, self.source)

    def test_financial_suites_restore_build_and_execute_the_built_assembly(self):
        # Compile/run processes are synthetic; exercise the actual orchestration up to
        # the first engine step without manufacturing a successful release receipt.
        for suite in MODULE.SUITES:
            for filename, data in [(suite + ".csproj", b"<Project />\n"), ("Program.cs", b"// synthetic test\n")]:
                relative = f"products/Fanasa.AccessManagement/tests/{suite}/{filename}"
                self.write(self.repository, relative, data)
                self.source["files"].append({"path": relative, "sha256": digest(data)})
        self.source["files"].sort(key=lambda item: item["path"])
        encoded = json.dumps(self.source["files"], sort_keys=True, separators=(",", ":")).encode()
        self.source["snapshotSha256"] = digest(encoded)
        calls = []
        def run(command, output, name, **kwargs):
            if name == "restore-" + MODULE.ENGINE.stem:
                raise RuntimeError("stop-after-executable-suites")
            calls.append((name, command))
            if command[:2] == ["dotnet", "build"]:
                project = Path(command[2])
                self.write(kwargs["cwd"], project.parent / "bin/Release/net10.0" / (project.stem + ".dll"), b"synthetic assembly")
            content = b"PASS synthetic executed check\n"
            log = self.write(output, name + ".log", content)
            return {"name": name, "log": log.name, "logSha256": digest(content), "exitCode": 0}
        args = argparse.Namespace(output=self.output, source_commit=SHA, allow_working_tree=False, cached_restore=False)
        with patch.object(MODULE, "ROOT", self.repository), patch.object(MODULE, "source_identity", return_value=self.source), \
                patch.object(MODULE, "capture", side_effect=lambda command: "10.0.401" if command[0] == "dotnet" else "v24.0.0"), \
                patch.object(MODULE, "run", side_effect=run):
            with self.assertRaisesRegex(RuntimeError, "stop-after-executable-suites"):
                MODULE.verify(args)
        evidence = json.loads((self.output / "verification.json").read_text(encoding="utf-8"))
        self.assertEqual(evidence["status"], "failed")
        names = [name for name, _ in calls]
        for suite in ["PaygMetering.Tests", "BillingLifecycle.Tests", "Billing.Api.Tests"]:
            with self.subTest(suite=suite):
                restore, build, execute = [names.index(prefix + suite) for prefix in ["restore-", "build-", "tests-"]]
                self.assertLess(restore, build)
                self.assertLess(build, execute)
                assembly = Path("products/Fanasa.AccessManagement/tests") / suite / "bin/Release/net10.0" / (suite + ".dll")
                self.assertEqual(calls[execute][1], ["dotnet", str(assembly)])
                gate = next(gate for gate in evidence["gates"] if gate["name"] == "tests-" + suite)
                self.assertEqual(gate["passedChecks"], 1)

    def test_gate_logs_and_integer_counters_cannot_be_relaxed(self):
        evidence = self.evidence()
        mutations = [("log", "../outside.log"), ("log", "missing.log"), ("logSha256", "a" * 64),
                     ("exitCode", False), ("exitCode", 0.0), ("passedChecks", True)]
        for field, value in mutations:
            changed = copy.deepcopy(evidence)
            gate = next(g for g in changed["gates"] if "passedChecks" in g)
            gate[field] = value
            with self.subTest(field=field, value=value), self.assertRaises(RuntimeError):
                MODULE.validate_test_evidence(changed, self.output, self.source)
        for name in ["tests-native-workbench", "tests-organization-temporal-ui"]:
            for field, value in [("passedTests", 21.5), ("passedTests", True), ("failedTests", 1), ("skippedTests", 1), ("skippedTests", False)]:
                changed = copy.deepcopy(evidence)
                next(g for g in changed["gates"] if g["name"] == name)[field] = value
                with self.subTest(gate=name, field=field), self.assertRaises(RuntimeError):
                    MODULE.validate_test_evidence(changed, self.output, self.source)

    def test_package_preflight_rejects_missing_gates_or_workspace_tamper_before_dotnet(self):
        for failure in ["missing-gates", "ui-below-floor", "workspace-tamper"]:
            with self.subTest(failure=failure):
                evidence = self.evidence()
                if failure == "missing-gates":
                    evidence["gates"] = []
                elif failure == "ui-below-floor":
                    next(g for g in evidence["gates"] if g["name"] == "tests-organization-temporal-ui")["passedTests"] = 43
                else:
                    workspace = self.prepare()
                    (workspace / WEB / "Program.cs").write_bytes(b"tampered before publish")
                self.write(self.output, "verification.json", json.dumps(evidence).encode())
                args = argparse.Namespace(output=self.output, source_commit=SHA, allow_working_tree=False)
                with patch.object(MODULE, "ROOT", self.repository), patch.object(MODULE, "source_identity", return_value=self.source), patch.object(MODULE, "run") as run:
                    with self.assertRaises(RuntimeError):
                        MODULE.package(args)
                    run.assert_not_called()
                self.assertFalse(list(self.output.glob("hr-development-*")))


if __name__ == "__main__":
    unittest.main()
