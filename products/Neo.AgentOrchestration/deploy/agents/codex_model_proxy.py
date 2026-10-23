"""Per-run Responses relay; real provider credentials never enter the executor.

Private container only, no published port, restart=no. Configuration and expiry
are operator-owned; agent input cannot choose a host, path, model or auth header.
No logging of requests, responses, headers, exceptions or credential values.
"""
import hmac
import http.client
import ipaddress
import json
import re
import socket
import ssl
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from codex_evidence import EvidenceError, json_object


class ProxyPolicy:
    def __init__(self, config, profile, now=None):
        # The profile comes from a private operator-only mount in THIS proxy,
        # never from a request, repository, executor environment or task record.
        now = time.time() if now is None else now
        url = urllib.parse.urlsplit(profile.get("base_url", ""))
        if (url.scheme != "https" or not url.hostname or url.port not in (None, 443) or
                url.username or url.password or url.query or url.fragment or
                profile.get("wire_api") != "responses" or
                not isinstance(config.get("model"), str) or not re.fullmatch(r"[A-Za-z0-9_.:-]{1,100}", config["model"]) or
                profile.get("model") != config.get("model") or
                not isinstance(profile.get("experimental_bearer_token"), str) or
                not profile["experimental_bearer_token"] or
                not isinstance(config.get("clientKey"), str) or not 32 <= len(config["clientKey"]) <= 128 or
                type(config.get("maxRequests")) is not int or not 1 <= config["maxRequests"] <= 40 or
                type(config.get("expiresAt")) not in (int, float) or not now < config["expiresAt"] <= now + 600):
            raise EvidenceError("model-proxy-policy-invalid")
        self.host = url.hostname
        self.path = url.path.rstrip("/") + "/responses"
        self.model = config["model"]
        self.client_key = config["clientKey"]
        self.expires = config["expiresAt"]
        self.budget = config["maxRequests"]
        self.lock = threading.Lock()
        self.active = False
        self.headers = {"Authorization": "Bearer " + profile["experimental_bearer_token"],
                        "Content-Type": "application/json", "Accept": "text/event-stream"}
        extra = profile.get("http_headers", {})
        # Only the already-authorized proxy actor header, never arbitrary
        # Host/cookie/forwarded/auth or hop-by-hop fields from configuration.
        if not isinstance(extra, dict) or set(extra) - {"x-openai-actor-authorization"}:
            raise EvidenceError("model-proxy-headers-invalid")
        for key, value in extra.items():
            if not isinstance(value, str) or not value or any(ord(c) < 32 for c in value):
                raise EvidenceError("model-proxy-headers-invalid")
            self.headers[key] = value
        if any(ord(c) < 32 for c in profile["experimental_bearer_token"]):
            raise EvidenceError("model-proxy-headers-invalid")

    def acquire(self, authorization, path, body, now=None):
        now = time.time() if now is None else now
        if path != "/v1/responses" or not hmac.compare_digest(authorization, "Bearer " + self.client_key):
            raise EvidenceError("model-proxy-denied")
        data = json_object(body, 256 * 1024)
        if not isinstance(data, dict) or data.get("model") != self.model or data.get("background") is True:
            raise EvidenceError("model-proxy-request-invalid")
        tools = data.get("tools", [])
        if not isinstance(tools, list) or len(tools) > 64 or any(
                not isinstance(x, dict) or x.get("type") not in ("function", "custom") for x in tools):
            raise EvidenceError("model-proxy-tool-denied")
        data["store"] = False
        with self.lock:
            if now >= self.expires or self.budget <= 0 or self.active:
                raise EvidenceError("model-proxy-budget-denied")
            self.budget -= 1
            self.active = True
        return json.dumps(data, separators=(",", ":")).encode()

    def release(self):
        with self.lock:
            self.active = False


def public_address(host):
    rows = socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)
    if not rows or any(not ipaddress.ip_address(x[4][0]).is_global for x in rows):
        raise EvidenceError("model-proxy-address-denied")
    return rows[0][4][0]


class PinnedHttps(http.client.HTTPSConnection):
    def connect(self):
        # Resolve once, validate all answers, connect to that actual public IP,
        # then validate TLS with the original hostname. No redirect or re-resolve.
        raw = socket.create_connection((public_address(self.host), 443), timeout=self.timeout)
        self.sock = self._context.wrap_socket(raw, server_hostname=self.host)
        self.relay_socket = self.sock


def handler_type(policy):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"
        server_version = "FanasaModelRelay/1"

        def log_message(self, *_):
            pass

        def deny(self, status=403):
            self.send_response(status)
            self.send_header("Content-Length", "0")
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True

        def do_GET(self):
            self.deny()

        def do_POST(self):
            connection = None
            acquired = False
            headers_sent = False
            try:
                self.connection.settimeout(10)
                lengths = self.headers.get_all("Content-Length", [])
                auth = self.headers.get_all("Authorization", [])
                if (len(lengths) != 1 or not lengths[0].isdigit() or not 0 < int(lengths[0]) <= 256 * 1024 or
                        len(auth) != 1 or self.headers.get("Transfer-Encoding") or self.headers.get("Content-Encoding") or
                        self.headers.get("Content-Type", "").split(";", 1)[0].strip() != "application/json"):
                    raise EvidenceError("model-proxy-request-invalid")
                raw = self.rfile.read(int(lengths[0]))
                if len(raw) != int(lengths[0]):
                    raise EvidenceError("model-proxy-request-incomplete")
                payload = policy.acquire(auth[0], self.path, raw)
                acquired = True
                deadline = min(time.monotonic() + 180, time.monotonic() + policy.expires - time.time())
                connection = PinnedHttps(policy.host, timeout=min(15, max(0.1, deadline - time.monotonic())),
                                         context=ssl.create_default_context())
                connection.request("POST", policy.path, body=payload, headers=policy.headers)
                response = connection.getresponse()
                if response.status != 200:
                    self.deny(502)
                    return
                content_type = response.getheader("Content-Type", "").split(";", 1)[0].strip()
                if content_type not in ("text/event-stream", "application/json"):
                    self.deny(502)
                    return
                self.send_response(200)
                self.send_header("Content-Type", content_type)
                self.send_header("Connection", "close")
                self.end_headers()
                headers_sent = True
                size = 0
                while True:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise EvidenceError("model-proxy-timeout")
                    connection.relay_socket.settimeout(min(30, remaining))
                    chunk = response.read1(16384)
                    if not chunk:
                        break
                    size += len(chunk)
                    if size > 16 * 1024 * 1024:
                        raise EvidenceError("model-proxy-output-limit")
                    self.wfile.write(chunk)
                    self.wfile.flush()
                self.close_connection = True
            except Exception:
                if not headers_sent:
                    try:
                        self.deny(502 if acquired else 403)
                    except OSError:
                        pass
                self.close_connection = True
            finally:
                if connection:
                    connection.close()
                if acquired:
                    policy.release()
    return Handler


if __name__ == "__main__":
    # Both mounts are private operator inputs. Never echo their contents.
    if sys.argv[1:] == ["--stdin"]:
        startup = json_object(sys.stdin.buffer.readline(64 * 1024 + 1), 64 * 1024)
        policy = ProxyPolicy(startup["config"], startup["profile"])
        startup = None
    else:
        policy = ProxyPolicy(json_object(Path("/policy/run.json").read_bytes()),
                             json_object(Path("/private/provider.json").read_bytes()))
    server = ThreadingHTTPServer(("0.0.0.0", 8080), handler_type(policy))
    server.daemon_threads = True
    server.serve_forever()
