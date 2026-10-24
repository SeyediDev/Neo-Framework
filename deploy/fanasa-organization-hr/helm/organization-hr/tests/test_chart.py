#!/usr/bin/env python3
"""Render-only acceptance of the HR chart. No kubeconfig, installation or network use."""
from __future__ import annotations
import argparse
import copy
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

import yaml

CHART = Path(__file__).resolve().parents[1]
NAMESPACE = "fanasa-organization-hr-development"
HELM = "helm"


def candidate():
    # Synthetic references and hashes; not a deployment binding or accepted artifact.
    values = yaml.safe_load((CHART / "values.yaml").read_text(encoding="utf-8"))
    values.update(targetNamespace=NAMESPACE,
        applicationId="aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa",
        tenantId="bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb",
        sourceCommit="a" * 40, release="hr-development-aaaaaaaaaaaa-bbbbbbbbbbbb",
        bundleSha256="b" * 64, configurationSha256="c" * 64)
    values["image"]["web"].update(repository="registry.example.invalid/fanasa/organization-hr-web", digest="sha256:" + "d" * 64)
    values["image"]["workbench"].update(repository="registry.example.invalid/fanasa/organization-hr-workbench", digest="sha256:" + "e" * 64)
    values["persistence"]["existingClaim"] = "synthetic-state"
    values["configuration"]["existingSecret"] = "synthetic-runtime-config"
    values["tls"]["existingSecret"] = "synthetic-upstream-tls"
    values["network"]["gateway"] = {"namespace": "synthetic-gateway", "podLabels": {"app.kubernetes.io/name": "gateway"}}
    values["network"]["dns"] = {"namespace": "kube-system", "podLabels": {"k8s-app": "kube-dns"}}
    values["network"]["identity"]["cidr"] = "192.0.2.10/32"
    return values


def relay_candidate():
    values = candidate()
    values["gatewayRelay"] = {"enabled": True, "nodeSourceCidr": "192.0.2.20/32"}
    values["network"]["gateway"] = {"namespace": NAMESPACE, "podLabels": {
        "app.kubernetes.io/name": "organization-hr-gateway-relay",
        "app.kubernetes.io/instance": "hr-candidate"}}
    return values


def run_helm(values=None, namespace=NAMESPACE, lint=False, extra=()):
    with tempfile.TemporaryDirectory(prefix="chart-", dir=CHART / ".runs") as directory:
        command = [HELM, "lint", "--strict", str(CHART)] if lint else [HELM, "template", "hr-candidate", str(CHART)]
        command += ["--namespace", namespace]
        if values is not None:
            path = Path(directory) / "values.json"
            path.write_text(json.dumps(values), encoding="utf-8")
            command += ["--values", str(path)]
        command += list(extra)
        return subprocess.run(command, capture_output=True, text=True, timeout=90)


class ChartAcceptance(unittest.TestCase):
    def render(self, values=None):
        result = run_helm(candidate() if values is None else values)
        self.assertEqual(result.returncode, 0, result.stderr)
        documents = list(yaml.safe_load_all(result.stdout))
        self.assertEqual({r["kind"] for r in documents}, {"Deployment", "Service", "NetworkPolicy"})
        self.assertEqual(len(documents), 3)
        return {r["kind"]: r for r in documents}

    def test_strict_lint_and_render(self):
        result = run_helm(candidate(), lint=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        documents = self.render()
        for resource in documents.values():
            self.assertEqual(resource["metadata"]["namespace"], NAMESPACE)
            self.assertEqual(resource["metadata"]["labels"]["fanasa.net/environment"], "development")

    def test_two_processes_share_network_and_pinned_role_images(self):
        deployment = self.render()["Deployment"]
        self.assertEqual(deployment["spec"]["replicas"], 1)
        self.assertEqual(deployment["spec"]["strategy"], {"type": "Recreate"})
        pod = deployment["spec"]["template"]["spec"]
        containers = pod["containers"]
        self.assertEqual([c["name"] for c in containers], ["web", "workbench"])
        self.assertTrue(containers[0]["image"].endswith("@sha256:" + "d" * 64))
        self.assertTrue(containers[1]["image"].endswith("@sha256:" + "e" * 64))
        self.assertEqual(pod["initContainers"][0]["image"], containers[0]["image"])
        self.assertEqual(containers[0]["command"], ["dotnet", "/app/web/Fanasa.AccessManagement.Web.dll"])
        self.assertEqual(containers[1]["command"], ["dotnet", "/app/workbench/Fanasa.ContractPolicy.Workbench.dll"])
        self.assertNotIn("ports", containers[1])
        self.assertFalse(pod["automountServiceAccountToken"])
        self.assertFalse(pod["enableServiceLinks"])
        self.assertNotIn("hostNetwork", pod)
        self.assertNotIn("fsGroup", pod["securityContext"])
        self.assertEqual(pod["securityContext"]["runAsUser"], 1654)
        for container in containers + pod["initContainers"]:
            security = container["securityContext"]
            self.assertTrue(security["readOnlyRootFilesystem"])
            self.assertFalse(security["allowPrivilegeEscalation"])
            self.assertEqual(security["capabilities"]["drop"], ["ALL"])

    def test_retained_state_is_checked_before_start_and_probes_do_not_require_a_permit(self):
        pod = self.render()["Deployment"]["spec"]["template"]["spec"]
        self.assertEqual(len(pod["initContainers"]), 1)
        init = pod["initContainers"][0]
        config_command = ["python3", "/adapter/runtime_config.py", "--config", "/app/web/appsettings.Development.json", "--expected-config-sha256", "c" * 64]
        self.assertEqual(init["command"], config_command)
        for mount in init["volumeMounts"]:
            if mount["name"] != "init-temporary":
                self.assertTrue(mount["readOnly"])
        self.assertEqual(next(m for m in init["volumeMounts"] if m["name"] == "init-temporary"), {"name": "init-temporary", "mountPath": "/tmp"})
        volumes = {v["name"]: v for v in pod["volumes"]}
        self.assertEqual(volumes["init-temporary"]["emptyDir"], {"sizeLimit": "128Mi"})
        self.assertEqual(init["resources"]["limits"]["ephemeral-storage"], "160Mi")
        for container in pod["containers"]:
            self.assertNotIn("init-temporary", {m["name"] for m in container["volumeMounts"]})
        for probe in ("startupProbe", "readinessProbe"):
            self.assertEqual(pod["containers"][0][probe]["exec"]["command"], config_command + ["--check-live"])
            self.assertEqual(pod["containers"][1][probe]["exec"]["command"], ["python3", "/adapter/health.py", "workbench"])
        self.assertEqual(pod["containers"][0]["livenessProbe"]["exec"]["command"], ["python3", "/adapter/health.py", "web"])
        self.assertNotIn("livenessProbe", pod["containers"][1])

    def test_existing_state_and_read_only_external_configuration(self):
        pod = self.render()["Deployment"]["spec"]["template"]["spec"]
        volumes = {v["name"]: v for v in pod["volumes"]}
        self.assertEqual(volumes["persistent-state"]["persistentVolumeClaim"]["claimName"], "synthetic-state")
        web, native = pod["containers"]
        web_state = next(v for v in web["volumeMounts"] if v["name"] == "persistent-state")
        native_state = next(v for v in native["volumeMounts"] if v["name"] == "persistent-state")
        self.assertEqual((web_state["mountPath"], web_state["subPath"]), ("/state/web", "web"))
        self.assertEqual((native_state["mountPath"], native_state["subPath"]), ("/state/workbench", "workbench"))
        self.assertEqual({v["name"] for v in native["volumeMounts"]}, {"persistent-state", "workbench-temporary"})
        self.assertEqual(volumes["configuration"]["secret"]["secretName"], "synthetic-runtime-config")
        self.assertEqual(volumes["configuration"]["secret"]["items"], [
            {"key": "appsettings.Development.json", "path": "appsettings.Development.json"},
            {"key": "deployment.json", "path": "deployment.json"}])
        for mount in web["volumeMounts"]:
            if mount["name"] in {"configuration", "upstream-tls"}:
                self.assertTrue(mount["readOnly"])
        env = {v["name"]: v["value"] for v in web["env"]}
        self.assertEqual(env["SSL_CERT_FILE"], "/tls/ca-bundle.crt")
        self.assertFalse(any("hostPath" in v for v in pod["volumes"]))

    def test_only_gateway_can_reach_only_hr_tls(self):
        resources = self.render()
        service = resources["Service"]["spec"]
        self.assertEqual(service["type"], "ClusterIP")
        self.assertEqual(service["ports"], [{"name": "hr-tls", "protocol": "TCP", "port": 5193, "targetPort": "hr-tls"}])
        policy = resources["NetworkPolicy"]["spec"]
        self.assertEqual(policy["policyTypes"], ["Ingress", "Egress"])
        self.assertEqual(len(policy["ingress"]), 1)
        ingress = policy["ingress"][0]
        self.assertEqual(ingress["ports"], [{"protocol": "TCP", "port": 5193}])
        self.assertEqual(ingress["from"], [{
            "namespaceSelector": {"matchLabels": {"kubernetes.io/metadata.name": "synthetic-gateway"}},
            "podSelector": {"matchLabels": {"app.kubernetes.io/name": "gateway"}}}])
        self.assertEqual(len(policy["egress"]), 2)
        dns, identity = policy["egress"]
        self.assertEqual(dns["to"][0]["podSelector"]["matchLabels"], {"k8s-app": "kube-dns"})
        self.assertEqual(dns["ports"], [{"protocol": "UDP", "port": 53}, {"protocol": "TCP", "port": 53}])
        self.assertEqual(identity, {"to": [{"ipBlock": {"cidr": "192.0.2.10/32"}}], "ports": [{"protocol": "TCP", "port": 443}]})

    def test_optional_private_image_pull_uses_external_secret_only(self):
        values = candidate()
        values["image"]["existingPullSecret"] = "synthetic-image-pull"
        pod = self.render(values)["Deployment"]["spec"]["template"]["spec"]
        self.assertEqual(pod["imagePullSecrets"], [{"name": "synthetic-image-pull"}])

    def relay_resources(self, values=None):
        result = run_helm(relay_candidate() if values is None else values)
        self.assertEqual(result.returncode, 0, result.stderr)
        documents = list(yaml.safe_load_all(result.stdout))
        self.assertEqual(len(documents), 7)
        self.assertEqual(sorted(r["kind"] for r in documents), [
            "ConfigMap", "Deployment", "Deployment", "NetworkPolicy", "NetworkPolicy", "Service", "Service"])
        self.assertTrue(all(r["metadata"]["namespace"] == NAMESPACE for r in documents))
        return {(r["kind"], r["metadata"]["name"]): r for r in documents}

    def test_relay_policy_binds_exact_hr_and_gateway_pod_peers(self):
        values = relay_candidate()
        lint = run_helm(values, lint=True)
        self.assertEqual(lint.returncode, 0, lint.stdout + lint.stderr)
        resources = self.relay_resources(values)
        relay_name = "hr-candidate-hr-gateway"
        relay_deployment = resources["Deployment", relay_name]
        hr_deployment = next(r for (kind, name), r in resources.items() if kind == "Deployment" and name != relay_name)
        relay_selector = relay_deployment["spec"]["selector"]["matchLabels"]
        hr_selector = hr_deployment["spec"]["selector"]["matchLabels"]
        hr_policy = next(r["spec"] for (kind, name), r in resources.items() if kind == "NetworkPolicy" and name != relay_name)
        self.assertEqual(hr_policy["ingress"], [{"from": [{
            "namespaceSelector": {"matchLabels": {"kubernetes.io/metadata.name": NAMESPACE}},
            "podSelector": {"matchLabels": relay_selector}}], "ports": [{"protocol": "TCP", "port": 5193}]}])
        policy = resources["NetworkPolicy", relay_name]["spec"]
        self.assertEqual(policy["podSelector"], {"matchLabels": relay_selector})
        self.assertEqual(policy["policyTypes"], ["Ingress", "Egress"])
        self.assertEqual(policy["ingress"], [{"from": [{"ipBlock": {"cidr": "192.0.2.20/32"}}],
                                              "ports": [{"protocol": "TCP", "port": 5193}]}])
        self.assertEqual(policy["egress"], [
            {"to": [{"namespaceSelector": {"matchLabels": {"kubernetes.io/metadata.name": NAMESPACE}},
                     "podSelector": {"matchLabels": hr_selector}}], "ports": [{"protocol": "TCP", "port": 5193}]},
            {"to": [{"namespaceSelector": {"matchLabels": {"kubernetes.io/metadata.name": "kube-system"}},
                     "podSelector": {"matchLabels": {"k8s-app": "kube-dns"}}}],
             "ports": [{"protocol": "UDP", "port": 53}, {"protocol": "TCP", "port": 53}]}])
        for (kind, _), resource in resources.items():
            if kind == "Service":
                self.assertEqual(resource["spec"]["type"], "ClusterIP")
                self.assertEqual([p["port"] for p in resource["spec"]["ports"]], [5193])
                self.assertTrue(all("nodePort" not in p for p in resource["spec"]["ports"]))

    def test_relay_has_no_state_tls_key_or_service_account_token_mounts(self):
        values = relay_candidate()
        values["image"]["existingPullSecret"] = "synthetic-image-pull"
        resources = self.relay_resources(values)
        relay_name = "hr-candidate-hr-gateway"
        deployment = resources["Deployment", relay_name]
        template = deployment["spec"]["template"]
        pod = template["spec"]
        self.assertFalse(pod["automountServiceAccountToken"])
        self.assertFalse(pod["enableServiceLinks"])
        self.assertNotIn("hostNetwork", pod)
        self.assertNotIn("hostPID", pod)
        self.assertNotIn("hostIPC", pod)
        self.assertNotIn("initContainers", pod)
        self.assertEqual(pod["securityContext"], {"runAsNonRoot": True, "runAsUser": 1654,
            "runAsGroup": 1654, "seccompProfile": {"type": "RuntimeDefault"}})
        self.assertEqual(pod["imagePullSecrets"], [{"name": "synthetic-image-pull"}])
        self.assertEqual(pod["volumes"], [{"name": "relay", "configMap": {"name": relay_name, "defaultMode": 292}}])
        self.assertEqual(len(pod["containers"]), 1)
        container = pod["containers"][0]
        self.assertEqual(container["image"], values["image"]["web"]["repository"] + "@" + values["image"]["web"]["digest"])
        self.assertEqual(container["volumeMounts"], [{"name": "relay", "mountPath": "/relay", "readOnly": True}])
        self.assertNotIn("env", container)
        self.assertNotIn("envFrom", container)
        self.assertEqual(container["securityContext"], {"allowPrivilegeEscalation": False,
            "readOnlyRootFilesystem": True, "capabilities": {"drop": ["ALL"]}})
        hr_name = next(name for (kind, name) in resources if kind == "Deployment" and name != relay_name)
        self.assertEqual(container["command"], ["python3", "-B", "/relay/gateway-relay.py",
            hr_name + "." + NAMESPACE + ".svc.cluster.local"])
        script = resources["ConfigMap", relay_name]["data"]["gateway-relay.py"]
        self.assertEqual(template["metadata"]["annotations"]["fanasa.net/relay-sha256"],
                         hashlib.sha256(script.encode()).hexdigest())

    def test_relay_rejects_broad_sources_and_mismatched_peers(self):
        cases = [
            ("missing node", ("gatewayRelay", "nodeSourceCidr"), ""),
            ("broad node", ("gatewayRelay", "nodeSourceCidr"), "0.0.0.0/0"),
            ("node subnet", ("gatewayRelay", "nodeSourceCidr"), "192.0.2.0/24"),
            ("IPv6 node", ("gatewayRelay", "nodeSourceCidr"), "2001:db8::1/128"),
            ("invalid node", ("gatewayRelay", "nodeSourceCidr"), "256.1.1.1/32"),
            ("CIDR injection", ("gatewayRelay", "nodeSourceCidr"), "192.0.2.20/32\nexcept: []"),
            ("coerced relay toggle", ("gatewayRelay", "enabled"), "true"),
            ("other namespace", ("network", "gateway", "namespace"), "synthetic-gateway"),
            ("unbounded peer", ("network", "gateway", "podLabels"), {}),
            ("missing release peer", ("network", "gateway", "podLabels"), {"app.kubernetes.io/name": "organization-hr-gateway-relay"}),
            ("different release peer", ("network", "gateway", "podLabels"), {"app.kubernetes.io/name": "organization-hr-gateway-relay", "app.kubernetes.io/instance": "another-release"}),
            ("different app peer", ("network", "gateway", "podLabels"), {"app.kubernetes.io/name": "gateway", "app.kubernetes.io/instance": "hr-candidate"}),
            ("inject TLS mount", ("gatewayRelay", "tlsSecret"), "synthetic-upstream-tls"),
            ("inject source state", ("gatewayRelay", "existingClaim"), "synthetic-state"),
            ("inject command", ("gatewayRelay", "command"), ["sh", "-c", "true"]),
        ]
        for label, path, bad_value in cases:
            with self.subTest(case=label):
                values = relay_candidate()
                target = values
                for key in path[:-1]:
                    target = target[key]
                target[path[-1]] = bad_value
                result = run_helm(values)
                self.assertNotEqual(result.returncode, 0, label + " unexpectedly rendered")
                self.assertFalse(result.stdout.strip(), label + " emitted deployable resources")

    def test_empty_defaults_fail_closed(self):
        self.assertNotEqual(run_helm().returncode, 0)

    def test_invalid_and_injectable_values_are_rejected(self):
        cases = [
            ("mutable web repository", ("image", "web", "repository"), "registry.example.invalid/fanasa/hr:latest"),
            ("mutable web digest", ("image", "web", "digest"), "latest"),
            ("mutable workbench digest", ("image", "workbench", "digest"), "latest"),
            ("missing claim", ("persistence", "existingClaim"), ""),
            ("missing config", ("configuration", "existingSecret"), ""),
            ("missing tls", ("tls", "existingSecret"), ""),
            ("path traversal", ("persistence", "webSubPath"), "../other"),
            ("same state paths", ("persistence", "webSubPath"), "workbench"),
            ("same config keys", ("configuration", "metadataKey"), "appsettings.Development.json"),
            ("unbounded gateway", ("network", "gateway", "podLabels"), {}),
            ("unbounded dns", ("network", "dns", "podLabels"), {}),
            ("invalid label", ("network", "gateway", "podLabels"), {"app": "all\nkind: Pod"}),
            ("broad identity egress", ("network", "identity", "cidr"), "0.0.0.0/0"),
            ("invalid identity IP", ("network", "identity", "cidr"), "999.1.1.1/32"),
            ("protected namespace", ("targetNamespace",), "tenant-fanasa-production"),
            ("invalid source binding", ("sourceCommit",), "e" * 40),
            ("unbounded resources", ("resources", "web", "memoryMiB"), 4096),
            ("coerced resources", ("resources", "web", "cpuMillicores"), "100"),
            ("arbitrary command", ("command",), ["sh", "-c", "true"]),
            ("arbitrary environment", ("env",), {"ASPNETCORE_URLS": "http://0.0.0.0:4187"}),
            ("extra raw resources", ("extraObjects",), [{"kind": "ClusterRole"}]),
            ("workbench service", ("service",), {"port": 4187}),
        ]
        for label, path, bad_value in cases:
            with self.subTest(case=label):
                values = copy.deepcopy(candidate())
                target = values
                for key in path[:-1]:
                    target = target[key]
                target[path[-1]] = bad_value
                result = run_helm(values)
                self.assertNotEqual(result.returncode, 0, label + " unexpectedly rendered")
                self.assertFalse(result.stdout.strip(), label + " emitted deployable resources")

    def test_caller_cannot_render_in_another_namespace(self):
        result = run_helm(candidate(), namespace="tenant-fanasa-production")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(result.stdout.strip())


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--helm", default=shutil.which("helm"))
    args, remaining = parser.parse_known_args()
    if not args.helm or not Path(args.helm).is_file():
        parser.error("A local Helm executable is required; pass --helm. No cluster actions are performed.")
    HELM = str(Path(args.helm).resolve())
    (CHART / ".runs").mkdir(exist_ok=True)
    unittest.main(argv=[__file__] + remaining, verbosity=2)
