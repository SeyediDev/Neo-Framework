import json
import unittest
from unittest.mock import patch

from codex_evidence import EvidenceError
from codex_model_proxy import ProxyPolicy, public_address


class ModelProxyTests(unittest.TestCase):
    def config(self):
        return dict(clientKey="x" * 40, model="synthetic-model", expiresAt=1200, maxRequests=2)

    def profile(self):
        return dict(base_url="https://model.example/private-api", wire_api="responses", model="synthetic-model",
                    experimental_bearer_token="synthetic-upstream-secret", http_headers={"x-openai-actor-authorization": "synthetic-actor"})

    def test_no_real_key_returned_to_executor_and_store_false(self):
        policy = ProxyPolicy(self.config(), self.profile(), now=1000)
        result = policy.acquire("Bearer " + "x" * 40, "/v1/responses", b'{"model":"synthetic-model","store":true}', now=1001)
        self.assertFalse(json.loads(result)["store"])
        self.assertNotIn(b"synthetic-upstream-secret", result)
        self.assertEqual("model.example", policy.host)
        self.assertEqual("/private-api/responses", policy.path)
        policy.release()

    def test_client_cannot_select_upstream_model_path_or_remote_tools(self):
        for path, body in (("https://other.example/responses", b'{}'), ("/v1/responses?url=evil", b'{}'),
                           ("/v1/responses", b'{"model":"other"}'),
                           ("/v1/responses", b'{"model":"synthetic-model","background":true}'),
                           ("/v1/responses", b'{"model":"synthetic-model","tools":[{"type":"web_search"}]}'),
                           ("/v1/responses", b'{"model":"synthetic-model","model":"other"}')):
            policy = ProxyPolicy(self.config(), self.profile(), now=1000)
            with self.subTest(path=path), self.assertRaises(EvidenceError):
                policy.acquire("Bearer " + "x" * 40, path, body, now=1001)

    def test_expiry_serialization_and_budget_are_enforced(self):
        policy = ProxyPolicy(self.config(), self.profile(), now=1000)
        def acquire(now=1001):
            return policy.acquire("Bearer " + "x" * 40, "/v1/responses", b'{"model":"synthetic-model"}', now=now)
        acquire()
        with self.assertRaises(EvidenceError): acquire()
        policy.release(); acquire(); policy.release()
        with self.assertRaises(EvidenceError): acquire()
        policy = ProxyPolicy(self.config(), self.profile(), now=1000)
        with self.assertRaises(EvidenceError): acquire(now=1200)

    def test_unsafe_config_and_arbitrary_headers_fail_closed(self):
        for change in (dict(base_url="http://host"), dict(base_url="https://user:pass@host"),
                       dict(base_url="https://host/?key=x"), dict(http_headers={"Cookie": "no"}),
                       dict(http_headers={"x-openai-actor-authorization": "unsafe\r\nHeader: injected"})):
            with self.assertRaises(EvidenceError):
                ProxyPolicy(self.config(), self.profile() | change, now=1000)
        for change in (dict(expiresAt=999), dict(expiresAt=1800), dict(maxRequests=0), dict(model=None)):
            with self.assertRaises(EvidenceError):
                ProxyPolicy(self.config() | change, self.profile(), now=1000)

    def test_dns_loopback_linklocal_and_mixed_private_answers_are_denied(self):
        for addresses in (("127.0.0.1",), ("169.254.169.254",), ("8.8.8.8", "10.0.0.1"), ("::1",)):
            with patch("socket.getaddrinfo", return_value=[(2, 1, 6, "", (ip, 443)) for ip in addresses]):
                with self.assertRaises(EvidenceError): public_address("model.example")
        with patch("socket.getaddrinfo", return_value=[(2, 1, 6, "", ("8.8.8.8", 443))]):
            self.assertEqual("8.8.8.8", public_address("model.example"))


if __name__ == "__main__":
    unittest.main()
