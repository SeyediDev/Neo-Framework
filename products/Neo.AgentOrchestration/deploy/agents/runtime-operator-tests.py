"""Offline installer/control-plane tests; no Docker, model, SQL or HTTP call."""
import importlib.util
import io
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch
import urllib.error

if sys.platform == "win32":
    sys.modules.setdefault("pwd", types.SimpleNamespace(getpwnam=lambda _: None))
folder = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("fanasa_runtime_admin", folder / "runtime-admin.py")
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)


class OperatorTests(unittest.TestCase):
    def test_manifest_has_fixed_official_https_sources_and_exact_hashes(self):
        pins = json.loads((folder / "runtime-pins.json").read_text())
        self.assertRegex(pins["baseImage"], r"^python@sha256:[a-f0-9]{64}$")
        self.assertEqual(set(pins["versions"]), {"opencode", "hermes", "codex"})
        for item in pins["downloads"].values():
            self.assertRegex(item["sha256"], r"^[a-f0-9]{64}$")
            self.assertTrue(item["url"].startswith("https://"))
            self.assertNotIn("latest", item["url"])

    def test_readiness_container_is_non_root_internal_bounded_without_host_mount(self):
        args = runtime.container_options("opencode")
        self.assertEqual(args[args.index("--user") + 1], "10000:10000")
        self.assertEqual(args[args.index("--network") + 1], runtime.NETWORK)
        self.assertIn("--read-only", args)
        self.assertEqual(args[args.index("--cap-drop") + 1], "ALL")
        self.assertEqual(args[args.index("--memory") + 1], args[args.index("--memory-swap") + 1])
        self.assertNotIn("--privileged", args)
        self.assertNotIn("--volume", args)
        self.assertNotIn("type=bind", " ".join(args))
        self.assertNotIn("docker.sock", " ".join(args))
        self.assertNotIn("host", args)

    def check_health(self, engine, body, unauthorized_status=401):
        class Response(io.BytesIO):
            status = 200
        class Opener:
            def __init__(self):
                self.requests = []
            def open(self, request, timeout):
                self.requests.append(request)
                if len(self.requests) == 1:
                    return Response(json.dumps(body).encode())
                if unauthorized_status:
                    raise urllib.error.HTTPError(str(request), unauthorized_status, "test", {}, None)
                return Response(b"{}")
        opener = Opener()
        with patch.object(runtime.urllib.request, "build_opener", return_value=opener):
            result = runtime.probe_health(engine, "a" * 64)
        self.assertEqual(opener.requests[0].full_url.split("/")[2].split(":")[0], "127.0.0.1")
        return result

    def test_authenticated_health_requires_native_schema_and_anonymous_rejection(self):
        self.assertTrue(self.check_health("opencode", {"healthy": True, "version": "1.18.35"}))
        self.assertFalse(self.check_health("opencode", {"healthy": False}))
        self.assertFalse(self.check_health("opencode", {"healthy": True}, 0))
        self.assertFalse(self.check_health("opencode", {"healthy": True}, 500))
        self.assertFalse(self.check_health("opencode", {"arbitrary": "success"}))

    def test_hermes_liveness_does_not_promote_model_readiness_or_execute(self):
        self.assertTrue(self.check_health("hermes", {"platform": "hermes-agent", "api_server": {}, "status": "degraded"}))
        self.assertFalse(self.check_health("hermes", {"platform": "other", "api_server": {}}))

    def test_internal_probe_has_fixed_read_only_route_and_no_key_in_arguments(self):
        with patch.object(runtime, "docker", return_value="true") as docker:
            self.assertTrue(runtime.health("hermes"))
        args = docker.call_args.args
        self.assertEqual(args[:4], ("exec", "fanasa-native-hermes", "python", "-c"))
        self.assertIn("/health/detailed", args[4])
        self.assertNotIn("POST", args[4])
        self.assertNotIn("a" * 64, args[4])
        self.assertNotIn("--privileged", args)
        with patch.object(runtime, "docker", return_value="true\nextra"):
            self.assertFalse(runtime.health("opencode"))


if __name__ == "__main__":
    unittest.main()
