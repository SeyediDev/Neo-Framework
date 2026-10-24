"""Boundary checks for the candidate's mounted configuration; no runtime credentials."""
import copy
from contextlib import closing
import importlib.util
import hashlib
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest

SOURCE = Path(__file__).resolve().parents[1] / "runtime_config.py"
spec = importlib.util.spec_from_file_location("hr_runtime_config", SOURCE)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def valid():
    return {
        "Authentication": {"Authority": module.ISSUER, "ClientId": "synthetic-web-client"},
        "Organization": {"DataDirectory": "/state/web/organization", "RequireSubscription": True, "HrApi": {
            "Enabled": True, "Authority": module.ISSUER, "Audience": "fanasa-organization-fabric",
            "DeploymentMetadataPath": "/runtime-config/deployment.json",
            "ClientId": "fanasa-organization-hr-development", "ServiceSubject": "11111111-1111-4111-8111-111111111111",
            "ReadScope": "organization.hr.read", "WriteScope": "organization.hr.write", "GatewayCertificateSha256": "a" * 64}},
        "Accounting": {"DataDirectory": "/state/web/accounting"},
        "Fabric": {"DataDirectory": "/state/web/fabric", "DataProtectionKeysPath": "/state/web/keys"},
        "Platform": {"RegistryPath": "/state/web/platform-registry.db", "CaFile": "/tls/platform-ca.crt"},
        "AllowedHosts": module.TLS_NAME + ";localhost;127.0.0.1",
        "Kestrel": {"Endpoints": {"Human": {"Url": "http://127.0.0.1:5191"}, "OrganizationHr": {
            "Url": "https://0.0.0.0:5193", "Certificate": {"Path": "/tls/server.crt", "KeyPath": "/tls/server.key"}}}}}


def valid_key_xml():
    return ('<key id="11111111-1111-4111-8111-111111111111" version="1">'
            '<creationDate>2026-10-13T00:00:00Z</creationDate><activationDate>2026-10-13T00:00:00Z</activationDate><expirationDate>2027-10-13T00:00:00Z</expirationDate>'
            '<descriptor deserializerType="Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.AuthenticatedEncryptorDescriptorDeserializer, Microsoft.AspNetCore.DataProtection">'
            '<descriptor><encryption algorithm="AES_256_CBC"/><validation algorithm="HMACSHA256"/><masterKey><value>'
            + module.base64.b64encode(b"synthetic" * 8).decode() + '</value></masterKey></descriptor></descriptor></key>')


class ConfigTests(unittest.TestCase):
    def test_valid_scoped_config(self):
        module.validate_config(valid())

    def test_config_rejects_relaxed_boundaries(self):
        cases = [(("Organization", "HrApi", "Enabled"), False), (("Organization", "HrApi", "Enabled"), "true"),
                 (("Organization", "RequireSubscription"), False), (("Platform", "BootstrapSubject"), "admin"),
                 (("Fabric", "ImportLegacyOnStartup"), True), (("Fabric", "DataDirectory"), "/app/data"),
                 (("Organization", "HrApi", "Audience"), "unrelated"), (("Organization", "HrApi", "ServiceSubject"), "0"),
                 (("Organization", "HrApi", "GatewayCertificateSha256"), "xyz"),
                 (("Authentication", "Authority"), "https://untrusted.invalid/realms/fanasa"),
                 (("AllowedHosts",), "*"), (("Kestrel", "Endpoints", "Human", "Url"), "http://0.0.0.0:5191")]
        cases.append((("AllowedHosts",), module.TLS_NAME + ";localhost"))
        for path, value in cases:
            with self.subTest(path=path, value=value):
                item = copy.deepcopy(valid())
                parent = item
                for name in path[:-1]:
                    parent = parent[name]
                parent[path[-1]] = value
                with self.assertRaises((ValueError, KeyError, TypeError)):
                    module.validate_config(item)

    def test_read_rejects_duplicates_case_ambiguity_flattening_and_oversize(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "config.json"
            for content in ['{"Platform":{},"platform":{}}', '{"A":1,"A":2}', '{"Organization:HrApi:Enabled":true}', " " * (module.MAX_JSON + 1)]:
                path.write_text(content)
                with self.assertRaises(ValueError):
                    module.read_config(path)

    def test_subscription_requires_explicit_value_in_development(self):
        item = valid()
        del item["Organization"]["RequireSubscription"]
        with self.assertRaises(ValueError):
            module.validate_config(item)

    def test_mount_must_match_rendered_config_hash(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "config.json"
            raw = json.dumps(valid()).encode()
            path.write_bytes(raw)
            expected = hashlib.sha256(raw).hexdigest()
            self.assertEqual(module.read_config(path, expected), valid())
            path.write_bytes(raw + b" ")
            with self.assertRaises(ValueError):
                module.read_config(path, expected)
            with self.assertRaises(ValueError):
                module.read_config(path, "not-a-sha256")

    def test_database_missing_empty_corrupt_and_fk_invalid_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.db"
            with self.assertRaises(ValueError):
                module.check_database(path)
            self.assertFalse(path.exists())
            path.touch()
            with self.assertRaises(ValueError):
                module.check_database(path)
            path.write_bytes(b"corrupt" * 100)
            with self.assertRaises(sqlite3.DatabaseError):
                module.check_database(path)
            path.unlink()
            with closing(sqlite3.connect(path)) as db:
                db.executescript("CREATE TABLE p(id INTEGER PRIMARY KEY); CREATE TABLE c(id INTEGER REFERENCES p(id)); INSERT INTO c VALUES(7);")
            with self.assertRaises(ValueError):
                module.check_database(path)

    def test_database_valid_read_only_unchanged(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.db"
            with closing(sqlite3.connect(path)) as db:
                db.executescript("CREATE TABLE state(id INTEGER PRIMARY KEY, value TEXT); INSERT INTO state VALUES(1,'synthetic');")
            before = path.read_bytes()
            module.check_database(path)
            self.assertEqual(before, path.read_bytes())

    def test_wal_snapshot_preserves_source_and_uses_private_scratch(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.db"
            with closing(sqlite3.connect(path)) as db:
                db.execute("PRAGMA journal_mode=WAL")
                db.executescript("CREATE TABLE state(id INTEGER PRIMARY KEY, value TEXT); INSERT INTO state VALUES(1,'synthetic');")
                db.commit()
                before = {p.name: p.read_bytes() for p in Path(folder).iterdir()}
                module.check_database(path, copy_snapshot=True)
                self.assertEqual(before, {p.name: p.read_bytes() for p in Path(folder).iterdir()})
            # The DB retains WAL journal mode after closing, with no sidecars.
            before = {p.name: p.read_bytes() for p in Path(folder).iterdir()}
            module.check_database(path, copy_snapshot=True)
            self.assertEqual(before, {p.name: p.read_bytes() for p in Path(folder).iterdir()})

    def test_unrelated_or_empty_role_schema_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.db"
            with closing(sqlite3.connect(path)) as db:
                db.executescript("CREATE TABLE unrelated(id INTEGER);")
            for role in module.TABLES:
                with self.subTest(role=role), self.assertRaises(ValueError):
                    module.check_database(path, copy_snapshot=True, role=role)
            with closing(sqlite3.connect(path)) as db:
                db.executescript("CREATE TABLE fabric_documents(kind TEXT,key TEXT,json TEXT); CREATE TABLE fabric_outbox(id INTEGER);")
            with self.assertRaises(ValueError):
                module.check_database(path, role="fabric")
            with closing(sqlite3.connect(path)) as db:
                db.execute("INSERT INTO fabric_documents VALUES('synthetic','1','{}')")
                db.commit()
            module.check_database(path, role="fabric")

    def test_key_xml_shape_entities_and_escape_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            directory = root / "keys"
            directory.mkdir()
            key = directory / "key-synthetic.xml"
            for raw in [b"junk", b"<key/>", b'<!DOCTYPE key><key/>', b'<key id="11111111-1111-4111-8111-111111111111" version="1"/>']:
                key.write_bytes(raw)
                with self.assertRaises((ValueError, module.ET.ParseError)):
                    module.check_keys(directory, root)
            incomplete = '<key id="11111111-1111-4111-8111-111111111111" version="1"><creationDate/><activationDate/><expirationDate/><descriptor/></key>'
            key.write_text(incomplete)
            with self.assertRaises((ValueError, AttributeError)):
                module.check_keys(directory, root)
            key.write_text(valid_key_xml())
            module.check_keys(directory, root)
            duplicate = directory / "key-duplicate.xml"
            duplicate.write_bytes(key.read_bytes())
            with self.assertRaises(ValueError):
                module.check_keys(directory, root)
            duplicate.unlink()
            with self.assertRaises(ValueError):
                module.check_keys(directory, directory / "unrelated")

    def test_key_descriptor_must_be_deserializable_by_dotnet(self):
        # .NET uses descriptor.Elements().Single() and resolves the declared
        # deserializer assembly. Metadata alone cannot preserve a usable key.
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            directory = root / "keys"
            directory.mkdir()
            path = directory / "key-synthetic.xml"
            for invalid in ("missing-child", "wrong-child", "extra-child", "wrong-assembly"):
                with self.subTest(invalid=invalid):
                    element = module.ET.fromstring(valid_key_xml())
                    descriptor = element.find("descriptor")
                    if invalid == "missing-child":
                        descriptor.remove(descriptor[0])
                    elif invalid == "wrong-child":
                        descriptor[0].tag = "unrelated"
                    elif invalid == "extra-child":
                        descriptor.append(module.ET.Element("unrelated"))
                    else:
                        descriptor.set("deserializerType", descriptor.get("deserializerType").replace(
                            ", Microsoft.AspNetCore.DataProtection", ", Unrelated.Assembly"))
                    path.write_bytes(module.ET.tostring(element))
                    before = path.read_bytes()
                    with self.assertRaises(ValueError):
                        module.check_keys(directory, root)
                    self.assertEqual(before, path.read_bytes())


if __name__ == "__main__":
    unittest.main()
