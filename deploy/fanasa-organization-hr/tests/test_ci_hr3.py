"""Exercise sealed HR3 gate ordering and complete source identity without .NET, deployment or live credentials."""
import argparse
from contextlib import ExitStack
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import tempfile
import types
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("hr_ci", Path(__file__).parents[1] / "ci.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
REAL_RUN = subprocess.run
SHA = "a" * 40
SOURCE = {"repository": "https://gitlab.fanasa.net.local/javadsayedi/organization-hr-api.git",
          "gitBaseCommit": SHA, "dirty": False, "kind": "git-commit", "files": [], "snapshotSha256": "b" * 64}
ACTIVE_PACKAGE = ROOT / MODULE.HR3_PACKAGE
SEALED_VERIFIER = (ACTIVE_PACKAGE / "verify-package.py").read_bytes()
# Execute the source directly so importing it cannot write __pycache__ into a sealed package.
PACKAGE_VERIFIER = types.ModuleType("active_hr3_source_verifier")
PACKAGE_VERIFIER.__file__ = str(ACTIVE_PACKAGE / "verify-package.py")
exec(compile(SEALED_VERIFIER, PACKAGE_VERIFIER.__file__, "exec"), PACKAGE_VERIFIER.__dict__)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def capture(command):
    if command[:3] == ["git", "rev-parse", "HEAD"]:
        return SHA
    if command[:2] == ["git", "status"]:
        return ""
    if command[:4] == ["git", "remote", "get-url", "origin"]:
        return SOURCE["repository"]
    raise AssertionError(command)


def fixture(root):
    package = root / MODULE.HR3_PACKAGE
    package.mkdir(parents=True)
    (package / "verify-package.py").write_bytes(SEALED_VERIFIER)
    (package / "test-host.sh").write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
    host = root / "products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/ConsumerUsageReport.cs"
    host.parent.mkdir(parents=True)
    host.write_bytes(b"synthetic pinned host input\n")
    # The fixed host boundary must be complete, even for a minimal synthetic tree.
    (host.parent / "Fanasa.AccessManagement.Web.csproj").write_bytes(b"<Project Sdk='Microsoft.NET.Sdk.Web' />\n")
    (host.parent / "appsettings.json").write_bytes(b'{"SyntheticFixture":true}\n')
    asset = host.parent / "wwwroot/js/fixture.js"
    asset.parent.mkdir(parents=True)
    asset.write_bytes(b"// synthetic asset included in host source identity\n")
    pcc = root / PACKAGE_VERIFIER.PCC / "ContractPolicyStore.cs"
    pcc.parent.mkdir(parents=True)
    pcc.write_bytes(b"// synthetic linked PCC source\n")
    (root / "Directory.Build.props").write_bytes(b"<Project />\n")
    # Preserve the actual immutable dependency identities. Only manifest-listed source
    # bytes are copied; no build outputs, local config, credentials or live state.
    dependencies = []
    for directory, pinned in PACKAGE_VERIFIER.DEPENDENCY_MANIFESTS.items():
        original = ACTIVE_PACKAGE.parent / directory
        manifest_bytes = (original / "SOURCE-PACKAGE.json").read_bytes()
        assert digest(manifest_bytes) == pinned
        declared = json.loads(manifest_bytes)
        dependency = package.parent / directory
        dependency.mkdir(parents=True)
        for entry in declared["files"]:
            target = dependency / entry["path"]
            target.parent.mkdir(parents=True, exist_ok=True)
            data = (original / entry["path"]).read_bytes()
            assert digest(data) == entry["sha256"]
            target.write_bytes(data)
        (dependency / "SOURCE-PACKAGE.json").write_bytes(manifest_bytes)
        dependencies.append({"directory": directory, "manifestSha256": pinned})
    previous = PACKAGE_VERIFIER.PREVIOUS_PACKAGE
    original = ACTIVE_PACKAGE.parent / previous["directory"]
    previous_bytes = (original / "SOURCE-PACKAGE.json").read_bytes()
    assert digest(previous_bytes) == previous["manifestSha256"]
    declared = json.loads(previous_bytes)
    destination = package.parent / previous["directory"]
    destination.mkdir(parents=True)
    for entry in declared["files"]:
        target = destination / entry["path"]
        target.parent.mkdir(parents=True, exist_ok=True)
        data = (original / entry["path"]).read_bytes()
        assert digest(data) == entry["sha256"]
        target.write_bytes(data)
    (destination / "SOURCE-PACKAGE.json").write_bytes(previous_bytes)
    manifest = {"schemaVersion": "fanasa.hr3.actual-host-source-package/1",
                "previousSourcePackage": previous,
                "operationalAcceptance": False, "liveHostRegistration": False, "runtimeDefault": "disabled",
                "files": [{"path": name, "sha256": digest((package / name).read_bytes())}
                          for name in ("verify-package.py", "test-host.sh")],
                "dependencies": dependencies,
                "hostInputs": [{"path": relative, "sha256": digest((root / relative).read_bytes())}
                               for relative in sorted(PACKAGE_VERIFIER.collect_host_inputs(root))]}
    (package / "SOURCE-PACKAGE.json").write_text(json.dumps(manifest), encoding="utf-8")
    return host


class Hr3GateTests(unittest.TestCase):
    def execute(self, root, *, drift=False, host_exit=0, changed_source=False, mutation=None):
        host = fixture(root)
        if drift:
            host.write_bytes(host.read_bytes() + b"unreviewed change\n")
        if mutation:
            mutation(root)
        args = argparse.Namespace(output=root / "logs", source_commit=SHA, allow_working_tree=False)
        commands = []

        def child(command, **kwargs):
            commands.append(command)
            if len(commands) == 1:
                # This is the REAL sealed verifier on synthetic package/host bytes.
                return REAL_RUN(command, **kwargs)
            self.assertEqual(command[0], "sh")
            self.assertEqual(Path(command[1]).name, "test-host.sh")
            kwargs["stdout"].write("PASS: synthetic host process; .NET was not run.\n")
            return subprocess.CompletedProcess(command, host_exit)

        after = copy.deepcopy(SOURCE)
        if changed_source:
            after["snapshotSha256"] = "c" * 64
        with ExitStack() as stack:
            stack.enter_context(patch.object(MODULE, "ROOT", root))
            stack.enter_context(patch.object(MODULE, "source_identity", side_effect=[copy.deepcopy(SOURCE), after]))
            stack.enter_context(patch.object(MODULE, "ci_origin", return_value={"engine": "synthetic-test"}))
            stack.enter_context(patch.object(MODULE.subprocess, "run", side_effect=child))
            error = None
            try:
                MODULE.validate_hr3(args)
            except RuntimeError as caught:
                error = caught
        evidence = json.loads((args.output / "hr3-validation.json").read_text(encoding="utf-8"))
        return commands, evidence, error, args.output

    def test_real_sealed_host_drift_stops_before_any_host_build(self):
        with tempfile.TemporaryDirectory() as temp:
            commands, evidence, error, output = self.execute(Path(temp), drift=True)
            self.assertIsNotNone(error)
            self.assertEqual(len(commands), 1, "source drift must prevent execution of test-host.sh")
            self.assertEqual(evidence["status"], "failed")
            self.assertIn("Source bytes changed:", (output / "hr3-source-integrity.log").read_text(encoding="utf-8"))
            self.assertFalse((output / "hr3-actual-host.log").exists())
            self.assertEqual(evidence["logs"], [{"log": "hr3-source-integrity.log",
                                              "logSha256": digest((output / "hr3-source-integrity.log").read_bytes())}])

    def test_integrity_and_host_success_produce_hash_bound_nonoperational_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            commands, evidence, error, output = self.execute(Path(temp))
            self.assertIsNone(error)
            self.assertEqual(len(commands), 2)
            self.assertEqual(Path(commands[0][1]).name, "verify-package.py")
            self.assertEqual(evidence["status"], "passed")
            self.assertFalse(evidence["runtimeActivation"])
            self.assertFalse(evidence["operationalAcceptance"])
            for gate in evidence["gates"]:
                self.assertEqual(gate["exitCode"], 0)
                self.assertEqual(gate["logSha256"], digest((output / gate["log"]).read_bytes()))
            self.assertEqual(len(evidence["logs"]), 2)

    def test_unlisted_host_package_and_dependency_files_stop_before_any_host_build(self):
        additions = [
            "products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Unlisted.cs",
            (MODULE.HR3_PACKAGE / "Unlisted.cs").as_posix(),
            (MODULE.HR3_PACKAGE.parent / "hr-read-profile-v3/Unlisted.cs").as_posix(),
        ]
        for relative in additions:
            with self.subTest(addition=relative), tempfile.TemporaryDirectory() as temp:
                def add_unlisted(root):
                    (root / relative).write_bytes(b"// unlisted source must fail the closed gate\n")
                commands, evidence, error, output = self.execute(Path(temp), mutation=add_unlisted)
                self.assertIsNotNone(error)
                self.assertEqual(len(commands), 1)
                self.assertEqual(evidence["status"], "failed")
                self.assertIn("inventory differs", (output / "hr3-source-integrity.log").read_text(encoding="utf-8"))
                self.assertFalse((output / "hr3-actual-host.log").exists())

    def test_manifest_cannot_narrow_the_fixed_host_inventory(self):
        with tempfile.TemporaryDirectory() as temp:
            def omit_host_input(root):
                path = root / MODULE.HR3_PACKAGE / "SOURCE-PACKAGE.json"
                manifest = json.loads(path.read_text(encoding="utf-8"))
                manifest["hostInputs"] = manifest["hostInputs"][1:]
                path.write_text(json.dumps(manifest), encoding="utf-8")
            commands, evidence, error, output = self.execute(Path(temp), mutation=omit_host_input)
            self.assertIsNotNone(error)
            self.assertEqual(len(commands), 1)
            self.assertEqual(evidence["status"], "failed")
            self.assertIn("Host inventory differs", (output / "hr3-source-integrity.log").read_text(encoding="utf-8"))
            self.assertFalse((output / "hr3-actual-host.log").exists())

    def test_host_failure_and_posttest_source_drift_block_success(self):
        for arguments in ({"host_exit": 1}, {"changed_source": True}):
            with self.subTest(arguments=arguments), tempfile.TemporaryDirectory() as temp:
                commands, evidence, error, _ = self.execute(Path(temp), **arguments)
                self.assertIsNotNone(error)
                self.assertEqual(len(commands), 2)
                self.assertEqual(evidence["status"], "failed")

    def test_missing_sealed_package_cannot_skip_gate(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            args = argparse.Namespace(output=root / "logs", source_commit=SHA, allow_working_tree=False)
            with patch.object(MODULE, "ROOT", root), patch.object(MODULE, "source_identity", return_value=SOURCE), \
                    patch.object(MODULE.subprocess, "run") as child:
                with self.assertRaisesRegex(RuntimeError, "sealed HR3"):
                    MODULE.validate_hr3(args)
                child.assert_not_called()

    def test_protected_pipeline_cannot_package_before_validation(self):
        text = (ROOT / ".gitlab-ci.hr-development.yml").read_text(encoding="utf-8")
        def block(name):
            match = re.search(r"(?ms)^" + re.escape(name) + r":\n(.*?)(?=^[A-Za-z_][A-Za-z0-9_-]*:|\Z)", text)
            self.assertIsNotNone(match, name)
            return match[1]
        stages = re.findall(r"(?m)^  - ([a-z-]+)$", block("stages"))
        validation, packaging = block("hr3_validate_host"), block("hr_verify_package")
        self.assertLess(stages.index("validate-host"), stages.index("verify-package"))
        self.assertRegex(validation, r"(?m)^  stage: validate-host$")
        self.assertRegex(packaging, r"(?m)^  stage: verify-package$")
        self.assertIn("ci.py validate-hr3", validation)
        for job in (validation, packaging):
            self.assertIn("resource_group: organization-hr-development-build", job)
            self.assertNotRegex(job, r"(?m)^  (?:allow_failure|needs|when):", "neither job may bypass the stage barrier")
        self.assertIn("image: fanasa/hr-build:10-node24", block("default"))
        self.assertIn("fanasa-hr-development", block("default"))
        self.assertIn('CI_COMMIT_BRANCH == "develop"', block("workflow"))
        self.assertIn('CI_COMMIT_REF_PROTECTED == "true"', block("workflow"))
        artifact_paths = re.findall(r"(?m)^      - (.+)$", validation)
        self.assertEqual(set(artifact_paths), {".artifacts/hr3-validation/*.log", ".artifacts/hr3-validation/hr3-validation.json"},
                         "the validation job must not transfer opt-in host binaries into packaging")
        self.assertIn("GIT_STRATEGY: clone", block("variables"))


class SourceCompletenessTests(unittest.TestCase):
    def test_selected_financial_successor_retains_the_frozen_predecessor(self):
        self.assertEqual(MODULE.HR3_PACKAGE, Path("deploy/fanasa-organization-hr/hr-read-host-v3-r7"))
        manifest_bytes = (ACTIVE_PACKAGE / "SOURCE-PACKAGE.json").read_bytes()
        self.assertEqual(digest(manifest_bytes), "1e6dfad8a2d5626e555b42c1a402f60cbcf9a001d7156fcb5e537ade660a16a0")
        manifest = json.loads(manifest_bytes)
        previous = {"directory": "hr-read-host-v3-r6",
                    "manifestSha256": "4cdfe260b6706bd33f857288b76f58867ce0d227df7dd5d29c2b49b1ec8af585"}
        self.assertEqual(manifest["previousSourcePackage"], previous)
        self.assertEqual(digest((ACTIVE_PACKAGE.parent / previous["directory"] / "SOURCE-PACKAGE.json").read_bytes()),
                         previous["manifestSha256"])

    def test_only_named_sealed_receipt_logs_participate_in_source_identity(self):
        receipts = {"deploy/fanasa-organization-hr/hr-read-profile-v3/contract-policy/original-regression.log": b"sealed profile receipt\n",
                    "deploy/fanasa-organization-hr/hr-read-pdp-v3/verification.log": b"sealed PDP receipt\n",
                    "deploy/fanasa-organization-hr/hr-read-host-v3-r3/verification.log": b"sealed r3 actual-host receipt\n"}
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for relative, data in receipts.items():
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
            operational = root / "deploy/fanasa-organization-hr/hr-read-pdp-v3/operational.log"
            operational.write_bytes(b"unrelated mutable log\n")
            with patch.object(MODULE, "ROOT", root), patch.object(MODULE, "capture", side_effect=capture):
                first = MODULE.source_identity(SHA, False)
                self.assertEqual(first["files"], [{"path": name, "sha256": digest(receipts[name])} for name in sorted(receipts)])
                operational.write_bytes(b"different unrelated mutable log\n")
                self.assertEqual(MODULE.source_identity(SHA, False), first)
                previous = first
                for relative in receipts:
                    with self.subTest(receipt=relative):
                        changed_bytes = receipts[relative] + b"changed receipt bytes\n"
                        (root / relative).write_bytes(changed_bytes)
                        changed = MODULE.source_identity(SHA, False)
                        self.assertNotEqual(changed["snapshotSha256"], previous["snapshotSha256"])
                        self.assertEqual({entry["path"]: entry["sha256"] for entry in changed["files"]}[relative], digest(changed_bytes))
                        previous = changed

    def test_powershell_source_changes_change_exact_source_identity(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            script = root / "deploy/fanasa-organization-hr/immutable-example/test-host.ps1"
            script.parent.mkdir(parents=True)
            script.write_bytes(b"Write-Output 'first fixture'\n")
            with patch.object(MODULE, "ROOT", root), patch.object(MODULE, "capture", side_effect=capture):
                first = MODULE.source_identity(SHA, False)
                script.write_bytes(b"Write-Output 'different fixture'\n")
                second = MODULE.source_identity(SHA, False)
            self.assertEqual(first["files"], [{"path": script.relative_to(root).as_posix(), "sha256": digest(b"Write-Output 'first fixture'\n")}])
            self.assertNotEqual(first["snapshotSha256"], second["snapshotSha256"], "a changed PowerShell test entrypoint cannot retain source identity")

    def test_every_active_sealed_package_member_is_included_with_its_pinned_hash(self):
        package = ROOT / MODULE.HR3_PACKAGE
        self.assertTrue((package / "SOURCE-PACKAGE.json").is_file(), "the active CI candidate must be present; no fallback to old pins")
        manifest = json.loads((package / "SOURCE-PACKAGE.json").read_text(encoding="utf-8"))
        packages = [(package, manifest)]
        previous = manifest["previousSourcePackage"]
        previous_root = package.parent / previous["directory"]
        previous_bytes = (previous_root / "SOURCE-PACKAGE.json").read_bytes()
        self.assertEqual(digest(previous_bytes), previous["manifestSha256"])
        packages.append((previous_root, json.loads(previous_bytes)))
        for dependency in manifest["dependencies"]:
            dependency_root = package.parent / dependency["directory"]
            packages.append((dependency_root, json.loads((dependency_root / "SOURCE-PACKAGE.json").read_text(encoding="utf-8"))))
        with patch.object(MODULE, "capture", side_effect=capture):
            identity = MODULE.source_identity(SHA, False)
        indexed = {entry["path"]: entry["sha256"] for entry in identity["files"]}
        for entry in manifest["hostInputs"]:
            with self.subTest(hostInput=entry["path"]):
                self.assertEqual(indexed.get(entry["path"]), entry["sha256"], "actual-host pins must be included in exact source identity")
        powershell_count = 0
        for package_root, declared in packages:
            manifest_name = (package_root / "SOURCE-PACKAGE.json").relative_to(ROOT).as_posix()
            self.assertEqual(indexed.get(manifest_name), digest((package_root / "SOURCE-PACKAGE.json").read_bytes()))
            for entry in declared["files"]:
                relative = (package_root / entry["path"]).relative_to(ROOT).as_posix()
                with self.subTest(path=relative):
                    self.assertEqual(indexed.get(relative), entry["sha256"], "manifest-listed source must survive source enumeration byte-for-byte")
                powershell_count += relative.endswith(".ps1")
        self.assertGreater(powershell_count, 0, "the real sealed host package must exercise the PowerShell source-completeness boundary")


if __name__ == "__main__":
    unittest.main()
