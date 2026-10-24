"""Filesystem regression tests for the r3 read-only source-package verifier.

Fixtures are synthetic and never compile or modify the real host or sealed packages.
"""
from __future__ import annotations

import hashlib
import importlib.util
from contextlib import redirect_stdout
from io import StringIO
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock
import xml.etree.ElementTree as ET


VERIFIER_PATH = Path(__file__).resolve().parents[1] / "verify-package.py"
SPEC = importlib.util.spec_from_file_location("hr3_r3_verify_package", VERIFIER_PATH)
assert SPEC is not None and SPEC.loader is not None
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)

WEB = "products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web"
PCC = "docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/pcc"


def write(root: Path, relative: str, content: str = "fixture\n") -> Path:
    target = root / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(content, encoding="utf-8", newline="\n")
    return target


def entries(root: Path, paths: set[str] | list[str]) -> list[dict[str, str]]:
    return [
        {"path": relative, "sha256": hashlib.sha256((root / relative).read_bytes()).hexdigest()}
        for relative in sorted(paths)
    ]


class InventoryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.scratch = tempfile.TemporaryDirectory(prefix="hr3-r3-verifier-")
        self.addCleanup(self.scratch.cleanup)
        self.base = Path(self.scratch.name)
        self.repository = self.base / "repository"
        self.repository.mkdir()
        self.host_paths = {
            "Directory.Build.props",
            "Directory.Build.targets",
            "Directory.Packages.props",
            "NuGet.config",
            "global.json",
            "products/Directory.Build.targets",
            "products/Fanasa.AccessManagement/Directory.Build.props",
            "products/Fanasa.AccessManagement/src/Directory.Build.targets",
            f"{WEB}/Directory.Build.targets",
            f"{WEB}/Fanasa.AccessManagement.Web.csproj",
            f"{WEB}/Program.cs",
            f"{WEB}/Pages/Workbench.cshtml",
            f"{WEB}/Models/Record.cs",
            f"{WEB}/Components/Editor.razor",
            f"{WEB}/Resources/Messages.resx",
            f"{WEB}/Build/Feature.props",
            f"{WEB}/Build/Feature.targets",
            f"{WEB}/appsettings.json",
            f"{WEB}/wwwroot/app.css",
            f"{WEB}/wwwroot/app.js",
            f"{WEB}/wwwroot/images/logo.svg",
            f"{WEB}/wwwroot/fonts/site.woff2",
            f"{WEB}/wwwroot/extensionless",
            f"{PCC}/ContractPolicyStore.cs",
        }
        for relative in sorted(self.host_paths):
            write(self.repository, relative, f"fixture: {relative}\n")
        self.host_entries = entries(self.repository, self.host_paths)
        self.excluded_paths = {
            f"{WEB}/appsettings.Development.json",
            f"{WEB}/appsettings.Staging.json",
        }
        for relative in sorted(self.excluded_paths):
            write(self.repository, relative, "synthetic ignored local configuration\n")

    def package_fixture(self) -> Path:
        """Minimal complete package: one fixture file, one immutable dependency."""
        package = self.base / "bundle" / "hr-read-host-v3-r3"
        dependency = package.parent / "hr-read-profile-v3"
        write(package, "root.fixture", "package fixture\n")
        write(dependency, "root.fixture", "dependency fixture\n")
        dependency_manifest = {"files": entries(dependency, {"root.fixture"})}
        dependency_path = write(
            dependency, "SOURCE-PACKAGE.json", json.dumps(dependency_manifest)
        )
        dependency_hash = hashlib.sha256(dependency_path.read_bytes()).hexdigest()
        dependency_pins = mock.patch.object(
            VERIFIER, "DEPENDENCY_MANIFESTS", {dependency.name: dependency_hash}
        )
        dependency_pins.start()
        self.addCleanup(dependency_pins.stop)
        manifest = {
            "schemaVersion": "fanasa.hr3.actual-host-source-package/1",
            "operationalAcceptance": False,
            "liveHostRegistration": False,
            "runtimeDefault": "disabled",
            "files": entries(package, {"root.fixture"}),
            "dependencies": [{
                "directory": dependency.name,
                "manifestSha256": dependency_hash,
            }],
            "hostInputs": self.host_entries,
        }
        write(package, "SOURCE-PACKAGE.json", json.dumps(manifest))
        return package

    def symlink(self, target: Path, link: Path, *, directory: bool = False) -> None:
        link.parent.mkdir(parents=True, exist_ok=True)
        try:
            os.symlink(target, link, target_is_directory=directory)
        except (NotImplementedError, OSError) as error:
            self.skipTest(f"Filesystem does not permit creating a symlink: {error}")

    def test_inventory_contains_all_supported_web_inputs_and_ancestor_metadata(self) -> None:
        self.assertEqual(self.host_paths, VERIFIER.collect_host_inputs(self.repository))
        self.assertEqual(
            len(self.host_paths),
            VERIFIER.verify_host_inventory(self.repository, self.host_entries),
        )

    def test_new_web_compile_input_is_discovered_and_old_inventory_is_rejected(self) -> None:
        added = f"{WEB}/NewFeature/Unlisted.cs"
        write(self.repository, added)
        self.assertEqual(self.host_paths | {added}, VERIFIER.collect_host_inputs(self.repository))
        with self.assertRaises(ValueError):
            VERIFIER.verify_host_inventory(self.repository, self.host_entries)

    def test_new_pcc_compile_input_is_discovered_and_old_inventory_is_rejected(self) -> None:
        added = f"{PCC}/NewPolicy.cs"
        write(self.repository, added)
        self.assertEqual(self.host_paths | {added}, VERIFIER.collect_host_inputs(self.repository))
        with self.assertRaises(ValueError):
            VERIFIER.verify_host_inventory(self.repository, self.host_entries)

    def test_new_ancestor_build_metadata_is_discovered_and_old_inventory_is_rejected(self) -> None:
        added = "products/Fanasa.AccessManagement/Directory.Build.targets"
        write(self.repository, added)
        self.assertEqual(self.host_paths | {added}, VERIFIER.collect_host_inputs(self.repository))
        with self.assertRaises(ValueError):
            VERIFIER.verify_host_inventory(self.repository, self.host_entries)

    def test_new_ui_asset_is_discovered_and_old_inventory_is_rejected(self) -> None:
        added = f"{WEB}/wwwroot/images/added.png"
        write(self.repository, added)
        self.assertEqual(self.host_paths | {added}, VERIFIER.collect_host_inputs(self.repository))
        with self.assertRaises(ValueError):
            VERIFIER.verify_host_inventory(self.repository, self.host_entries)

    def test_base_published_settings_mutation_is_rejected(self) -> None:
        write(self.repository, f"{WEB}/appsettings.json", "changed official base configuration\n")
        self.assertEqual(self.host_paths, VERIFIER.collect_host_inputs(self.repository))
        with self.assertRaises(ValueError):
            VERIFIER.verify_host_inventory(self.repository, self.host_entries)

    def test_generated_files_and_unrelated_files_are_excluded(self) -> None:
        excluded = self.excluded_paths | {
            f"{WEB}/bin/Debug/Generated.cs",
            f"{WEB}/obj/Debug/Generated.cs",
            f"{WEB}/Nested/__pycache__/Cache.cs",
            f"{WEB}/README.md",
            f"{WEB}/Nested/appsettings.json",
            f"{PCC}/Nested/NotIncluded.cs",
            "products/Unrelated/src/Other.cs",
        }
        for relative in excluded:
            write(self.repository, relative)
        self.assertEqual(self.host_paths, VERIFIER.collect_host_inputs(self.repository))

    def test_omitted_host_input_is_rejected(self) -> None:
        with self.assertRaises(ValueError):
            VERIFIER.verify_host_inventory(self.repository, self.host_entries[1:])

    def test_arbitrary_addition_to_host_manifest_is_rejected(self) -> None:
        write(self.repository, "root.fixture")
        with self.assertRaises(ValueError):
            VERIFIER.verify_host_inventory(
                self.repository, self.host_entries + entries(self.repository, {"root.fixture"})
            )

    def test_removed_host_input_is_rejected(self) -> None:
        (self.repository / f"{WEB}/Program.cs").unlink()
        with self.assertRaises(ValueError):
            VERIFIER.verify_host_inventory(self.repository, self.host_entries)

    def test_mutated_host_bytes_are_rejected(self) -> None:
        write(self.repository, f"{WEB}/Program.cs", "changed after sealing\n")
        with self.assertRaises(ValueError):
            VERIFIER.verify_host_inventory(self.repository, self.host_entries)

    def test_noncanonical_paths_are_rejected_even_when_the_file_resolves_inside_root(self) -> None:
        root = self.base / "paths"
        write(root, "root.fixture")
        digest = entries(root, {"root.fixture"})[0]["sha256"]
        invalid = [
            "", "./root.fixture", "nested/../root.fixture", "nested//root.fixture",
            "nested\\root.fixture", "root.fixture/", "../paths/root.fixture",
            (root / "root.fixture").as_posix(),
        ]
        for relative in invalid:
            with self.subTest(path=relative), self.assertRaises(ValueError):
                VERIFIER.verify_files(root, [{"path": relative, "sha256": digest}])

    def test_duplicate_paths_are_rejected(self) -> None:
        root = self.base / "duplicates"
        write(root, "root.fixture")
        original = entries(root, {"root.fixture"})
        with self.assertRaises(ValueError):
            VERIFIER.verify_files(root, original + original)

    def test_case_aliased_paths_are_rejected(self) -> None:
        root = self.base / "case-aliases"
        write(root, "root.fixture")
        original = entries(root, {"root.fixture"})
        alias = {"path": "ROOT.FIXTURE", "sha256": original[0]["sha256"]}
        with self.assertRaises(ValueError):
            VERIFIER.verify_files(root, original + [alias])

    def test_malformed_digests_are_rejected(self) -> None:
        root = self.base / "digests"
        write(root, "root.fixture")
        for digest in [None, 12, "", "a" * 63, "a" * 65, "z" * 64, "a" * 63 + "\n"]:
            with self.subTest(digest=digest), self.assertRaises(ValueError):
                VERIFIER.verify_files(root, [{"path": "root.fixture", "sha256": digest}])

    def test_file_symlink_is_rejected_even_if_target_and_hash_are_valid(self) -> None:
        root = self.base / "file-symlink"
        target = write(root, "root.fixture")
        self.symlink(target, root / "link.fixture")
        with self.assertRaises(ValueError):
            VERIFIER.verify_files(root, entries(root, {"link.fixture"}))

    def test_parent_directory_symlink_is_rejected(self) -> None:
        root = self.base / "directory-symlink"
        write(root, "real/root.fixture")
        self.symlink(root / "real", root / "link", directory=True)
        with self.assertRaises(ValueError):
            VERIFIER.verify_files(root, entries(root, {"link/root.fixture"}))

    def test_host_compile_symlink_is_rejected(self) -> None:
        target = write(self.base, "outside.fixture")
        self.symlink(target, self.repository / f"{WEB}/Linked.cs")
        with self.assertRaises(ValueError):
            VERIFIER.collect_host_inputs(self.repository)

    def test_link_guard_rejects_symlink_and_windows_reparse_metadata(self) -> None:
        # Exercise both guards even where Windows denies actual symlink creation.
        for mode, attributes in [(stat.S_IFLNK, 0), (stat.S_IFREG, 0x400)]:
            metadata = SimpleNamespace(st_mode=mode, st_file_attributes=attributes)
            with self.subTest(mode=mode, attributes=attributes):
                with mock.patch.object(Path, "lstat", return_value=metadata):
                    with self.assertRaises(ValueError):
                        VERIFIER.reject_link(self.repository)

    def test_inventory_cli_is_read_only_and_emits_only_hash_bound_candidate_data(self) -> None:
        ignored_paths = {self.repository / path for path in self.excluded_paths}
        all_files = {path for path in self.base.rglob("*") if path.is_file()}
        ignored_metadata = {path: path.stat() for path in ignored_paths}
        before = entries(
            self.base,
            {path.relative_to(self.base).as_posix() for path in all_files - ignored_paths},
        )
        result = subprocess.run(
            [sys.executable, "-B", str(VERIFIER_PATH), "--repository", str(self.repository),
             "--print-host-inventory"],
            capture_output=True, text=True, timeout=30, check=True,
        )
        candidate = json.loads(result.stdout)
        self.assertEqual("unsealed-host-inventory-candidate", candidate["kind"])
        self.assertIs(False, candidate["operationalAcceptance"])
        self.assertEqual(len(self.host_paths), candidate["count"])
        self.assertEqual(self.host_entries, candidate["hostInputs"])
        self.assertNotIn("fixture: ", result.stdout)
        self.assertEqual("", result.stderr)
        after = entries(
            self.base,
            {path.relative_to(self.base).as_posix()
             for path in self.base.rglob("*") if path.is_file() and path not in ignored_paths},
        )
        self.assertEqual(before, after)
        self.assertEqual(ignored_metadata, {path: path.stat() for path in ignored_paths})

    def test_collect_and_cli_never_read_or_hash_ignored_local_configuration(self) -> None:
        ignored_paths = {(self.repository / path).absolute() for path in self.excluded_paths}
        original_sha = VERIFIER.sha
        original_read_bytes, original_read_text, original_open = Path.read_bytes, Path.read_text, Path.open

        def allow(path: Path) -> None:
            self.assertNotIn(Path(path).absolute(), ignored_paths, "Ignored local config must not be opened")

        def guarded_sha(path: Path) -> str:
            allow(path)
            return original_sha(path)

        def guarded_read_bytes(path: Path, *args, **kwargs):
            allow(path)
            return original_read_bytes(path, *args, **kwargs)

        def guarded_read_text(path: Path, *args, **kwargs):
            allow(path)
            return original_read_text(path, *args, **kwargs)

        def guarded_open(path: Path, *args, **kwargs):
            allow(path)
            return original_open(path, *args, **kwargs)

        output = StringIO()
        with (
            mock.patch.object(VERIFIER, "sha", side_effect=guarded_sha) as hashes,
            mock.patch.object(Path, "read_bytes", autospec=True, side_effect=guarded_read_bytes),
            mock.patch.object(Path, "read_text", autospec=True, side_effect=guarded_read_text),
            mock.patch.object(Path, "open", autospec=True, side_effect=guarded_open),
            mock.patch.object(sys, "argv", [str(VERIFIER_PATH), "--repository", str(self.repository),
                                           "--print-host-inventory"]),
            redirect_stdout(output),
        ):
            self.assertEqual(self.host_paths, VERIFIER.collect_host_inputs(self.repository))
            hashes.assert_not_called()
            with self.assertRaises(ValueError):
                VERIFIER.verify_host_inventory(
                    self.repository,
                    self.host_entries + [{"path": f"{WEB}/appsettings.Development.json", "sha256": "0" * 64}],
                )
            hashes.assert_not_called()
            VERIFIER.main()
            self.assertEqual(len(self.host_paths), hashes.call_count)
        candidate = json.loads(output.getvalue())
        self.assertEqual(self.host_entries, candidate["hostInputs"])
        self.assertNotIn("synthetic ignored local configuration", output.getvalue())

    def test_opt_in_targets_remove_local_config_before_output_and_publish_assignment(self) -> None:
        project = ET.parse(VERIFIER_PATH.parent / "integration" / "Hr3Host.targets").getroot()
        target = project.find("Target[@Name='ExcludeHr3LocalEnvironmentSettings']")
        self.assertIsNotNone(target)
        assert target is not None
        self.assertIn("'$(EnableHr3Host)' == 'true'", target.attrib.get("Condition", ""))
        self.assertIn("'$(MSBuildProjectName)' == 'Fanasa.AccessManagement.Web'", target.attrib["Condition"])
        self.assertTrue({
            "AssignTargetPaths", "GetCopyToOutputDirectoryItems",
            "GetCopyToPublishDirectoryItems", "_ComputeCopyToPublishDirectoryItems",
        }.issubset(set(target.attrib.get("BeforeTargets", "").split(";"))))
        for item_type in ["Content", "None"]:
            item = target.find(f"ItemGroup/{item_type}")
            self.assertIsNotNone(item)
            assert item is not None
            removed = set(item.attrib.get("Remove", "").split(";"))
            self.assertTrue({"appsettings.*.json", "$(MSBuildProjectDirectory)/appsettings.*.json"}.issubset(removed))
            self.assertNotIn("appsettings.json", removed)

    def test_closed_package_rejects_unlisted_compile_input(self) -> None:
        root = self.base / "closed"
        write(root, "root.fixture")
        original = entries(root, {"root.fixture"})
        write(root, "Unlisted.cs")
        with self.assertRaises(ValueError):
            VERIFIER.verify_files(root, original, closed=True)

    def test_closed_package_allows_only_manifest_and_generated_exclusions(self) -> None:
        root = self.base / "closed-generated"
        write(root, "root.fixture")
        original = entries(root, {"root.fixture"})
        for relative in ["SOURCE-PACKAGE.json", "bin/Output.dll", "obj/Generated.cs", "__pycache__/cache.pyc"]:
            write(root, relative)
        self.assertEqual(1, VERIFIER.verify_files(root, original, closed=True))

    def test_synthetic_package_and_dependency_fixtures_verify_without_real_sources(self) -> None:
        result = VERIFIER.verify_package(self.package_fixture(), self.repository)
        self.assertIsInstance(result, dict)

    def test_package_verification_rejects_unlisted_own_file(self) -> None:
        package = self.package_fixture()
        write(package, "Unlisted.cs")
        with self.assertRaises(ValueError):
            VERIFIER.verify_package(package, self.repository)

    def test_package_verification_rejects_unlisted_dependency_compile_input(self) -> None:
        package = self.package_fixture()
        write(package.parent / "hr-read-profile-v3", "Unlisted.cs")
        with self.assertRaises(ValueError):
            VERIFIER.verify_package(package, self.repository)

    def test_package_verification_rejects_dependency_manifest_mutation(self) -> None:
        package = self.package_fixture()
        target = package.parent / "hr-read-profile-v3" / "SOURCE-PACKAGE.json"
        target.write_bytes(target.read_bytes() + b"\n")
        with self.assertRaises(ValueError):
            VERIFIER.verify_package(package, self.repository)

    def test_editing_package_manifest_cannot_repin_an_immutable_dependency(self) -> None:
        package = self.package_fixture()
        dependency_manifest = package.parent / "hr-read-profile-v3" / "SOURCE-PACKAGE.json"
        dependency_manifest.write_bytes(dependency_manifest.read_bytes() + b"\n")
        package_manifest = package / "SOURCE-PACKAGE.json"
        manifest = json.loads(package_manifest.read_text(encoding="utf-8"))
        manifest["dependencies"][0]["manifestSha256"] = hashlib.sha256(
            dependency_manifest.read_bytes()
        ).hexdigest()
        package_manifest.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaises(ValueError):
            VERIFIER.verify_package(package, self.repository)

    def test_package_verification_rejects_missing_pinned_dependency(self) -> None:
        package = self.package_fixture()
        package_manifest = package / "SOURCE-PACKAGE.json"
        manifest = json.loads(package_manifest.read_text(encoding="utf-8"))
        manifest["dependencies"] = []
        package_manifest.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaises(ValueError):
            VERIFIER.verify_package(package, self.repository)

    def test_package_verification_rejects_duplicate_dependency(self) -> None:
        package = self.package_fixture()
        package_manifest = package / "SOURCE-PACKAGE.json"
        manifest = json.loads(package_manifest.read_text(encoding="utf-8"))
        manifest["dependencies"] *= 2
        package_manifest.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaises(ValueError):
            VERIFIER.verify_package(package, self.repository)

    def test_package_verification_rejects_post_manifest_host_addition(self) -> None:
        package = self.package_fixture()
        write(self.repository, f"{WEB}/AfterManifest.cs")
        with self.assertRaises(ValueError):
            VERIFIER.verify_package(package, self.repository)


class SourceOnlyRevisionTests(unittest.TestCase):
    def test_r4_manifest_records_pending_ci_without_inheriting_prior_success_receipts(self) -> None:
        package = VERIFIER_PATH.parent
        manifest = json.loads((package / "SOURCE-PACKAGE.json").read_text(encoding="utf-8"))
        self.assertEqual("source-only-pending-ci", manifest["evidenceStatus"])
        self.assertEqual(847, manifest["expectedTestAssertions"])
        self.assertIs(manifest["operationalAcceptance"], False)
        self.assertIs(manifest["liveHostRegistration"], False)
        self.assertEqual("disabled", manifest["runtimeDefault"])
        self.assertEqual("hr-read-host-v3-r3", manifest["previousSourcePackage"]["directory"])
        self.assertEqual("be2fa346050551fffc899d27038109fc0040e3b54614b9b7132416c5ad1c416d",
                         manifest["previousSourcePackage"]["manifestSha256"])
        names = {entry["path"] for entry in manifest["files"]}
        for old_receipt in ["verification.log", "test-evidence.json"]:
            self.assertNotIn(old_receipt, names)
            self.assertFalse((package / old_receipt).exists())
        self.assertNotIn("assertions", manifest)
        self.assertNotIn("build", manifest)

    def test_runtime_and_complete_csharp_suite_are_identical_to_the_sealed_predecessor(self) -> None:
        package = VERIFIER_PATH.parent
        predecessor = package.parent / "hr-read-host-v3-r3"
        predecessor_manifest = predecessor / "SOURCE-PACKAGE.json"
        self.assertEqual("be2fa346050551fffc899d27038109fc0040e3b54614b9b7132416c5ad1c416d",
                         hashlib.sha256(predecessor_manifest.read_bytes()).hexdigest())
        entries = json.loads(predecessor_manifest.read_text(encoding="utf-8"))["files"]
        checked = 0
        for entry in entries:
            if Path(entry["path"]).suffix in {".cs", ".csproj", ".props", ".targets"}:
                self.assertEqual(entry["sha256"], hashlib.sha256((package / entry["path"]).read_bytes()).hexdigest(),
                                 entry["path"])
                checked += 1
        self.assertGreaterEqual(checked, 20)


if __name__ == "__main__":
    unittest.main()
