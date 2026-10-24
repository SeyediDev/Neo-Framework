"""Deployment consistency regressions; synthetic plans only, no credentials/network."""
import copy
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import pip_preflight

HERE = Path(__file__).resolve().parent


class PreflightTests(unittest.TestCase):
    def plan(self):
        return json.loads((HERE / "plan.example.json").read_text(encoding="utf-8"))

    def rejected(self, plan):
        report = pip_preflight.validate_plan(plan)
        self.assertFalse(report["configurationValidated"])
        self.assertFalse(report["liveAccepted"])
        self.assertFalse(report["networkPerformed"])
        self.assertTrue(report["errors"])
        return report

    def test_consistent_plan_never_claims_live_acceptance(self):
        report = pip_preflight.validate_plan(self.plan())
        self.assertTrue(report["configurationValidated"], report)
        self.assertFalse(report["liveAccepted"])
        self.assertFalse(report["networkPerformed"])
        self.assertGreaterEqual(len(report["pendingChecks"]), 8)

    def test_provider_consumer_runtime_is_explicit_and_independent(self):
        for provider in ["systemd", "kubernetes"]:
            for consumer in ["systemd", "kubernetes"]:
                plan = self.plan()
                plan["provider"]["runtime"] = provider
                plan["consumer"]["runtime"] = consumer
                self.assertTrue(pip_preflight.validate_plan(plan)["configurationValidated"])
        plan["consumer"]["runtime"] = "automatically-inferred"
        self.rejected(plan)

    def test_contract_matches_authoritative_hr_projector(self):
        source = HERE.parents[2] / "products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/OrganizationHrAttributes.cs"
        declaration = re.search(r"PipAttributeDefinition\[\] Definitions => \[([\s\S]*?)\];", source.read_text(encoding="utf-8"))
        self.assertIsNotNone(declaration)
        fields = re.findall(r'new\("([^"]+)", "([^"]+)"(?:, (false|true))?\)', declaration[1])
        expected = [{"name": name, "dataType": kind, "required": required != "false", "externalName": name}
                    for name, kind, required in fields]
        self.assertEqual(self.plan()["source"]["definitions"], expected)

    def test_schema_rejects_omitted_position_and_changed_types(self):
        for index in range(6):
            plan = self.plan()
            plan["source"]["definitions"].pop(index)
            self.rejected(plan)
        for key, value in [("dataType", "integer"), ("required", False), ("externalName", "forged")]:
            plan = self.plan()
            plan["source"]["definitions"][0][key] = value
            self.rejected(plan)

    def test_tenant_client_scope_and_secret_correlations(self):
        changes = [
            (("consumer", "oauth", "tenantId"), "22222222-2222-4222-8222-222222222222"),
            (("consumer", "binding", "tenantId"), "22222222-2222-4222-8222-222222222222"),
            (("provider", "reader", "tenantIds"), ["22222222-2222-4222-8222-222222222222"]),
            (("consumer", "oauth", "clientId"), "another-reader"),
            (("consumer", "oauth", "scopes"), ["organization.hr.write"]),
            (("consumer", "binding", "secretReference"), "another-secret"),
            (("consumer", "secretMount", "reference"), "another-secret"),
            (("consumer", "secretMount", "environmentKey"), "Pip__Secrets__wrong"),
        ]
        for keys, value in changes:
            with self.subTest(keys=keys):
                plan = self.plan()
                target = plan
                for key in keys[:-1]: target = target[key]
                target[keys[-1]] = value
                self.rejected(plan)

    def test_issuer_and_gateway_path_are_exact(self):
        for endpoint in [
            "https://api.example.test/attributes",
            self.plan()["source"]["endpoint"].replace("11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222"),
            self.plan()["source"]["endpoint"] + "?token=DO_NOT_PRINT",
            self.plan()["source"]["endpoint"].replace("https://", "http://"),
            self.plan()["source"]["endpoint"].replace("https://", "https://user:DO_NOT_PRINT@"),
        ]:
            plan = self.plan()
            plan["source"]["endpoint"] = plan["consumer"]["binding"]["endpoint"] = endpoint
            plan["consumer"]["egressEndpoints"][1] = endpoint
            self.assertNotIn("DO_NOT_PRINT", json.dumps(self.rejected(plan)))
        plan = self.plan()
        plan["consumer"]["oauth"]["tokenEndpoint"] = "https://wrong.example.test/token"
        self.rejected(plan)

    def test_loopback_metadata_and_unapproved_private_literal_are_rejected(self):
        for host in ["127.0.0.1", "169.254.169.254", "[::1]", "10.30.0.10", "192.0.2.1", "0x7f.0.0.0x1", "[::1]suffix"]:
            plan = self.plan()
            endpoint = plan["source"]["endpoint"].replace("api.example.test", host)
            plan["source"]["endpoint"] = plan["consumer"]["binding"]["endpoint"] = endpoint
            plan["consumer"]["egressEndpoints"][1] = endpoint
            self.rejected(plan)
        plan = self.plan()
        endpoint = plan["source"]["endpoint"].replace("api.example.test", "10.20.0.11")
        plan["source"]["endpoint"] = plan["consumer"]["binding"]["endpoint"] = endpoint
        plan["consumer"]["egressEndpoints"][1] = endpoint
        self.assertTrue(pip_preflight.validate_plan(plan)["configurationValidated"])

    def test_egress_does_not_treat_identity_access_as_gateway_access(self):
        for destinations in [[], [self.plan()["consumer"]["egressEndpoints"][0]], ["https://wildcard.example.test"]]:
            plan = self.plan()
            plan["consumer"]["egressEndpoints"] = destinations
            self.rejected(plan)

    def test_explicit_timing_and_canonical_subjects(self):
        for name, value in [("schemaVersion", 1), ("schemaVersion", True), ("maxAgeSeconds", 0),
                            ("pollIntervalSeconds", 60), ("pollIntervalSeconds", 29), ("syncSubjects", []),
                            ("syncSubjects", ["person@example.test"])]:
            plan = self.plan()
            plan["source"][name] = value
            self.rejected(plan)
        plan = self.plan()
        plan["provider"]["reader"]["serviceSubject"] = "00000000-0000-0000-0000-000000000000"
        self.rejected(plan)

    def test_unknown_secret_values_and_relative_ca_are_rejected_without_echo(self):
        for section in ["provider", "consumer", "source"]:
            plan = self.plan()
            plan[section]["password"] = "DO_NOT_PRINT_SECRET"
            self.assertNotIn("DO_NOT_PRINT_SECRET", json.dumps(self.rejected(plan)))
        plan = self.plan()
        plan["consumer"]["oauth"]["caFile"] = "relative.crt"
        self.rejected(plan)

    def test_validation_has_no_network_or_file_access(self):
        plan = self.plan()
        with patch("socket.socket", side_effect=AssertionError("network forbidden")), patch("builtins.open", side_effect=AssertionError("file access forbidden")), patch.object(Path, "open", side_effect=AssertionError("file access forbidden")):
            report = pip_preflight.validate_plan(plan)
        self.assertTrue(report["configurationValidated"])

    def test_malformed_json_fields_never_escape_as_exceptions(self):
        original = self.plan()
        paths = []
        def walk(value, path=()):
            if isinstance(value, dict):
                for key, child in value.items():
                    paths.append(path + (key,))
                    walk(child, path + (key,))
        walk(original)
        for keys in paths:
            for replacement in [None, [], {}, [None], 1.5, True, ""]:
                plan = copy.deepcopy(original)
                target = plan
                for key in keys[:-1]: target = target[key]
                target[keys[-1]] = replacement
                with self.subTest(path=keys, replacement_type=type(replacement).__name__):
                    report = pip_preflight.validate_plan(plan)
                    self.assertFalse(report["liveAccepted"])
                    self.assertFalse(report["networkPerformed"])
                    self.assertIsInstance(report["errors"], list)

    def test_cli_bounds_duplicate_fields_and_sanitizes_parse_errors(self):
        for raw in ['{"provider":{},"Provider":{"password":"DO_NOT_PRINT_SECRET"}}',
                    '{"password":"DO_NOT_PRINT_SECRET", invalid', ' ' * (1024 * 1024 + 1),
                    '{"provider":NaN}', '[]']:
            with self.subTest(raw_length=len(raw)), tempfile.TemporaryDirectory(prefix="pip-preflight-test-") as temp:
                plan_path = Path(temp) / "plan.json"
                plan_path.write_text(raw, encoding="utf-8")
                process = subprocess.run([sys.executable, "-B", str(HERE / "pip_preflight.py"), "--plan", str(plan_path)], capture_output=True, text=True, timeout=15)
                self.assertEqual(process.returncode, 2, process.stdout)
                self.assertNotIn("DO_NOT_PRINT_SECRET", process.stdout + process.stderr)
                report = json.loads(process.stdout)
                self.assertFalse(report["liveAccepted"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
