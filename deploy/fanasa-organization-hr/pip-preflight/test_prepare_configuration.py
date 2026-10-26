import copy
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import prepare_configuration as prepare

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
WEB = REPO / "products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web"


class PreparationTests(unittest.TestCase):
    def plan(self):
        return json.loads((HERE / "plan.example.json").read_text(encoding="utf-8"))

    def prepared(self, plan=None):
        return prepare.prepare_configuration(plan or self.plan(), "منبع HR توسعه", "آزمون اتصال مجاز")

    def invoke(self, directory, raw, *extra):
        directory = Path(directory)
        (directory / "plan.json").write_text(raw, encoding="utf-8")
        return subprocess.run([sys.executable, "-B", str(HERE / "prepare_configuration.py"),
            "--plan", str(directory / "plan.json"), "--source-name", "HR source", "--reason", "Acceptance setup",
            "--output-directory", str(directory / "output"), *extra], capture_output=True, text=True)

    def test_provider_and_consumer_keep_separate_actual_binding_contracts(self):
        result = self.prepared()
        provider = result["provider.fragment.json"]
        self.assertEqual(set(provider), {"Organization"})
        self.assertEqual(set(provider["Organization"]), {"HrApi", "HrAttributes"})
        self.assertEqual(set(provider["Organization"]["HrApi"]), {"Authority", "Audience"})
        reader = provider["Organization"]["HrAttributes"]["Readers"][0]
        self.assertEqual(reader["TenantIds"], [self.plan()["source"]["tenantId"]])
        self.assertEqual(reader["Scope"], "organization.hr.attributes.read")
        consumer = result["consumer.fragment.json"]
        self.assertEqual(set(consumer), {"Pip"})
        self.assertEqual(set(consumer["Pip"]), {"OAuth", "Transport"})
        properties = re.findall(r"public \S+ (\w+) \{ get; set; \}",
            (WEB / "PipOAuth.cs").read_text(encoding="utf-8").split("public sealed class PipOAuthOptions")[0])
        self.assertEqual(set(consumer["Pip"]["OAuth"]["Profiles"][0]), set(properties))
        transport = (WEB / "PipTransportOptions.cs").read_text(encoding="utf-8").split("public sealed class PipEndpointBinding")[1].split("public interface")[0]
        properties = re.findall(r"public \S+ (\w+) \{ get; set; \}", transport)
        self.assertEqual(set(consumer["Pip"]["Transport"]["Bindings"][0]), set(properties))
        services = (WEB / "FabricServices.cs").read_text(encoding="utf-8")
        for key in ("Pip:OAuth", "Pip:Transport", "Organization:HrAttributes"):
            self.assertIn('GetSection("' + key + '")', services)

    def test_registration_matches_actual_dto_and_projector(self):
        body = self.prepared()["source.register.json"]
        dto = (WEB / "PipApi.cs").read_text(encoding="utf-8").split("record RegisterPipSourceCommand(")[1].split(");")[0]
        names = re.findall(r"(?:string\??|int|bool|PipAttributeDefinition\[\]\??|string\[\]\??)\s+(\w+)", dto)
        self.assertEqual(set(body), {name[0].lower() + name[1:] for name in names})
        projector = (WEB / "OrganizationHrAttributes.cs").read_text(encoding="utf-8")
        declaration = re.search(r"PipAttributeDefinition\[\] Definitions => \[([\s\S]*?)\];", projector)[1]
        expected = [{"name": name, "dataType": kind, "required": required != "false", "externalName": name}
            for name, kind, required in re.findall(r'new\("([^\"]+)", "([^\"]+)"(?:, (false|true))?\)', declaration)]
        self.assertEqual(body["definitions"], expected)
        self.assertFalse(body["enabled"])
        self.assertEqual((body["schemaVersion"], body["timeoutSeconds"], body["maxResponseBytes"]), (2, 10, 65536))

    def test_host_comes_from_exact_endpoint_with_dotnet_ipv6_brackets(self):
        for address, allowed, expected in [("api.example.test:8443", [], "api.example.test"),
                ("[fd00::1234]:8443", ["fd00::1234"], "[fd00::1234]")]:
            plan = self.plan()
            endpoint = plan["source"]["endpoint"].replace("api.example.test", address)
            plan["source"]["endpoint"] = plan["consumer"]["binding"]["endpoint"] = endpoint
            plan["consumer"]["binding"]["allowedPrivateAddresses"] = allowed
            plan["consumer"]["egressEndpoints"][1] = endpoint
            body = self.prepared(plan)["source.register.json"]
            self.assertEqual(body["allowedHost"], expected)
            self.assertEqual(body["endpoint"], endpoint)

    def test_no_secret_values_or_activation_or_grants_generated(self):
        result = self.prepared()
        keys = []
        def visit(value):
            if isinstance(value, dict):
                keys.extend(value)
                for item in value.values(): visit(item)
            elif isinstance(value, list):
                for item in value: visit(item)
        visit(result)
        self.assertFalse(set(keys) & {"Secrets", "ClientSecret", "Password", "Token", "Grants", "GrantPermissions"})
        metadata = result["preparation.json"]
        for key in ("liveAccepted", "networkPerformed", "installed", "sourceRegistered", "sourceActivated", "standaloneAppsettings"):
            self.assertFalse(metadata[key])
        self.assertTrue(metadata["mergeRequired"])
        self.assertEqual(metadata["secretInjection"], self.plan()["consumer"]["secretMount"])

    def test_pure_function_reads_no_external_files_or_network_and_does_not_mutate_plan(self):
        plan = self.plan()
        original = copy.deepcopy(plan)
        with patch("pathlib.Path.open", side_effect=AssertionError("unexpected file read")), \
             patch("socket.create_connection", side_effect=AssertionError("unexpected network")):
            result = self.prepared(plan)
        self.assertEqual(plan, original)
        result["source.register.json"]["definitions"][0]["name"] = "changed"
        self.assertEqual(plan, original)

    def test_rejects_unsupported_host_realm_even_if_preflight_accepts(self):
        plan = self.plan()
        plan["provider"]["hrAuthority"] = "https://sso.example.test/realms/other"
        plan["consumer"]["oauth"]["tokenEndpoint"] = plan["provider"]["hrAuthority"] + "/protocol/openid-connect/token"
        plan["consumer"]["egressEndpoints"][0] = plan["consumer"]["oauth"]["tokenEndpoint"]
        self.assertTrue(prepare.pip_preflight.validate_plan(plan)["configurationValidated"])
        with self.assertRaisesRegex(prepare.PreparationError, "provider.realm-not-supported"):
            self.prepared(plan)

    def test_label_validation_follows_dotnet_utf16_length_without_echo(self):
        for name, reason in [("", "reason"), ("a" * 201, "reason"), ("😀" * 101, "reason"),
                ("name", "a" * 1001), ("name", "bad\ntext"), ("\ud800", "reason")]:
            with self.assertRaisesRegex(prepare.PreparationError, "source.labels-invalid"):
                prepare.prepare_configuration(self.plan(), name, reason)

    def test_systemd_secret_key_rejects_hyphens_without_restricting_kubernetes(self):
        self.assertTrue(prepare.pip_preflight.validate_plan(self.plan())["configurationValidated"])
        self.assertEqual(self.prepared()["preparation.json"]["secretInjection"]["environmentKey"],
                         "Pip__Secrets__organization_hr_client_secret")
        plan = self.plan()
        plan["consumer"]["oauth"]["clientSecretReference"] = "organization-hr-client-secret"
        plan["consumer"]["secretMount"] = {"reference": "organization-hr-client-secret",
            "environmentKey": "Pip__Secrets__organization-hr-client-secret"}
        report = prepare.pip_preflight.validate_plan(plan)
        self.assertFalse(report["configurationValidated"])
        self.assertIn("secretMount.systemd-environment-key-invalid", report["errors"])
        with self.assertRaisesRegex(prepare.PreparationError, "plan.invalid"):
            self.prepared(plan)
        plan["consumer"]["runtime"] = "kubernetes"
        self.assertEqual(self.prepared(plan)["preparation.json"]["consumerRuntime"], "kubernetes")

    def test_secret_reference_cannot_change_aspnet_configuration_path(self):
        for runtime in ("systemd", "kubernetes"):
            plan = self.plan()
            plan["consumer"]["runtime"] = runtime
            plan["consumer"]["oauth"]["clientSecretReference"] = "organization__hr"
            plan["consumer"]["secretMount"] = {"reference": "organization__hr", "environmentKey": "Pip__Secrets__organization__hr"}
            report = prepare.pip_preflight.validate_plan(plan)
            self.assertIn("oauth.secret-reference-environment-separator", report["errors"])
            with self.assertRaisesRegex(prepare.PreparationError, "plan.invalid"):
                self.prepared(plan)

    def test_secret_reference_correlation_cannot_be_changed_independently(self):
        plan = self.plan()
        plan["consumer"]["secretMount"]["reference"] = "another_secret"
        with self.assertRaisesRegex(prepare.PreparationError, "plan.invalid"):
            self.prepared(plan)

    def test_invalid_plan_creates_no_output_and_does_not_leak_secret(self):
        plan = self.plan()
        plan["consumer"]["oauth"]["clientSecret"] = "DO_NOT_PRINT"
        with tempfile.TemporaryDirectory(prefix="test-configuration-", dir=HERE) as directory:
            process = self.invoke(directory, json.dumps(plan))
            self.assertEqual(process.returncode, 2)
            self.assertFalse((Path(directory) / "output").exists())
            self.assertNotIn("DO_NOT_PRINT", process.stdout + process.stderr)
            self.assertEqual(json.loads(process.stderr)["error"], "plan.invalid")

    def test_duplicate_fields_case_insensitive_and_invalid_json_create_no_output(self):
        for raw, code in [('{"provider":{},"Provider":{"secret":"DO_NOT_PRINT"}}', "input.duplicate-field"),
                ('{"provider":{"x":1,"X":"DO_NOT_PRINT"}}', "input.duplicate-field"),
                ('{"x":NaN}', "input.invalid-json"), ('{"secret":"DO_NOT_PRINT"', "input.invalid-json")]:
            with self.subTest(code=code), tempfile.TemporaryDirectory(prefix="test-configuration-", dir=HERE) as directory:
                process = self.invoke(directory, raw)
                self.assertEqual(process.returncode, 2)
                self.assertFalse((Path(directory) / "output").exists())
                self.assertNotIn("DO_NOT_PRINT", process.stdout + process.stderr)
                self.assertEqual(json.loads(process.stderr)["error"], code)

    def test_cli_writes_usable_files_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory(prefix="test-configuration-", dir=HERE) as directory:
            process = self.invoke(directory, json.dumps(self.plan()))
            self.assertEqual(process.returncode, 0, process.stderr)
            target = Path(directory) / "output"
            self.assertEqual({path.name for path in target.iterdir()}, set(self.prepared()))
            before = {path.name: path.read_bytes() for path in target.iterdir()}
            self.assertFalse(json.loads(before["source.register.json"])["enabled"])
            self.assertEqual(json.loads(before["source.register.json"])["name"], "HR source")
            process = self.invoke(directory, json.dumps(self.plan()))
            self.assertEqual(process.returncode, 2)
            self.assertEqual(before, {path.name: path.read_bytes() for path in target.iterdir()})

    def test_unreadable_arguments_and_oversize_input_are_sanitized(self):
        with tempfile.TemporaryDirectory(prefix="test-configuration-", dir=HERE) as directory:
            process = self.invoke(directory, " " * (prepare.pip_preflight.MAX_INPUT_BYTES + 1))
            self.assertEqual(process.returncode, 2)
            self.assertEqual(json.loads(process.stderr)["error"], "input.too-large")
            self.assertFalse((Path(directory) / "output").exists())
        process = subprocess.run([sys.executable, "-B", str(HERE / "prepare_configuration.py"), "--secret=DO_NOT_PRINT"],
                                 capture_output=True, text=True)
        self.assertEqual(process.returncode, 2)
        self.assertNotIn("DO_NOT_PRINT", process.stdout + process.stderr)


if __name__ == "__main__":
    unittest.main()
