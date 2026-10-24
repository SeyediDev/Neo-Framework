"""Synthetic archive/origin/context boundaries; these tests never invoke Docker."""
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

DIRECTORY = Path(__file__).resolve().parents[1]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


ADAPTER = module("oci_adapter", DIRECTORY / "adapter.py")
HEALTH = module("oci_health", DIRECTORY / "health.py")
FIXTURE = module("oci_original_fixture", DIRECTORY.parent / "tests/test_verify_bundle.py")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def fixture(root, change=None, extra=None):
    archive, receipt = FIXTURE.fixture(root)
    with tarfile.open(archive, "r:gz") as bundle:
        files = {item.name: bundle.extractfile(item).read() for item in bundle}
    ci = json.loads(files["ci-verification.json"])
    ci["source"]["repository"] = ADAPTER.REPOSITORY
    ci["ciOrigin"] = {"engine": "GitLab CI", "projectId": "2", "pipelineId": "6", "jobId": "6",
                      "commit": "a" * 40, "ref": "develop", "protected": True}
    if change:
        change(ci)
    files["ci-verification.json"] = ADAPTER.encode(ci)
    if extra:
        files.update(extra)
    manifest = json.loads(files["release-manifest.json"])
    manifest["source"] = ci["source"]
    manifest["verificationSha256"] = digest(files["ci-verification.json"])
    manifest["files"] = [{"path": name, "sha256": digest(data), "size": len(data)} for name, data in files.items() if name != "release-manifest.json"]
    files["release-manifest.json"] = ADAPTER.encode(manifest)
    with tarfile.open(archive, "w:gz") as bundle:
        for name, data in files.items():
            item = tarfile.TarInfo(name)
            item.size, item.mode = len(data), 0o644
            bundle.addfile(item, io.BytesIO(data))
    original_receipt = json.loads(receipt.read_bytes())
    original_receipt.update({"sha256": digest(archive.read_bytes()), "manifestSha256": digest(files["release-manifest.json"]),
                             "sourceKind": ci["source"]["kind"]})
    receipt.write_bytes(ADAPTER.encode(original_receipt))
    validator = root / "runtime_config.py"
    validator.write_bytes(b"# synthetic read-only validator fixture\n")
    return archive, receipt, validator


def prepare(root, change=None, extra=None, context="context"):
    archive, receipt, validator = fixture(root, change, extra)
    return ADAPTER.prepare(archive, receipt, root / context, runtime_validator=validator)


class AdapterTests(unittest.TestCase):
    def test_deterministic_context_preserves_original_provenance_and_binary_only_recipes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, receipt, validator = fixture(root)
            before = (archive.read_bytes(), receipt.read_bytes())
            first = ADAPTER.prepare(archive, receipt, root / "a", runtime_validator=validator)
            second = ADAPTER.prepare(archive, receipt, root / "b", runtime_validator=validator)
            self.assertEqual(first, second)
            self.assertEqual(first, ADAPTER.checked_context(root / "a"))
            self.assertEqual(before, (archive.read_bytes(), receipt.read_bytes()))
            self.assertEqual((root / "a/evidence/original-release.json").read_bytes(), receipt.read_bytes())
            self.assertEqual(first["binding"]["baseImage"], ADAPTER.BASE_IMAGE)
            self.assertEqual(first["binding"]["runtimeValidatorSha256"], ADAPTER.sha(validator))
            for component in ADAPTER.COMPONENTS:
                recipe = (root / ("a/Dockerfile." + component)).read_text()
                self.assertIn("USER 1654:1654", recipe)
                self.assertIn("WORKDIR /app/" + component, recipe)
                self.assertNotIn("dotnet publish", recipe)
                self.assertNotIn("dotnet build", recipe)
                self.assertNotIn("COPY . ", recipe)
                self.assertNotIn("evidence/", recipe)
            self.assertEqual(first["trustBasis"], "operator-authoritative-receipt-not-signature")
            self.assertEqual(first["promotion"], "pending")

    def test_rejects_wrong_ci_engine_project_ref_protection_and_identity(self):
        variants = {"engine": "local-operator", "projectId": "3", "ref": "main", "protected": False,
                    "pipelineId": "0", "jobId": "bad", "commit": "b" * 40}
        for key, value in variants.items():
            with self.subTest(key=key), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                with self.assertRaises(ValueError):
                    prepare(root, lambda ci: ci["ciOrigin"].update({key: value}))
                self.assertFalse((root / "context").exists())

    def test_rejects_wrong_source_and_dirty_checkout(self):
        changes = [lambda ci: ci["source"].update({"repository": "https://github.com/SeyediDev/Neo-Framework.git"}),
                   lambda ci: ci["source"].update({"kind": "working-tree-snapshot", "dirty": True}),
                   lambda ci: ci.update({"status": "failed"})]
        for change in changes:
            with tempfile.TemporaryDirectory() as temp:
                with self.assertRaises(ValueError):
                    prepare(Path(temp), change)

    def test_rejects_runtime_secrets_state_and_environment_settings(self):
        candidates = {"web/appsettings.json": ADAPTER.encode({"Authentication": {"ClientSecret": "fixture-credential"}}),
                      "web/appsettings.Development.json": b"{}", "web/secret.sqlite3": b"state",
                      "web/secrets/credential.json": b"{}", "web/.env.local": b"secret",
                      "web/credentials.json": b"{}"}
        for name, data in candidates.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temp:
                with self.assertRaises(ValueError):
                    prepare(Path(temp), extra={name: data})

    def test_allows_empty_generic_configuration_and_keeps_credentials_external(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            prepare(root, extra={"web/appsettings.json": ADAPTER.encode({"Authentication": {"ClientSecret": ""}})})
            recipe = (root / "context/Dockerfile.web").read_text()
            self.assertNotIn("identity.invalid", recipe)
            self.assertNotIn("HrApi__Enabled=true", recipe)
            self.assertFalse(any(path.suffix in ADAPTER.SECRET_SUFFIXES for path in (root / "context").rglob("*") if path.is_file()))

    def test_rejects_exact_legacy_public_development_template_before_context_creation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            template = ADAPTER.encode({"Authentication": {"Authority": "http://127.0.0.1:18080/realms/fanasa",
                                                         "ClientId": "fanasa-access-management-web", "ClientSecret": ""}})
            with self.assertRaisesRegex(ValueError, "Environment-specific"):
                prepare(root, extra={"web/appsettings.Development.json": template})
            self.assertFalse((root / "context").exists())
            self.assertFalse(list(root.glob(".hr-oci-prepare-*")))

    def test_preserves_exact_inert_pip_defaults_without_adding_runtime_bindings(self):
        variants = [
            {"Pip": {"Secrets": {}, "Transport": {"Bindings": []}}},
            {"pIP": {"sECRETS": {}, "tRANSPORT": {"bINDINGS": []}}},
        ]
        for pip in variants:
            with self.subTest(pip=pip), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                settings = {"Authentication": {"Authority": "", "ClientId": "", "ClientSecret": ""},
                            "AllowedHosts": "localhost;127.0.0.1", **pip}
                raw = ADAPTER.encode(settings)
                report = prepare(root, extra={"web/appsettings.json": raw})
                self.assertEqual((root / "context/web/appsettings.json").read_bytes(), raw)
                self.assertEqual(report, ADAPTER.checked_context(root / "context"))
                recipe = (root / "context/Dockerfile.web").read_text()
                self.assertNotIn("Pip__", recipe)
                self.assertNotIn("fixture-credential", recipe)

    def test_rejects_nonempty_or_ambiguous_pip_defaults_before_publishing_context(self):
        candidates = [None, False, [], "", {},
                      {"Secrets": {}}, {"Transport": {"Bindings": []}},
                      {"Secrets": {}, "Transport": {"Bindings": []}, "Enabled": False},
                      {"Secrets": {}, "Transport": {"Bindings": []}, "Unknown": {}},
                      {"Secrets": {"source": "fixture-credential"}, "Transport": {"Bindings": []}},
                      {"Secrets": {"source": ""}, "Transport": {"Bindings": []}},
                      {"Secrets": {"source": None}, "Transport": {"Bindings": []}},
                      {"Secrets": [], "Transport": {"Bindings": []}},
                      {"Secrets": None, "Transport": {"Bindings": []}},
                      {"Secrets": {}, "secrets": {}, "Transport": {"Bindings": []}},
                      {"Secrets": {}, "Transport:Bindings": []}]
        for transport in [None, False, [], "", {}, {"Bindings": None}, {"Bindings": {}},
                          {"Bindings": False}, {"Bindings": ""}, {"Bindings": [{}]},
                          {"Bindings": [{"Enabled": False}]},
                          {"Bindings": [{"Endpoint": "https://fixture.invalid/attributes"}]},
                          {"Bindings": [], "bindings": []}, {"Bindings:0": {}},
                          {"Bindings": [], "Unknown": {}}, {"Bindings": [], "Enabled": False}]:
            candidates.append({"Secrets": {}, "Transport": transport})
        for pip in candidates:
            with self.subTest(pip=pip), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                with self.assertRaises(ValueError):
                    prepare(root, extra={"web/appsettings.json": ADAPTER.encode({"Pip": pip})})
                self.assertFalse((root / "context").exists())
                self.assertFalse(list(root.glob(".hr-oci-prepare-*")))

    def test_rejects_changed_public_development_identity_secret_and_extra_sections(self):
        variants = [("Authority", "https://other.invalid/realms/fanasa"), ("ClientId", "different-client"),
                    ("ClientSecret", "fixture-secret")]
        for key, value in variants:
            with self.subTest(key=key), tempfile.TemporaryDirectory() as temp:
                settings = copy.deepcopy(ADAPTER.PUBLIC_DEVELOPMENT_TEMPLATE)
                settings["Authentication"][key] = value
                with self.assertRaises(ValueError):
                    prepare(Path(temp), extra={"web/appsettings.Development.json": ADAPTER.encode(settings)})
        with tempfile.TemporaryDirectory() as temp:
            settings = copy.deepcopy(ADAPTER.PUBLIC_DEVELOPMENT_TEMPLATE)
            settings["Kestrel"] = {"Endpoints": {"Unsafe": {"Url": "http://0.0.0.0:5191"}}}
            with self.assertRaises(ValueError):
                prepare(Path(temp), extra={"web/appsettings.Development.json": ADAPTER.encode(settings)})

    def test_rejects_unsafe_base_settings_that_would_survive_runtime_merge(self):
        candidates = [{"Kestrel": {"Endpoints": {"Unsafe": {"Url": "http://0.0.0.0:5191"}}}},
                      {"PaymentGateway": {"Enabled": True}}, {"UsageOutbox": {"Enabled": True}},
                      {"Organization": {"HrApi": {"Enabled": True}}},
                      {"Authentication": {"Authority": "https://other.invalid/realms/fanasa"}},
                      {"AllowedHosts": "*"}, {"Logging": {"Providers": ["file"]}},
                      {"Authentication": {}, "authentication": {}}]
        for settings in candidates:
            with self.subTest(settings=settings), tempfile.TemporaryDirectory() as temp:
                with self.assertRaises(ValueError):
                    prepare(Path(temp), extra={"web/appsettings.json": ADAPTER.encode(settings)})

    def test_refuses_mutable_or_nonofficial_base_and_existing_destination(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, receipt, validator = fixture(root)
            for image in ["mcr.microsoft.com/dotnet/aspnet:10.0", "example.invalid/aspnet@sha256:" + "a" * 64]:
                with self.assertRaises(ValueError):
                    ADAPTER.prepare(archive, receipt, root / "context", image, validator)
            (root / "context").mkdir()
            with self.assertRaises(ValueError):
                ADAPTER.prepare(archive, receipt, root / "context", runtime_validator=validator)

    def test_refuses_linked_authoritative_input(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, receipt, validator = fixture(root)
            original = Path.is_symlink
            with patch.object(Path, "is_symlink", lambda path: path == archive or original(path)):
                with self.assertRaises(ValueError):
                    ADAPTER.prepare(archive, receipt, root / "context", runtime_validator=validator)

    def test_changed_authoritative_input_is_rejected_after_verification(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, receipt, validator = fixture(root)
            original = ADAPTER.BUNDLE.verify
            def mutate(*args):
                result = original(*args)
                receipt.write_bytes(receipt.read_bytes() + b" ")
                return result
            with patch.object(ADAPTER.BUNDLE, "verify", mutate):
                with self.assertRaisesRegex(ValueError, "changed during preparation"):
                    ADAPTER.prepare(archive, receipt, root / "context", runtime_validator=validator)
            self.assertFalse((root / "context").exists())
            self.assertFalse(list(root.glob(".hr-oci-prepare-*")))

    def test_changed_context_refused_even_if_inventory_is_recomputed(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            prepare(root)
            context = root / "context"
            (context / "web/Fanasa.AccessManagement.Web.dll").write_bytes(b"modified")
            with self.assertRaisesRegex(ValueError, "context changed"):
                ADAPTER.checked_context(context)
            report = json.loads((context / "prepare.json").read_bytes())
            report["files"] = ADAPTER.inventory(context)
            report["contextSnapshotSha256"] = digest(ADAPTER.encode(report["files"]))
            (context / "prepare.json").write_bytes(ADAPTER.encode(report))
            with self.assertRaisesRegex(ValueError, "original verified manifest"):
                ADAPTER.checked_context(context)

    def test_smoke_has_no_host_runtime_mount_no_network_and_nonroot_readonly(self):
        command = ADAPTER.container_args("web", "sha256:" + "a" * 64, "test-name", "test-volume")
        self.assertIn("--read-only", command)
        self.assertEqual(command[command.index("--network") + 1], "none")
        self.assertEqual(command[command.index("--user") + 1], "1654:1654")
        self.assertFalse(any("type=bind" in item for item in command))
        self.assertIn("Organization__HrApi__Enabled=false", command)
        self.assertIn("Platform__RegistryPath=/state/platform-registry.db", command)
        self.assertFalse(any("ClientSecret" in item for item in command))

    def test_success_receipt_keeps_local_image_ids_distinct_from_registry_digests(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            prepared = prepare(root)
            binding = prepared["binding"]
            def fake_docker(args, **kwargs):
                if args[:2] == ["image", "inspect"]:
                    component = "web" if "hr-web:" in args[2] else "workbench"
                    return [{"Id": "sha256:" + ("a" if component == "web" else "b") * 64, "Os": "linux", "Architecture": "amd64",
                             "Config": {"User": "1654:1654", "WorkingDir": "/app/" + component,
                                        "Entrypoint": ["dotnet", ADAPTER.COMPONENTS[component]], "Labels": {
                                            "net.fanasa.original-archive-sha256": binding["archiveSha256"],
                                            "org.opencontainers.image.revision": binding["sourceCommit"],
                                            "net.fanasa.ci.pipeline": "6", "net.fanasa.ci.job": "6",
                                            "net.fanasa.component": component, "net.fanasa.policy-engine-sha256": binding["policyEngineSha256"]}}}]
                return binding["policyEngineSha256"] if args[0] == "run" else ""
            with patch.object(ADAPTER, "docker", fake_docker), patch.object(ADAPTER, "smoke", return_value={"synthetic-test": True}):
                report = ADAPTER.build_smoke(root / "context", root / "oci-receipt.json")
            self.assertEqual(report["status"], "local-image-verified")
            self.assertEqual(report["registryStatus"], "registry-not-pushed")
            self.assertEqual(report["argoStatus"], "Argo-not-installed")
            self.assertEqual(report["promotion"], "pending")
            self.assertIsNone(report["images"]["web"]["registryManifestDigest"])
            self.assertIsNone(report["images"]["workbench"]["registryManifestDigest"])
            self.assertEqual(report["binding"], binding)

    def test_failed_build_never_writes_a_success_receipt(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            prepare(root)
            with patch.object(ADAPTER, "docker", side_effect=RuntimeError("synthetic build failure")):
                with self.assertRaises(RuntimeError):
                    ADAPTER.build_smoke(root / "context", root / "oci-receipt.json")
            self.assertFalse((root / "oci-receipt.json").exists())


class Response:
    def __init__(self, value, status=200, url=None):
        self.body = value if isinstance(value, bytes) else json.dumps(value).encode()
        self.status = status
        self.url = url
    def __enter__(self): return self
    def __exit__(self, *args): pass
    def read(self, maximum): return self.body[:maximum]
    def geturl(self): return self.url


class Opener:
    def __init__(self, response): self.response = response
    def open(self, request, timeout):
        self.request, self.timeout = request, timeout
        if self.response.url is None:
            self.response.url = request.full_url
        return self.response


class HealthTests(unittest.TestCase):
    def test_fixed_urls_bounded_timeout_and_healthy_contracts(self):
        for mode, value in [("web", {"status": "Healthy"}), ("workbench", {"status": "ready", "storage": {"healthy": True}})]:
            opener = Opener(Response(value))
            self.assertEqual(HEALTH.probe(mode, opener), {"mode": mode, "status": "healthy"})
            self.assertEqual(opener.request.full_url, HEALTH.URLS[mode])
            self.assertEqual(opener.timeout, 3)

    def test_fail_closed_for_invalid_http_json_storage_and_oversize(self):
        candidates = [Response({}, 503), Response(b"not-json"), Response([]), Response({"status": "ready"}),
                      Response({"status": "ready", "storage": {"healthy": True}}, url="https://other.invalid/health"),
                      Response(b'{"status":"ready","storage":{"healthy":false,"healthy":true}}'),
                      Response({"status": "ready", "storage": {"healthy": "true"}}),
                      Response({"status": "ready", "storage": {"healthy": False}}), Response(b" " * (HEALTH.MAX_RESPONSE + 1))]
        for response in candidates:
            with self.assertRaises(ValueError):
                HEALTH.probe("workbench", Opener(response))
        with self.assertRaises(ValueError):
            HEALTH.probe("web", Opener(Response({"status": "ready"})))

    def test_environment_proxy_is_disabled(self):
        fake = Opener(Response({"status": "Healthy"}))
        with patch.object(HEALTH, "build_opener", return_value=fake) as builder:
            HEALTH.probe("web")
            self.assertEqual(builder.call_args.args[0].proxies, {})


if __name__ == "__main__":
    unittest.main()
