#!/usr/bin/env python3
"""Offline validation of an intended, nonsecret PIP/HR deployment plan.

Never opens CA/secret files, resolves DNS, contacts services, changes configuration
or proves approval. A successful report is not live acceptance.
"""
from __future__ import annotations

import argparse
import ipaddress
import json
from pathlib import Path
import re
import sys
from urllib.parse import urlsplit
import uuid

MAX_INPUT_BYTES = 1024 * 1024
READ_SCOPE = "organization.hr.attributes.read"
HR_AUDIENCE = "fanasa-organization-fabric"
EXPECTED_DEFINITIONS = {
    "org.member.active": ("boolean", True),
    "org.assignment.active": ("boolean", True),
    "org.assignment.count": ("integer", True),
    "org.unit.code": ("string", False),
    "org.position.code": ("string", False),
    "org.role.code": ("string", False),
}
PENDING_CHECKS = ["owner-approval", "client-registration", "service-membership", "tenant-read-grant",
    "active-subscription", "secret-and-ca-presence", "gateway-route-and-mtls",
    "consumer-egress-and-dns", "hr-pip-pdp-runtime-acceptance"]
FIELDS = {
    "plan": {"provider", "consumer", "source"},
    "provider": {"runtime", "hrAuthority", "hrAudience", "reader"},
    "reader": {"clientId", "serviceSubject", "scope", "tenantIds"},
    "consumer": {"runtime", "oauth", "binding", "secretMount", "egressEndpoints"},
    "oauth": {"tenantId", "reference", "tokenEndpoint", "clientId", "clientSecretReference",
              "scopes", "caFile", "allowedPrivateAddresses", "timeoutSeconds"},
    "binding": {"tenantId", "endpoint", "secretReference", "caFile", "allowedPrivateAddresses"},
    "secretMount": {"reference", "environmentKey"},
    "source": {"tenantId", "endpoint", "secretReference", "schemaVersion", "maxAgeSeconds",
               "pollIntervalSeconds", "syncSubjects", "definitions"},
    "definition": {"name", "dataType", "required", "externalName"},
}
REFERENCE = re.compile(r"[A-Za-z][A-Za-z0-9_.-]{0,199}\Z", re.ASCII)
DNS_LABEL = re.compile(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\Z", re.ASCII)


def _report(errors: list[str]) -> dict:
    return {"schemaVersion": 1, "configurationValidated": not errors, "liveAccepted": False,
        "networkPerformed": False, "errors": list(dict.fromkeys(errors)), "pendingChecks": list(PENDING_CHECKS)}


def _guid(value) -> bool:
    if type(value) is not str:
        return False
    try:
        parsed = uuid.UUID(value)
        return parsed.int != 0 and str(parsed) == value
    except (ValueError, AttributeError):
        return False


def _reference(value) -> bool:
    return type(value) is str and REFERENCE.fullmatch(value) is not None


def _integer(value, minimum: int, maximum: int) -> bool:
    return type(value) is int and minimum <= value <= maximum


def _ca_reference(value) -> bool:
    if type(value) is not str or not 2 <= len(value) <= 1024 or not value.startswith("/"):
        return False
    return all(part not in ("", ".", "..") and re.fullmatch(r"[A-Za-z0-9_.-]+", part, re.ASCII)
               for part in value[1:].split("/"))


def _private(address) -> bool:
    if isinstance(address, ipaddress.IPv6Address) and address.ipv4_mapped:
        address = address.ipv4_mapped
    ranges = ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16") if address.version == 4 else ("fc00::/7",)
    return any(address in ipaddress.ip_network(network) for network in ranges)


def _public(address) -> bool:
    if isinstance(address, ipaddress.IPv6Address) and address.ipv4_mapped:
        address = address.ipv4_mapped
    if not address.is_global or address.is_multicast or address.is_reserved:
        return False
    if address.version == 4:
        return not any(address in ipaddress.ip_network(network) for network in ("192.0.0.0/24", "192.88.99.0/24"))
    return address in ipaddress.ip_network("2000::/3") and not any(address in ipaddress.ip_network(network)
        for network in ("2001::/23", "2001:db8::/32", "2002::/16", "3fff::/20"))


def _private_addresses(value) -> bool:
    if type(value) is not list or len(value) > 32 or any(type(item) is not str for item in value):
        return False
    if len(set(value)) != len(value):
        return False
    for item in value:
        try:
            address = ipaddress.ip_address(item)
        except ValueError:
            return False
        if str(address) != item or "%" in item or not _private(address):
            return False
    return True


def _https(value):
    if type(value) is not str or not 1 <= len(value) <= 2048 or any(ord(c) <= 32 or ord(c) >= 127 for c in value):
        return None
    if not value.startswith("https://") or any(c in value for c in ("\\", "?", "#", "%")):
        return None
    try:
        parsed = urlsplit(value)
        host, port = parsed.hostname, parsed.port
    except ValueError:
        return None
    if parsed.scheme != "https" or not host or parsed.username is not None or parsed.password is not None:
        return None
    if port is not None and not 1 <= port <= 65535:
        return None
    # urlsplit normalizes hostname case and can tolerate suffixes after an IPv6
    # bracket. Require an exact, canonical authority before trusting its parts.
    authority_host = "[" + host + "]" if ":" in host else host
    authority = authority_host + (":" + str(port) if port is not None else "")
    if parsed.netloc != authority:
        return None
    if host.endswith(".") or host == "localhost" or host.endswith(".localhost"):
        return None
    if any(part in (".", "..") for part in parsed.path.split("/")) or "//" in parsed.path:
        return None
    try:
        address = ipaddress.ip_address(host)
        if str(address) != host:
            return None
    except ValueError:
        # Reject alternative numeric IP spellings instead of treating them as DNS.
        labels = host.split(".")
        if len(labels) < 2 or len(host) > 253 or not re.search(r"[a-z]", labels[-1]):
            return None
        if all(re.fullmatch(r"(?:0x[0-9a-f]+|[0-9]+)", label, re.ASCII) for label in labels):
            return None
        if any(DNS_LABEL.fullmatch(label) is None for label in labels):
            return None
    return parsed


def validate_plan(plan) -> dict:
    """Return fixed, nonsecret status codes; inspect no external state."""
    errors: list[str] = []

    def fail(code: str) -> None:
        errors.append(code)

    def section(value, name: str):
        if type(value) is not dict:
            fail(name + ".object-required")
            return None
        if any(type(key) is not str for key in value) or set(value) != FIELDS[name]:
            fail(name + ".fields-invalid")
            return None
        return value

    top = section(plan, "plan")
    if top is None:
        return _report(errors)
    provider = section(top["provider"], "provider")
    consumer = section(top["consumer"], "consumer")
    source = section(top["source"], "source")
    reader = section(provider["reader"], "reader") if provider else None
    oauth = section(consumer["oauth"], "oauth") if consumer else None
    binding = section(consumer["binding"], "binding") if consumer else None
    secret_mount = section(consumer["secretMount"], "secretMount") if consumer else None

    def check(condition: bool, code: str) -> None:
        if not condition:
            fail(code)

    if provider:
        check(type(provider["runtime"]) is str and provider["runtime"] in ("kubernetes", "systemd"), "provider.runtime-invalid")
        authority = _https(provider["hrAuthority"])
        check(authority is not None and bool(authority.path) and not authority.path.endswith("/"), "provider.authority-invalid")
        check(provider["hrAudience"] == HR_AUDIENCE, "provider.audience-invalid")
    if reader:
        check(_reference(reader["clientId"]), "reader.client-invalid")
        check(_guid(reader["serviceSubject"]), "reader.subject-invalid")
        check(reader["scope"] == READ_SCOPE, "reader.scope-invalid")
        check(type(reader["tenantIds"]) is list and len(reader["tenantIds"]) == 1
              and all(_guid(item) for item in reader["tenantIds"]), "reader.tenants-invalid")
    if consumer:
        check(type(consumer["runtime"]) is str and consumer["runtime"] in ("kubernetes", "systemd"), "consumer.runtime-invalid")
    for name, value, endpoint_key in (("oauth", oauth, "tokenEndpoint"), ("binding", binding, "endpoint")):
        if value is None:
            continue
        check(_guid(value["tenantId"]), name + ".tenant-invalid")
        check(_ca_reference(value["caFile"]), name + ".ca-path-invalid")
        valid_addresses = _private_addresses(value["allowedPrivateAddresses"])
        check(valid_addresses, name + ".private-addresses-invalid")
        endpoint = _https(value[endpoint_key])
        check(endpoint is not None, name + ".endpoint-invalid")
        if endpoint:
            try:
                address = ipaddress.ip_address(endpoint.hostname)
            except ValueError:
                address = None
            if address is not None:
                if _private(address):
                    check(valid_addresses and str(address) in value["allowedPrivateAddresses"], name + ".private-target-not-listed")
                else:
                    check(_public(address), name + ".target-not-eligible")
    if oauth:
        check(_reference(oauth["reference"]), "oauth.reference-invalid")
        check(_reference(oauth["clientId"]), "oauth.client-invalid")
        check(_reference(oauth["clientSecretReference"]), "oauth.secret-reference-invalid")
        check(type(oauth["clientSecretReference"]) is str and "__" not in oauth["clientSecretReference"],
              "oauth.secret-reference-environment-separator")
        check(type(oauth["scopes"]) is list and oauth["scopes"] == [READ_SCOPE], "oauth.scopes-invalid")
        check(_integer(oauth["timeoutSeconds"], 1, 120), "oauth.timeout-invalid")
    if binding:
        check(_reference(binding["secretReference"]), "binding.reference-invalid")
    if secret_mount:
        check(_reference(secret_mount["reference"]), "secretMount.reference-invalid")
        check(type(secret_mount["environmentKey"]) is str and _reference(secret_mount["environmentKey"]), "secretMount.environment-key-invalid")
        if consumer and consumer["runtime"] == "systemd":
            check(type(secret_mount["environmentKey"]) is str and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", secret_mount["environmentKey"], re.ASCII) is not None,
                  "secretMount.systemd-environment-key-invalid")
    if source:
        check(_guid(source["tenantId"]), "source.tenant-invalid")
        check(_https(source["endpoint"]) is not None, "source.endpoint-invalid")
        check(_reference(source["secretReference"]), "source.reference-invalid")
        check(type(source["schemaVersion"]) is int and source["schemaVersion"] == 2, "source.schema-version-invalid")
        valid_age = _integer(source["maxAgeSeconds"], 1, 86400)
        check(valid_age, "source.max-age-invalid")
        subjects = source["syncSubjects"]
        valid_subjects = type(subjects) is list and 1 <= len(subjects) <= 100 and all(_guid(item) for item in subjects)
        if valid_subjects:
            valid_subjects = len(set(subjects)) == len(subjects)
        check(valid_subjects, "source.subjects-invalid")
        poll = source["pollIntervalSeconds"]
        check(type(poll) is int and (poll == 0 or _integer(poll, 30, 86400) and valid_age
                                    and poll < source["maxAgeSeconds"] and valid_subjects), "source.poll-interval-invalid")
        definitions = source["definitions"]
        if type(definitions) is not list or len(definitions) != len(EXPECTED_DEFINITIONS):
            fail("source.definitions-invalid")
        else:
            names = set()
            for item in definitions:
                definition = section(item, "definition")
                if definition is None:
                    continue
                name = definition["name"]
                if type(name) is not str or name not in EXPECTED_DEFINITIONS or name in names:
                    fail("source.definitions-invalid")
                    continue
                names.add(name)
                data_type, required = EXPECTED_DEFINITIONS[name]
                check(definition["dataType"] == data_type and type(definition["required"]) is bool
                      and definition["required"] == required and definition["externalName"] == name,
                      "source.definitions-invalid")
            check(names == set(EXPECTED_DEFINITIONS), "source.definitions-invalid")

    if reader and source:
        check(reader["tenantIds"] == [source["tenantId"]], "correlation.reader-tenant")
    if oauth and source:
        check(oauth["tenantId"] == source["tenantId"], "correlation.oauth-tenant")
        check(oauth["reference"] == source["secretReference"], "correlation.oauth-reference")
    if binding and source:
        check(binding["tenantId"] == source["tenantId"], "correlation.binding-tenant")
        check(binding["endpoint"] == source["endpoint"], "correlation.binding-endpoint")
        check(binding["secretReference"] == source["secretReference"], "correlation.binding-reference")
        endpoint = _https(binding["endpoint"])
        expected_path = "/development/organization-hr/v1/tenants/" + source["tenantId"] + "/attributes" if _guid(source["tenantId"]) else None
        check(endpoint is not None and endpoint.path == expected_path, "correlation.attributes-path")
    if reader and oauth:
        check(reader["clientId"] == oauth["clientId"], "correlation.reader-client")
        check(oauth["scopes"] == [reader["scope"]], "correlation.reader-scope")
    if provider and oauth:
        authority = provider["hrAuthority"]
        check(type(authority) is str and oauth["tokenEndpoint"] == authority + "/protocol/openid-connect/token", "correlation.token-endpoint")
    if secret_mount and oauth:
        check(secret_mount["reference"] == oauth["clientSecretReference"], "correlation.secret-reference")
        reference = oauth["clientSecretReference"]
        check(type(reference) is str and secret_mount["environmentKey"] == "Pip__Secrets__" + reference,
              "correlation.secret-environment-key")
    if consumer and oauth and binding:
        egress = consumer["egressEndpoints"]
        valid_egress = (type(egress) is list and len(egress) == 2 and all(type(item) is str for item in egress)
                        and type(oauth["tokenEndpoint"]) is str and type(binding["endpoint"]) is str)
        if valid_egress:
            valid_egress = len(set(egress)) == 2 and sorted(egress) == sorted(
                [oauth["tokenEndpoint"], binding["endpoint"]])
        check(valid_egress, "correlation.egress-endpoints")
    return _report(errors)


class _DuplicateField(ValueError):
    pass


def _unique_fields(items):
    value, seen = {}, set()
    for key, item in items:
        normalized = key.casefold()
        if normalized in seen:
            raise _DuplicateField()
        seen.add(normalized)
        value[key] = item
    return value


def _reject_constant(_value):
    raise ValueError()


def main(argv=None) -> int:
    class Parser(argparse.ArgumentParser):
        def error(self, message):
            print(json.dumps(_report(["input.arguments-invalid"]), separators=(",", ":")))
            raise SystemExit(2)

    parser = Parser(description=__doc__)
    parser.add_argument("--plan", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        with args.plan.open("rb") as stream:
            raw = stream.read(MAX_INPUT_BYTES + 1)
        if len(raw) > MAX_INPUT_BYTES:
            report = _report(["input.too-large"])
        else:
            plan = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=_unique_fields, parse_constant=_reject_constant)
            report = validate_plan(plan)
    except _DuplicateField:
        report = _report(["input.duplicate-field"])
    except OSError:
        report = _report(["input.unreadable"])
    except (ValueError, UnicodeError, RecursionError):
        report = _report(["input.invalid-json"])
    print(json.dumps(report, separators=(",", ":")))
    return 0 if report["configurationValidated"] else 2


if __name__ == "__main__":
    sys.exit(main())
