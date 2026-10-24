#!/usr/bin/env python3
"""Fail closed on mounted HR candidate config, preserved state and real TLS readiness.

No secrets or detailed exceptions are written to stdout/stderr. The default check
is an init-container preflight, before either application can seed a missing DB.
"""
from __future__ import annotations
import argparse
import base64
from contextlib import closing
from datetime import datetime
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import sqlite3
import ssl
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET

ISSUER = "https://sso.fanasa.net.local/realms/fanasa"
TLS_NAME = "organization-hr-upstream.fanasa.net.local"
MAX_JSON = 65536
MAX_STATE_SNAPSHOT = 64 * 1024 * 1024
TABLES = {
    "fabric": {"fabric_documents", "fabric_outbox"},
    "registry": {"platform_products", "platform_tenants", "platform_grants", "platform_meta", "platform_audit"},
    "workbench": {"state_metadata", "trusted_context", "policies", "drafts", "resources", "decisions", "operations", "executions", "audit"},
}


def unique_fields(items):
    result, seen = {}, set()
    for key, value in items:
        if key.casefold() in seen or ":" in key:
            raise ValueError("Ambiguous configuration field")
        seen.add(key.casefold())
        result[key] = value
    return result


def read_config(path: Path, expected_hash: str | None = None) -> dict:
    with path.open("rb") as stream:
        raw = stream.read(MAX_JSON + 1)
    if len(raw) > MAX_JSON:
        raise ValueError("Configuration exceeds size bound")
    if expected_hash is not None and (not re.fullmatch(r"[a-f0-9]{64}", expected_hash)
                                     or hashlib.sha256(raw).hexdigest() != expected_hash):
        raise ValueError("Mounted configuration differs from the rendered candidate")
    value = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique_fields)
    if not isinstance(value, dict):
        raise ValueError("Configuration object required")
    return value


def validate_config(value: dict) -> None:
    auth, hr = value["Authentication"], value["Organization"]["HrApi"]
    if (auth["Authority"] != ISSUER or not isinstance(auth["ClientId"], str) or not auth["ClientId"]
            or hr["Enabled"] is not True or hr["Authority"] != ISSUER
            or hr["Audience"] != "fanasa-organization-fabric"
            or hr["DeploymentMetadataPath"] != "/runtime-config/deployment.json"
            or hr["ClientId"] != "fanasa-organization-hr-development"
            or hr["ReadScope"] != "organization.hr.read" or hr["WriteScope"] != "organization.hr.write"
            or not isinstance(hr["GatewayCertificateSha256"], str)
            or not re.fullmatch(r"[a-fA-F0-9]{64}", hr["GatewayCertificateSha256"])):
        raise ValueError("Scoped identity settings required")
    subject = hr["ServiceSubject"]
    if str(uuid.UUID(subject)) != subject or uuid.UUID(subject).int == 0:
        raise ValueError("Canonical service subject required")
    endpoints = value["Kestrel"]["Endpoints"]
    if set(endpoints) != {"Human", "OrganizationHr"} or endpoints["Human"]["Url"] != "http://127.0.0.1:5191":
        raise ValueError("Human listener must stay private")
    api = endpoints["OrganizationHr"]
    if (api["Url"] != "https://0.0.0.0:5193" or api["Certificate"]["Path"] != "/tls/server.crt"
            or api["Certificate"]["KeyPath"] != "/tls/server.key"):
        raise ValueError("Dedicated TLS endpoint required")
    fabric, platform = value["Fabric"], value["Platform"]
    if (fabric["DataDirectory"] != "/state/web/fabric" or fabric["DataProtectionKeysPath"] != "/state/web/keys"
            or platform["RegistryPath"] != "/state/web/platform-registry.db"
            or platform["CaFile"] != "/tls/platform-ca.crt"
            or fabric.get("ImportLegacyOnStartup", False) is not False
            or platform.get("BootstrapSubject") not in (None, "")):
        raise ValueError("Preserved state paths without bootstrap required")
    if value["Organization"].get("RequireSubscription") is not True:
        raise ValueError("Subscription boundary required")
    if value["Organization"].get("MeterChanges", False) is not False:
        raise ValueError("Legacy metering is outside this candidate")
    # With closed egress, the candidate cannot enable payments or an unreviewed outbox.
    if (value.get("Payments", {}).get("Zarinpal", {}).get("Enabled", False) is not False
            or value.get("UsageMetering", {}).get("Outbox", {}).get("Enabled", False) is not False):
        raise ValueError("External integrations need their own transport review")
    for section, path in (("Organization", "/state/web/organization"), ("Accounting", "/state/web/accounting")):
        if value.get(section, {}).get("DataDirectory") != path:
            raise ValueError("Legacy state directories must be explicit")
    hosts = value["AllowedHosts"].split(";")
    if TLS_NAME not in hosts or "127.0.0.1" not in hosts or any(not name or "*" in name for name in hosts):
        raise ValueError("Bounded allowed hosts required")


def validate_sqlite(path: Path, role: str | None = None) -> None:
    with closing(sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True, timeout=2)) as db:
        if db.execute("PRAGMA quick_check").fetchall() != [("ok",)] or db.execute("PRAGMA foreign_key_check").fetchone():
            raise ValueError("Invalid preserved SQLite state")
        tables = {row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if not tables:
            raise ValueError("Schema required")
        if role is not None:
            if not TABLES[role].issubset(tables):
                raise ValueError("Existing application schema required")
            if role == "fabric" and db.execute("SELECT COUNT(*) FROM fabric_documents").fetchone()[0] == 0:
                raise ValueError("Retained fabric records required")
            if role == "registry" and db.execute("SELECT COUNT(*) FROM platform_products WHERE key='organization-fabric'").fetchone()[0] != 1:
                raise ValueError("Retained central HR product required")
            if role == "workbench" and (db.execute("PRAGMA user_version").fetchone()[0] != 1
                    or db.execute("SELECT COUNT(*) FROM state_metadata WHERE singleton=1").fetchone()[0] != 1
                    or db.execute("SELECT COUNT(*) FROM trusted_context WHERE singleton=1").fetchone()[0] != 1):
                raise ValueError("Retained native state and schema version required")


def check_database(path: Path, copy_snapshot: bool = False, role: str | None = None) -> None:
    if not path.is_file() or path.is_symlink() or path.stat().st_size < 100:
        raise ValueError("Existing state database required")
    if not copy_snapshot:
        validate_sqlite(path, role)
        return
    # A restored WAL-mode DB may need fresh WAL/SHM files even for mode=ro.
    # Init reads a stopped/coherent state set into private bounded scratch. It
    # never marks a live DB immutable or creates sidecars on the retained PVC.
    files = [item for item in (path, Path(str(path) + "-wal"), Path(str(path) + "-shm")) if item.exists()]
    signatures = {}
    for item in files:
        if not item.is_file() or item.is_symlink():
            raise ValueError("Regular state snapshot files required")
        info = item.stat()
        signatures[item] = (info.st_size, info.st_mtime_ns, info.st_ino)
    if sum(info[0] for info in signatures.values()) > MAX_STATE_SNAPSHOT:
        raise ValueError("Development state snapshot exceeds scratch bound")
    with tempfile.TemporaryDirectory(prefix="hr-state-preflight-") as folder:
        for item in files:
            destination = Path(folder) / item.name
            with item.open("rb") as incoming, destination.open("xb") as outgoing:
                copied = 0
                while chunk := incoming.read(1024 * 1024):
                    copied += len(chunk)
                    if copied > signatures[item][0]:
                        raise ValueError("State changed during preflight")
                    outgoing.write(chunk)
                if copied != signatures[item][0]:
                    raise ValueError("State changed during preflight")
        for item, signature in signatures.items():
            info = item.stat()
            if (info.st_size, info.st_mtime_ns, info.st_ino) != signature:
                raise ValueError("State must be quiescent during preflight")
        if [item for item in (path, Path(str(path) + "-wal"), Path(str(path) + "-shm")) if item.exists()] != files:
            raise ValueError("State sidecars changed during preflight")
        validate_sqlite(Path(folder) / path.name, role)


def check_keys(directory: Path, state_root: Path) -> None:
    if directory.is_symlink() or any(parent.is_symlink() for parent in directory.parents):
        raise ValueError("Linked state directories are not accepted")
    directory.resolve().relative_to(state_root.resolve())
    keys = list(directory.glob("key-*.xml"))
    if not keys or len(keys) > 100:
        raise ValueError("Bounded retained key set required")
    identifiers = set()
    for path in keys:
        if not path.is_file() or path.is_symlink():
            raise ValueError("Regular retained key file required")
        path.resolve().relative_to(state_root.resolve())
        with path.open("rb") as stream:
            raw = stream.read(MAX_JSON + 1)
        if not raw or len(raw) > MAX_JSON or b"<!DOCTYPE" in raw.upper() or b"<!ENTITY" in raw.upper():
            raise ValueError("Invalid key XML")
        element = ET.fromstring(raw)
        if (element.tag != "key" or element.get("version") != "1" or str(uuid.UUID(element.get("id", ""))) != element.get("id")
                or any(element.find(name) is None for name in ("creationDate", "activationDate", "expirationDate", "descriptor"))):
            raise ValueError("Data-protection key structure required")
        if element.get("id") in identifiers:
            raise ValueError("Duplicate data-protection key ID")
        identifiers.add(element.get("id"))
        dates = [datetime.fromisoformat(element.find(name).text.replace("Z", "+00:00"))
                 for name in ("creationDate", "activationDate", "expirationDate")]
        if any(date.tzinfo is None for date in dates) or dates[2] <= dates[1]:
            raise ValueError("Valid retained key dates required")
        descriptor = element.find("descriptor")
        expected_type = "Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.AuthenticatedEncryptorDescriptorDeserializer"
        deserializer = descriptor.get("deserializerType", "").split(",")
        if (len(deserializer) < 2 or deserializer[0] != expected_type
                or deserializer[1].strip() != "Microsoft.AspNetCore.DataProtection"
                or len(list(descriptor)) != 1 or descriptor[0].tag != "descriptor"):
            raise ValueError("Supported Linux data-protection descriptor required")
        material = descriptor.find("descriptor")
        if material is None:
            raise ValueError("Key material required")
        encryption, validation, master = material.find("encryption"), material.find("validation"), material.find("masterKey/value")
        if (encryption is None or encryption.get("algorithm") != "AES_256_CBC"
                or validation is None or validation.get("algorithm") != "HMACSHA256" or master is None
                or not 32 <= len(base64.b64decode(master.text or "", validate=True)) <= 128):
            raise ValueError("Supported retained key material required")


def check_state(include_workbench: bool) -> None:
    paths = [(Path("/state/web/fabric/fabric.db"), "fabric"), (Path("/state/web/platform-registry.db"), "registry")]
    if include_workbench:
        paths.append((Path("/state/workbench/workbench.db"), "workbench"))
    for path, role in paths:
        path.resolve().relative_to(Path("/state/web" if role != "workbench" else "/state/workbench").resolve())
        check_database(path, copy_snapshot=include_workbench, role=role)
    check_keys(Path("/state/web/keys"), Path("/state/web"))


def check_tls_files() -> None:
    for name in ("server.crt", "server.key", "ca.crt", "platform-ca.crt", "ca-bundle.crt"):
        path = Path("/tls") / name
        if not path.is_file() or not 0 < path.stat().st_size <= MAX_JSON:
            raise ValueError("Bounded external TLS material required")
        path.resolve().relative_to(Path("/tls").resolve())
        with path.open("rb") as stream:
            if not stream.read(1):
                raise ValueError("Readable TLS material required")
    if os.environ.get("SSL_CERT_FILE") != "/tls/ca-bundle.crt":
        raise ValueError("Explicit OIDC trust bundle required")
    ssl.create_default_context(cafile="/tls/ca.crt")
    ssl.create_default_context(cafile="/tls/platform-ca.crt")
    ssl.create_default_context(cafile="/tls/ca-bundle.crt")


def response_ok(connection, host: str) -> None:
    try:
        connection.request("GET", "/health/live", headers={"Host": host})
        response = connection.getresponse()
        raw = response.read(MAX_JSON + 1)
        if response.status != 200 or len(raw) > MAX_JSON or json.loads(raw).get("status") != "Healthy":
            raise ValueError("Listener not healthy")
    finally:
        connection.close()


def check_live() -> None:
    response_ok(http.client.HTTPConnection("127.0.0.1", 5191, timeout=3), TLS_NAME)
    context = ssl.create_default_context(cafile="/tls/ca.crt")
    # Connect locally while validating the real certificate's canonical upstream name.
    connection = http.client.HTTPConnection("127.0.0.1", 5193, timeout=3)
    connection.connect()
    try:
        connection.sock = context.wrap_socket(connection.sock, server_hostname=TLS_NAME)
        response_ok(connection, TLS_NAME)
    finally:
        connection.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--expected-config-sha256", required=True)
    parser.add_argument("--check-live", action="store_true")
    args = parser.parse_args()
    try:
        validate_config(read_config(args.config, args.expected_config_sha256))
        check_tls_files()
        check_state(include_workbench=not args.check_live)
        if args.check_live:
            check_live()
    except Exception:
        print("HR candidate configuration or preserved state is not ready.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
