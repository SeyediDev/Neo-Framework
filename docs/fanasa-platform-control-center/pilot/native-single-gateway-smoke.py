"""Single-gateway synthetic smoke; never calls the owner's full acceptance suite."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sys
import time

PACKAGE = Path("/opt/fanasa-api-gateway")
sys.path.insert(0, str(PACKAGE / "native"))


def validated_settings(path):
    settings = json.loads(path.read_text())
    pep = settings["pep"]
    if (settings.get("fixtureOnly") is not True
            or settings.get("gateways") != ["gateway1", "gateway2", "gateway3"]
            or settings.get("gatewayPort") != 9086
            or pep.get("issuer") != "https://oidc.fixture.invalid/realms/fanasa"
            or pep.get("pdpEndpoint") != "https://pcc-pdp.fixture.invalid:8443/api/gateway/decision"
            or pep.get("redisHost") != "redis.fixture.invalid"
            or settings.get("redis") != {"host": "redis.fixture.invalid", "port": 6380}):
        raise ValueError("unexpected_fixture_contract")
    return settings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--settings", default=str(PACKAGE / "native/generated/settings.json"))
    parser.add_argument("--state", default="/state/native.sqlite")
    parser.add_argument("--ca", default="/run/native-trust/ca-bundle.pem")
    parser.add_argument("--output", default="/results/native-single-gateway-smoke.json")
    parser.add_argument("--observations", default="/observations")
    args = parser.parse_args()
    cases = []
    evidence = {"schemaVersion": "fanasa.gateway-native-single-smoke.v1",
                "executedAt": datetime.now(timezone.utc).isoformat(),
                "scope": "single gateway1; isolated synthetic fixtures",
                "nativeSmokeExecuted": True, "fullAcceptanceExecuted": False,
                "threeReplicaConcurrencyVerified": False, "guardGatewayVerified": False,
                "deployed": False, "sharedWiringApplied": False,
                "passed": False, "cases": cases}
    before = None
    settings_path = Path(args.settings)
    settings_hash = None
    try:
        # Import the existing package; do not invoke Acceptance.ready/run/main.
        from run import Acceptance, source_hashes
        settings = validated_settings(settings_path)
        settings_hash = hashlib.sha256(settings_path.read_bytes()).hexdigest()
        before = source_hashes(PACKAGE)
        acceptance = Acceptance(settings, args.state, args.ca)
        for role in ("oidc", "pdp", "backend"):
            acceptance.store.set(role)

        def check(name, condition, **numeric_detail):
            cases.append({"name": name, "passed": bool(condition), **numeric_detail})
            if not condition:
                raise AssertionError(name)

        deadline = time.monotonic() + 90
        ready = False
        while time.monotonic() < deadline:
            try:
                redis_ok = acceptance.redis.command("PING") == "PONG"
                oidc_status, discovery = acceptance.http(settings["pep"]["issuer"] + "/.well-known/openid-configuration")
                oidc_ok = oidc_status == 200 and json.loads(discovery).get("issuer") == settings["pep"]["issuer"]
                pdp_status, _ = acceptance.http(settings["pep"]["pdpEndpoint"])
                inactive = acceptance.request(gateway="gateway1", token="native-inactive")
                if redis_ok and oidc_ok and pdp_status == 404 and inactive == 401:
                    ready = True
                    break
            except (OSError, RuntimeError, ValueError):
                pass
            time.sleep(1)
        check("single-gateway-readiness", ready)
        check("verified-redis-tls-ping", redis_ok)
        check("verified-oidc-discovery", oidc_ok, status=oidc_status)
        check("verified-pdp-tls-readiness", pdp_status == 404, status=pdp_status)
        check("inactive-identity-denied", inactive == 401, status=inactive)
        check("fresh-fixture-allocation", acceptance.redis.command("EXISTS", *acceptance.keys) == 0)
        forwards = acceptance.forwards()
        status = acceptance.request(gateway="gateway1", token="native-inactive")
        check("inactive-identity-no-backend-forward", status == 401 and acceptance.forwards() == forwards, status=status)

        # This exercises verified positive OIDC + PDP transport without admission.
        acceptance.store.set("pdp", mode="deny")
        status = acceptance.request(gateway="gateway1")
        check("pdp-deny-no-backend-forward", status == 403 and acceptance.forwards() == forwards
              and acceptance.redis.command("EXISTS", *acceptance.keys) == 0, status=status)
        acceptance.store.set("pdp")

        # Provision only the fresh synthetic allocation; never reset or reuse one.
        acceptance.provision()
        status = acceptance.request(gateway="gateway1")
        used, after_forwards = acceptance.used(), acceptance.forwards()
        check("positive-single-gateway-backend-mtls", status == 200 and used == 1
              and after_forwards == forwards + 1, status=status, used=used, forwarded=after_forwards - forwards)

        # Online positive forwarding above makes this negative TLS check meaningful.
        status, _ = acceptance.http("https://managed-backend.fixture.invalid:8443" + settings["backendPath"],
                                   {"Authorization": "Bearer native-valid",
                                    "X-Fanasa-Tenant": settings["consumerTenantId"]})
        check("direct-backend-without-client-cert-denied", status == 0 and acceptance.forwards() == after_forwards,
              status=status)
        observation_path = Path(args.observations) / "gateway1.txt"
        observation = observation_path.read_text()
        check("gateway1-runtime-observation-present", "replica=gateway1" in observation)
        evidence["runtimeObservations"] = {"gateway1.txt": observation}
        unchanged = before == source_hashes(PACKAGE) and settings_hash == hashlib.sha256(settings_path.read_bytes()).hexdigest()
        check("source-unchanged", unchanged)
        evidence.update({"passed": True, "nativeApisixSmokeVerified": True, "nativeRedisTlsSmokeVerified": True,
                         "backendMtlsSmokeVerified": True, "sourceUnchangedDuringSmoke": unchanged})
    except Exception as error:
        # Fixed error class only; never export exception text, bodies, or credentials.
        evidence["errorClass"] = type(error).__name__
    evidence["sourceSha256"] = before
    evidence["overlaySettingsSha256"] = settings_hash
    evidence["pendingGates"] = ["full three-replica acceptance", "missing-PEP guard gateway",
                                "image digest export", "unclean Redis restart/recovery",
                                "Redis negative TLS", "OIDC cache-expiry outage", "production integration"]
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(evidence, indent=2) + "\n")
    print(json.dumps({"scope": "single-gateway-smoke", "passed": evidence["passed"],
                      "caseCount": len(cases), "errorClass": evidence.get("errorClass")}))
    return 0 if evidence["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
