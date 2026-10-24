"""Run pinned packaged PEP/context Lua with synthetic dependencies, not native APISIX/Redis."""
from __future__ import annotations
import argparse
import base64
import hashlib
import hmac
import io
import json
import math
from pathlib import Path
import sys
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / ".tools" / "python"))
ZIP = Path("E:/SJVS/Projects/NeoCluster/fanasa-platform-workstreams/api-gateway/native/results/dual-gateway-handoff.zip")
ZIP_SHA256 = "7ef61a999a580bb0f69d8b8d145139708bb2b2175a40244b7930ceef220eec1a"
CONTEXT_SHA256 = "30f3c3f6a29766e6e88ea3d597b9c19a96e61f84d9f9f7619dff60a27d921dc3"
PEP_SHA256 = "abee6b0932da93530cbfe681a21670b00aa5f49ffd1c997353c637e7b89f121d"

class BridgeInputError(Exception):
    pass

def no_duplicates(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise BridgeInputError("Duplicate JSON field.")
        result[key] = value
    return result

def sha(raw):
    return hashlib.sha256(raw).hexdigest()

def main(payload, archive_path):
    from lupa import LuaRuntime, lua_type
    raw_zip = archive_path.read_bytes()
    if sha(raw_zip) != ZIP_SHA256:
        raise BridgeInputError("Pinned archive hash mismatch.")
    with zipfile.ZipFile(io.BytesIO(raw_zip)) as archive:
        pep_source = archive.read("adapters/pep.lua")
        context_source = archive.read("adapters/context.lua")
        manifest = json.loads(archive.read("handoff-source-manifest.json"), object_pairs_hook=no_duplicates)
    if sha(pep_source) != PEP_SHA256 or sha(context_source) != CONTEXT_SHA256:
        raise BridgeInputError("Pinned module hash mismatch.")
    if manifest.get("adapters/pep.lua") != PEP_SHA256 or manifest.get("adapters/context.lua") != CONTEXT_SHA256:
        raise BridgeInputError("Pinned manifest mismatch.")
    if payload.get("operation") != "authorizeAndSign":
        raise BridgeInputError("Only packaged PEP followed by packaged signing is supported.")
    required = {"conf", "claims", "requestId", "expectedPdpRequest", "decision", "admission", "method",
                "rawTarget", "authorization", "bodyBase64", "idempotencyKey", "secret", "keyId", "tier", "nowEpochSeconds"}
    if not required.issubset(payload):
        raise BridgeInputError("Missing bridge input.")
    secret = payload["secret"].encode("utf-8")
    if len(secret) < 32:
        raise BridgeInputError("Synthetic signing key too short.")
    now = payload["nowEpochSeconds"]
    if isinstance(now, bool) or not isinstance(now, (int, float)) or not math.isfinite(now) or now < 0:
        raise BridgeInputError("Invalid clock input.")
    body = base64.b64decode(payload["bodyBase64"], validate=True)
    if base64.b64encode(body).decode("ascii") != payload["bodyBase64"] or len(body) > 65536:
        raise BridgeInputError("Body encoding/size invalid.")
    lua = LuaRuntime(unpack_returned_tuples=True, encoding=None, register_eval=False, register_builtins=False)
    wrap = lua.eval(b"function(callback) return function(...) return callback(...) end end")

    def to_lua(value):
        if isinstance(value, dict):
            return lua.table_from({key.encode("utf-8"): to_lua(item) for key, item in value.items()})
        if isinstance(value, list):
            return lua.table_from([to_lua(item) for item in value])
        if isinstance(value, str):
            return value.encode("utf-8")
        return value

    def from_lua(value):
        if lua_type(value) == "table":
            pairs = list(value.items())
            if pairs and all(isinstance(key, int) and not isinstance(key, bool) for key, _ in pairs):
                keys = sorted(key for key, _ in pairs)
                if keys == list(range(1, len(keys) + 1)):
                    return [from_lua(value[index]) for index in keys]
            return {key.decode("utf-8") if isinstance(key, bytes) else str(key): from_lua(item) for key, item in pairs}
        if isinstance(value, bytes):
            return value.decode("utf-8")
        return value

    pep = lua.execute(pep_source)
    signer = lua.execute(context_source)
    counters = {"pdpCalls": 0, "admissionCalls": 0}
    observed_request = None
    observed_allocation = None
    request_matched = False

    def decide(request):
        nonlocal observed_request, request_matched
        counters["pdpCalls"] += 1
        observed_request = from_lua(request)
        request_matched = observed_request == payload["expectedPdpRequest"]
        if not request_matched:
            raise BridgeInputError("Native request differs from captured Decision request.")
        return to_lua({"status": 200, "body": payload["decision"]})

    def admit(allocation, context):
        nonlocal observed_allocation
        counters["admissionCalls"] += 1
        observed_allocation = from_lua(allocation)
        return to_lua(payload["admission"])

    dependencies = lua.table_from({b"now": wrap(lambda: now), b"decide": wrap(decide), b"admit": wrap(admit)})
    result = pep[b"authorize"](to_lua(payload["conf"]),
        to_lua({"claims": payload["claims"], "now": now, "requestId": payload["requestId"]}), dependencies)
    if counters["pdpCalls"] and not request_matched:
        raise BridgeInputError("Native request binding mismatch.")
    status = result[b"status"]
    code = from_lua(result[b"code"])
    output = {"allowed": False, "status": status, "code": code, "context": None,
        "signature": None, "authorizedContext": None, "pdpRequest": observed_request, "allocation": observed_allocation, **counters,
        "provenance": {"zipSha256": ZIP_SHA256, "contextLuaSha256": CONTEXT_SHA256, "pepLuaSha256": PEP_SHA256,
            "mode": "packaged-pep-and-context", "luaVersion": from_lua(lua.eval(b"_VERSION")),
            "nativeApisix": False, "nativeRedis": False, "admissionMode": "deterministic-test-double",
            "pccDecisionRequestMatched": request_matched, "bodySha256": sha(body)}}
    if status != 200:
        return output
    context = result[b"context"]
    if context is None:
        raise BridgeInputError("PEP returned no confirmed context.")

    def encode(value):
        return json.dumps(from_lua(value), ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")

    crypto = lua.table_from({b"encode": wrap(encode), b"base64": wrap(lambda raw: base64.b64encode(raw)),
        b"hash": wrap(lambda raw: hashlib.sha256(raw).hexdigest().encode("ascii")),
        b"sign": wrap(lambda message: hmac.new(secret, message, hashlib.sha256).hexdigest().encode("ascii"))})
    request = lua.table_from({b"now": now, b"method": payload["method"].encode("utf-8"), b"path": payload["rawTarget"].encode("utf-8"),
        b"authorization": payload["authorization"].encode("utf-8"), b"body": body,
        b"idempotency": payload["idempotencyKey"].encode("utf-8")})
    signed = signer[b"sign"](to_lua({"contextTier": payload["tier"], "contextKeyId": payload["keyId"]}), context, request, crypto)
    if not isinstance(signed, tuple) or len(signed) != 2 or signed[0] is None:
        output.update(status=503, code="context_unavailable")
        return output
    encoded, signature = signed
    output.update(allowed=True, context=encoded.decode("ascii"), signature=signature.decode("ascii"), authorizedContext=from_lua(context))
    return output

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", "--zip", dest="bundle", type=Path, default=ZIP)
    args = parser.parse_args()
    try:
        raw = sys.stdin.buffer.read(2 * 1024 * 1024 + 1)
        if len(raw) > 2 * 1024 * 1024:
            raise BridgeInputError("Input exceeds budget.")
        def invalid_constant(_):
            raise BridgeInputError("Nonfinite JSON value.")
        payload = json.loads(raw, object_pairs_hook=no_duplicates, parse_constant=invalid_constant)
        if not isinstance(payload, dict):
            raise BridgeInputError("Input must be an object.")
        print(json.dumps(main(payload, args.bundle), ensure_ascii=False, separators=(",", ":"), allow_nan=False))
    except Exception as error:
        print(json.dumps({"passed": False, "failureClass": type(error).__name__}), file=sys.stderr)
        raise SystemExit(1)

