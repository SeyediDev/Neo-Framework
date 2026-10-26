#!/usr/bin/env python3
"""Prepare nonsecret PIP configuration fragments; never install or activate them."""
from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path
import sys
from urllib.parse import urlsplit

import pip_preflight


class PreparationError(ValueError):
    """Contains only a fixed error code, never supplied values."""


def _label(value, maximum):
    if type(value) is not str or not value.strip() or any(ord(char) < 32 or ord(char) == 127 for char in value):
        return False
    try:
        return len(value.encode("utf-16-le")) // 2 <= maximum
    except UnicodeError:
        return False


def prepare_configuration(plan, source_name, reason):
    """Return new independent fragments, after validating the entire input plan."""
    report = pip_preflight.validate_plan(plan)
    if not report["configurationValidated"]:
        raise PreparationError("plan.invalid")
    if not _label(source_name, 200) or not _label(reason, 1000):
        raise PreparationError("source.labels-invalid")
    provider, consumer, source = plan["provider"], plan["consumer"], plan["source"]
    # The existing JWT host is specifically bound to this realm path.
    if urlsplit(provider["hrAuthority"]).path != "/realms/fanasa":
        raise PreparationError("provider.realm-not-supported")
    reader = provider["reader"]
    oauth = consumer["oauth"]
    binding = consumer["binding"]
    host = urlsplit(source["endpoint"]).hostname
    # System.Uri.Host retains brackets around literal IPv6 addresses.
    allowed_host = "[" + host + "]" if ":" in host else host
    provider_fragment = {"Organization": {
        "HrApi": {"Authority": provider["hrAuthority"], "Audience": provider["hrAudience"]},
        "HrAttributes": {"ObservationLifetimeSeconds": 60, "Readers": [{
            "ClientId": reader["clientId"], "ServiceSubject": reader["serviceSubject"],
            "Scope": reader["scope"], "TenantIds": list(reader["tenantIds"])}]}}}
    consumer_fragment = {"Pip": {
        "OAuth": {"Profiles": [{"TenantId": oauth["tenantId"], "Reference": oauth["reference"],
            "TokenEndpoint": oauth["tokenEndpoint"], "ClientId": oauth["clientId"],
            "ClientSecretReference": oauth["clientSecretReference"], "Scopes": list(oauth["scopes"]),
            "CaFile": oauth["caFile"], "AllowedPrivateAddresses": list(oauth["allowedPrivateAddresses"]),
            "TimeoutSeconds": oauth["timeoutSeconds"]}]},
        "Transport": {"Bindings": [{"TenantId": binding["tenantId"], "Endpoint": binding["endpoint"],
            "SecretReference": binding["secretReference"], "CaFile": binding["caFile"],
            "AllowedPrivateAddresses": list(binding["allowedPrivateAddresses"])}]}}}
    registration = {"name": source_name.strip(), "endpoint": source["endpoint"], "allowedHost": allowed_host,
        "secretReference": source["secretReference"], "schemaVersion": 2, "maxAgeSeconds": source["maxAgeSeconds"],
        "timeoutSeconds": 10, "maxResponseBytes": 65536, "enabled": False,
        "definitions": copy.deepcopy(source["definitions"]), "syncSubjects": list(source["syncSubjects"]),
        "pollIntervalSeconds": source["pollIntervalSeconds"], "reason": reason.strip()}
    instructions = {"schemaVersion": 1, "configurationValidated": True, "liveAccepted": False,
        "networkPerformed": False, "installed": False, "sourceRegistered": False, "sourceActivated": False,
        "providerRuntime": provider["runtime"], "consumerRuntime": consumer["runtime"],
        "mergeRequired": True, "standaloneAppsettings": False,
        "registration": {"method": "POST", "path": "/api/governance/tenants/" + source["tenantId"] + "/pip/sources",
                         "bodyFile": "source.register.json", "authentication": "authorized-cookie-and-antiforgery"},
        "secretInjection": copy.deepcopy(consumer["secretMount"]),
        "consumerEgressEndpoints": list(consumer["egressEndpoints"]),
        "pendingChecks": list(report["pendingChecks"])}
    return {"provider.fragment.json": provider_fragment, "consumer.fragment.json": consumer_fragment,
            "source.register.json": registration, "preparation.json": instructions}


def _read_plan(path):
    try:
        with path.open("rb") as stream:
            raw = stream.read(pip_preflight.MAX_INPUT_BYTES + 1)
        if len(raw) > pip_preflight.MAX_INPUT_BYTES:
            raise PreparationError("input.too-large")
        return json.loads(raw.decode("utf-8-sig"), object_pairs_hook=pip_preflight._unique_fields,
                          parse_constant=pip_preflight._reject_constant)
    except pip_preflight._DuplicateField:
        raise PreparationError("input.duplicate-field") from None
    except OSError:
        raise PreparationError("input.unreadable") from None
    except (UnicodeError, ValueError, RecursionError) as error:
        if isinstance(error, PreparationError):
            raise
        raise PreparationError("input.invalid-json") from None


def main(argv=None):
    class Parser(argparse.ArgumentParser):
        def error(self, message):
            raise PreparationError("input.arguments-invalid")

    try:
        parser = Parser(description=__doc__)
        parser.add_argument("--plan", required=True, type=Path)
        parser.add_argument("--source-name", required=True)
        parser.add_argument("--reason", required=True)
        parser.add_argument("--output-directory", required=True, type=Path)
        args = parser.parse_args(argv)
        artifacts = prepare_configuration(_read_plan(args.plan), args.source_name, args.reason)
        # Validate and serialize everything before creating any output. Existing
        # directories/files are never overwritten; no runtime path is inferred.
        serialized = {name: json.dumps(value, ensure_ascii=False, indent=2) + "\n"
                      for name, value in artifacts.items()}
        try:
            args.output_directory.mkdir(parents=False, exist_ok=False)
            for name, content in serialized.items():
                with (args.output_directory / name).open("x", encoding="utf-8", newline="\n") as stream:
                    stream.write(content)
        except OSError:
            raise PreparationError("output.not-created-completely") from None
        print(json.dumps({"prepared": True, "files": list(artifacts), "liveAccepted": False,
                          "installed": False, "sourceActivated": False}))
        return 0
    except PreparationError as error:
        print(json.dumps({"prepared": False, "error": str(error), "liveAccepted": False}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
