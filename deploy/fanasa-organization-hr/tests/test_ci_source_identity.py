"""Verify source identity on synthetic files without accessing real local settings."""
from __future__ import annotations

import builtins
from contextlib import ExitStack
import hashlib
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("hr_ci_source_identity", Path(__file__).parents[1] / "ci.py")
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

SHA = "a" * 40
REPOSITORY = "https://gitlab.fanasa.net.local/javadsayedi/organization-hr-api.git"
WEB = "products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web"
IMPLEMENTATION = "docs/fanasa-platform-control-center/workstreams/contract-policy/implementation"
DEPLOY = "deploy/fanasa-organization-hr"


def digest(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


class SourceIdentityConfigurationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.scratch = tempfile.TemporaryDirectory(prefix="hr-ci-source-identity-")
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.base_config = f"{WEB}/appsettings.json"
        self.example_configs = {
            f"{DEPLOY}/hr-read-host-v3-r3/appsettings.hr3.disabled.json",
            f"{DEPLOY}/hr-read-profile-v3/appsettings.example.json",
            f"{DEPLOY}/hr-read-pdp-v3/appsettings.example.json",
        }
        self.included = {
            self.base_config: b'{"synthetic": "official base"}\n',
            f"{WEB}/Program.cs": b"// Synthetic host source.\n",
            f"{IMPLEMENTATION}/src/Synthetic/appsettings.json": b'{"synthetic": "tool base"}\n',
            **{relative: b'{"synthetic": "sealed example", "enabled": false}\n'
               for relative in self.example_configs},
        }
        self.ignored = {
            f"{WEB}/appsettings.Development.json",
            f"{WEB}/appsettings.Staging.json",
            f"{WEB}/appsettings.local.json",
            f"{WEB}/Nested/appsettings.local.json",
            f"{IMPLEMENTATION}/src/Synthetic/appsettings.Development.json",
            f"{DEPLOY}/hr-read-host-v3-r3/src/appsettings.local.json",
        }
        for relative, content in self.included.items():
            self.write(relative, content)
        for relative in self.ignored:
            self.write(relative, b"synthetic ignored configuration; must never be read\n")

    def write(self, relative: str, content: bytes) -> None:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    def capture(self, command: list[str]) -> str:
        if command == ["git", "rev-parse", "HEAD"]:
            return SHA
        if command == ["git", "status", "--porcelain", "--", *MODULE.SOURCE_PATHS]:
            return ""
        if command == ["git", "remote", "get-url", "origin"]:
            return REPOSITORY
        self.fail(f"Unexpected external command: {command!r}")

    def identity(self) -> tuple[dict, set[str]]:
        """All content access is guarded while the real source collector runs."""
        ignored_paths = {(self.root / relative).absolute() for relative in self.ignored}
        original_sha = MODULE.sha256
        original_path_open = Path.open
        original_read_bytes = Path.read_bytes
        original_read_text = Path.read_text
        original_builtin_open = builtins.open
        hashed_paths: set[str] = set()

        def allow(file) -> None:
            if isinstance(file, (str, bytes, os.PathLike)):
                path = Path(os.fsdecode(file)).absolute()
                self.assertNotIn(path, ignored_paths, "CI must never read or hash local src settings")

        def sha(path: Path) -> str:
            allow(path)
            hashed_paths.add(path.relative_to(self.root).as_posix())
            return original_sha(path)

        def path_open(path: Path, *args, **kwargs):
            allow(path)
            return original_path_open(path, *args, **kwargs)

        def read_bytes(path: Path, *args, **kwargs):
            allow(path)
            return original_read_bytes(path, *args, **kwargs)

        def read_text(path: Path, *args, **kwargs):
            allow(path)
            return original_read_text(path, *args, **kwargs)

        def builtin_open(file, *args, **kwargs):
            allow(file)
            return original_builtin_open(file, *args, **kwargs)

        with ExitStack() as stack:
            stack.enter_context(patch.object(MODULE, "ROOT", self.root))
            stack.enter_context(patch.object(MODULE, "capture", side_effect=self.capture))
            stack.enter_context(patch.object(MODULE, "sha256", side_effect=sha))
            stack.enter_context(patch.object(Path, "open", autospec=True, side_effect=path_open))
            stack.enter_context(patch.object(Path, "read_bytes", autospec=True, side_effect=read_bytes))
            stack.enter_context(patch.object(Path, "read_text", autospec=True, side_effect=read_text))
            stack.enter_context(patch.object(builtins, "open", side_effect=builtin_open))
            result = MODULE.source_identity(SHA, allow_working_tree=False)
        return result, hashed_paths

    def test_source_identity_never_reads_hashes_or_includes_src_environment_settings(self) -> None:
        source, hashed = self.identity()
        actual = {entry["path"]: entry["sha256"] for entry in source["files"]}
        expected = {relative: digest(content) for relative, content in self.included.items()}
        self.assertEqual(set(expected), set(actual), "Official base configs and sealed examples must be included")
        self.assertEqual(expected, actual)
        self.assertEqual(set(self.included), hashed)
        self.assertTrue(self.ignored.isdisjoint(actual))
        self.assertEqual(REPOSITORY, source["repository"])
        self.assertEqual(SHA, source["gitBaseCommit"])

    def test_changing_ignored_environment_settings_leaves_exact_identity_unchanged(self) -> None:
        before, before_hashed = self.identity()
        for relative in self.ignored:
            self.write(relative, b"different synthetic local configuration\n")
        after, after_hashed = self.identity()
        self.assertEqual(before, after)
        self.assertEqual(before_hashed, after_hashed)

    def test_adding_another_local_src_configuration_leaves_identity_unchanged(self) -> None:
        before, before_hashed = self.identity()
        added = f"{WEB}/appsettings.SyntheticTenant.json"
        self.ignored.add(added)
        self.write(added, b"new synthetic machine configuration\n")
        after, after_hashed = self.identity()
        self.assertEqual(before, after)
        self.assertEqual(before_hashed, after_hashed)

    def test_changing_official_base_config_changes_file_hash_and_snapshot(self) -> None:
        before, _ = self.identity()
        replacement = b'{"synthetic": "changed official base"}\n'
        self.write(self.base_config, replacement)
        after, hashed = self.identity()
        self.assertNotEqual(before["snapshotSha256"], after["snapshotSha256"])
        self.assertEqual(
            digest(replacement),
            next(entry["sha256"] for entry in after["files"] if entry["path"] == self.base_config),
        )
        self.assertEqual(set(self.included), hashed)

    def test_each_sealed_package_root_example_remains_hash_bound(self) -> None:
        before, _ = self.identity()
        replacement = b'{"synthetic": "changed sealed example", "enabled": false}\n'
        for relative in sorted(self.example_configs):
            with self.subTest(example=relative):
                self.write(relative, replacement)
                after, hashed = self.identity()
                self.assertNotEqual(before["snapshotSha256"], after["snapshotSha256"])
                self.assertEqual(
                    digest(replacement),
                    next(entry["sha256"] for entry in after["files"] if entry["path"] == relative),
                )
                self.assertIn(relative, hashed)
                self.write(relative, self.included[relative])
                restored, _ = self.identity()
                self.assertEqual(before, restored)


if __name__ == "__main__":
    unittest.main()
