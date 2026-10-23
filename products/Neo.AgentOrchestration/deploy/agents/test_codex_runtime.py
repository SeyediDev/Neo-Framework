import copy
import hashlib
import http.client
from http.server import ThreadingHTTPServer
import json
from pathlib import Path
import tempfile
import sys
import subprocess
import threading
import unittest
import uuid

from codex_runtime import Runtime, RuntimeErrorSafe, Store, handler_type, bounded_command, acceptance_result
from test_codex_evidence import fixture


class FakeDocker:
    def __init__(self, inspected):
        self.inspected = inspected
        self.calls = []

    def call(self, *args, **kwargs):
        self.calls.append((args, kwargs))
        if args[0] == "inspect": return json.dumps(self.inspected).encode()
        if args[0] == "stop":
            self.inspected[0]["State"].update(Running=False, Status="exited", Pid=0, ExitCode=137)
            return b""
        if args[0] == "logs": return b""
        raise RuntimeErrorSafe("unexpected-fake-operation")


class RuntimeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.expected, self.info, self.inspected = fixture()
        self.runtime = Runtime.__new__(Runtime)
        self.runtime.config = {k: self.expected["scope"][k] for k in ("organizationId", "workspaceId", "projectId")} | {
            "bindingFingerprint": self.expected["bindingFingerprint"], "provider": "synthetic", "model": "test-model"}
        self.runtime.policy = self.expected["policy"]
        self.runtime.root = Path(self.temp.name)
        self.runtime.lock = threading.RLock()
        self.runtime.store = Store(self.runtime.root / "test.sqlite")
        self.runtime.docker = FakeDocker(self.inspected)
        self.rid = self.expected["scope"]["runId"]

    def tearDown(self):
        self.temp.cleanup()

    def body(self):
        return dict(protocol="fanasa-codex/v1", scope=self.expected["scope"], nativeId=self.expected["nativeId"],
                    bindingFingerprint=self.expected["bindingFingerprint"], policy=self.expected["policy"],
                    provider="synthetic", model="test-model", prompt="synthetic task", instructions=None)

    def reserve(self, body, phase="Running", frozen=None):
        raw = json.dumps(body).encode()
        record = copy.deepcopy(self.expected) | dict(frozen=frozen, executor="synthetic-executor", proxy="synthetic-proxy", stopRequested=False)
        self.runtime.store.reserve(self.rid, hashlib.sha256(raw).hexdigest(), record)
        self.runtime.store.save(self.rid, phase, record)
        return raw, record

    def test_store_reserves_durably_before_native_actions_and_conflicts_on_changed_bytes(self):
        raw, record = self.reserve(self.body(), "Intent")
        reopened = Store(self.runtime.root / "test.sqlite")
        self.assertEqual("Intent", reopened.read(self.rid)[1])
        self.assertFalse(reopened.reserve(self.rid, hashlib.sha256(raw).hexdigest(), record))
        with self.assertRaises(RuntimeErrorSafe): reopened.reserve(self.rid, "changed", record)
        with self.assertRaises(RuntimeErrorSafe): reopened.reserve(str(uuid.uuid4()), "new", record)
        self.assertEqual([], self.runtime.docker.calls)

    def test_store_rolls_back_and_closes_on_exception(self):
        with self.assertRaises(RuntimeErrorSafe):
            with self.runtime.store.connect() as db:
                db.execute("INSERT INTO runs VALUES ('rollback','hash','Intent','{}')")
                raise RuntimeErrorSafe("synthetic-rollback")
        self.assertIsNone(self.runtime.store.read("rollback"))

    def test_operator_closed_before_executor_is_not_a_fabricated_exit_or_replay(self):
        raw, record = self.reserve(self.body(), "ClosedBeforeExecutor")
        record["containerId"] = None
        self.runtime.store.save(self.rid, "ClosedBeforeExecutor", record)
        with self.assertRaises(RuntimeErrorSafe): self.runtime.submit(self.rid, raw)
        self.assertEqual([], self.runtime.docker.calls)
        # Only the explicit operator terminal phase frees the native slot;
        # no HTTP route performs that transition or returns a success receipt.
        self.assertTrue(self.runtime.store.reserve(str(uuid.uuid4()), "new-synthetic-body", record))

    def test_bounded_subprocess_rejects_excessive_output_and_timeout(self):
        import os
        self.assertEqual(b"ok", bounded_command([sys.executable, "-c", "print('ok',end='')"], env=os.environ.copy(), limit=10))
        with self.assertRaises(RuntimeErrorSafe):
            bounded_command([sys.executable, "-c", "print('x'*50000)"], env=os.environ.copy(), limit=100)
        with self.assertRaises(RuntimeErrorSafe):
            bounded_command([sys.executable, "-c", "import time;time.sleep(5)"], env=os.environ.copy(), timeout=0.1)

    def test_shipped_template_exits_disabled_before_daemon_or_state_access(self):
        source = Path(__file__).resolve().parent
        result = subprocess.run([sys.executable, "-B", str(source / "codex_runtime.py"), "--config",
                                 str(source / "codex.runtime.template.json")], capture_output=True, timeout=5)
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b"runtime-disabled", result.stderr)
        self.assertNotIn(b"rootless-owner-required", result.stderr)

    def test_acceptance_requires_pinned_observed_report_not_configured_count_alone(self):
        report = dict(protocol="fanasa-acceptance/v1", acceptanceSha256=self.runtime.policy["acceptanceSha256"], testCount=3, passed=True)
        output = json.dumps(report).encode()
        self.assertEqual(3, acceptance_result(output, 0, self.runtime.policy, 3)["testCount"])
        for patch in (dict(testCount=2), dict(testCount=True), dict(acceptanceSha256="0" * 64), dict(passed=False), dict(extra=True)):
            with self.assertRaises(RuntimeErrorSafe): acceptance_result(json.dumps(report | patch).encode(), 0, self.runtime.policy, 3)
        with self.assertRaises(Exception): acceptance_result(b"3 tests OK\n", 0, self.runtime.policy, 3)

    def test_uncertain_intake_never_replays_a_container_or_model(self):
        for phase in ("Intent", "Starting", "Uncertain", "Validating"):
            if self.runtime.store.read(self.rid) is None: raw, record = self.reserve(self.body(), phase)
            else: self.runtime.store.save(self.rid, phase, record)
            with self.subTest(phase=phase), self.assertRaises(RuntimeErrorSafe): self.runtime.submit(self.rid, raw)
        self.assertEqual([], self.runtime.docker.calls)

    def test_identical_frozen_retry_returns_exact_receipt_without_native_write(self):
        frozen = self.runtime.receipt(self.rid, "Completed", containerId=self.expected["containerId"], executorExited=True, exitCode=0)
        raw, _ = self.reserve(self.body(), "Frozen", frozen)
        self.assertEqual(frozen, self.runtime.submit(self.rid, raw))
        self.assertEqual(frozen, self.runtime.evidence(self.rid))
        self.assertEqual([], self.runtime.docker.calls)
        with self.assertRaises(RuntimeErrorSafe): self.runtime.submit(self.rid, raw + b" ")

    def test_body_cannot_select_another_project_repository_model_or_host_command(self):
        changes = [dict(scope=self.expected["scope"] | dict(projectId=str(uuid.uuid4()))),
                   dict(policy=self.expected["policy"] | dict(revision="0" * 40)), dict(model="unapproved"),
                   dict(command="root shell"), dict(prompt=""), dict(instructions="x" * 16385)]
        for change in changes:
            with self.subTest(change=list(change)), self.assertRaises(RuntimeErrorSafe):
                self.runtime.submit(self.rid, json.dumps(self.body() | change).encode())
        self.assertEqual([], self.runtime.docker.calls)

    def test_stop_is_recorded_before_native_stop_and_remains_cancelled_without_fabricated_success(self):
        _, record = self.reserve(self.body())
        self.inspected[0]["State"].update(Running=True, Status="running", Pid=100)
        result = self.runtime.stop(self.rid)
        self.assertTrue(self.runtime.store.read(self.rid)[2]["stopRequested"])
        self.assertEqual("Cancelled", result["state"])
        self.assertFalse(result["modelCompleted"])
        self.assertIsNone(result["validation"])

    def test_scope_or_container_swap_is_never_observed_as_success(self):
        self.reserve(self.body())
        self.inspected[0]["Config"]["Labels"]["fanasa.run-id"] = str(uuid.uuid4())
        with self.assertRaises(RuntimeErrorSafe): self.runtime.observe(self.rid)

    def test_empty_run_id_is_never_an_executable_scope(self):
        with self.assertRaises(RuntimeErrorSafe): self.runtime.scope(str(uuid.UUID(int=0)))
        self.assertEqual([], self.runtime.docker.calls)

    def test_private_ingress_requires_exact_auth_binding_run_and_idempotency(self):
        runtime = self.runtime
        # In-process HTTP fixture only. Production main always wraps loopback in
        # TLS; this is not deployed transport/certificate acceptance.
        key = "synthetic-transport-" + "T" * 32
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_type(runtime, key))
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            def request(auth=None, binding=None, run=None, method="GET", path=None, body=None, idempotency=None):
                client = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=2)
                headers = {"X-Fanasa-Run": run or self.rid, "X-Fanasa-Binding": binding or runtime.config["bindingFingerprint"]}
                if auth: headers["Authorization"] = auth
                if idempotency: headers["Idempotency-Key"] = idempotency
                if body: headers["Content-Type"] = "application/json"
                client.request(method, path or "/v1/codex/runs/" + self.expected["nativeId"], body, headers)
                response = client.getresponse(); raw = response.read(); status = response.status; client.close()
                self.assertNotIn(key.encode(), raw)
                return status
            self.assertEqual(401, request())
            self.assertEqual(401, request(auth="Bearer wrong"))
            self.assertEqual(409, request(auth="Bearer " + key, binding="wrong"))
            self.assertEqual(503, request(auth="Bearer " + key, run="invalid-uuid"))
            self.assertEqual(409, request(auth="Bearer " + key, method="POST", path="/v1/codex/runs", body=b"{}", idempotency="wrong"))
            self.assertEqual([], runtime.docker.calls)
        finally:
            server.shutdown(); server.server_close(); thread.join(timeout=2)


if __name__ == "__main__":
    unittest.main()
