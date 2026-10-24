#!/usr/bin/env python3
"""Fixed loopback probes; health does not establish HR/SSO authorization."""
import argparse
import json
import sys
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener

URLS = {"web": "http://127.0.0.1:5191/health/live", "workbench": "http://127.0.0.1:4187/health"}
MAX_RESPONSE = 65536


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, file, code, message, headers, new_url):
        return None


def unique_fields(items):
    value = {}
    for key, item in items:
        if key in value:
            raise ValueError("Ambiguous health response")
        value[key] = item
    return value


def probe(mode, opener=None):
    if mode not in URLS:
        raise ValueError("Unknown health mode")
    opener = opener or build_opener(ProxyHandler({}), NoRedirect())
    with opener.open(Request(URLS[mode], headers={"Accept": "application/json"}), timeout=3) as response:
        if response.status != 200:
            raise ValueError("Health status must be 200")
        if response.geturl() != URLS[mode]:
            raise ValueError("Health endpoint redirected outside its fixed URL")
        data = response.read(MAX_RESPONSE + 1)
    if len(data) > MAX_RESPONSE:
        raise ValueError("Health response exceeds limit")
    value = json.loads(data, object_pairs_hook=unique_fields)
    if not isinstance(value, dict):
        raise ValueError("Health response must be an object")
    if mode == "web" and value.get("status") != "Healthy":
        raise ValueError("Web liveness is not healthy")
    if mode == "workbench" and (value.get("status") != "ready" or not isinstance(value.get("storage"), dict)
                                or value["storage"].get("healthy") is not True):
        raise ValueError("Workbench persisted storage is not healthy")
    return {"mode": mode, "status": "healthy"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=sorted(URLS))
    args = parser.parse_args()
    try:
        print(json.dumps(probe(args.mode)))
        return 0
    except Exception:
        print("Health probe refused: endpoint unavailable or invalid", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
