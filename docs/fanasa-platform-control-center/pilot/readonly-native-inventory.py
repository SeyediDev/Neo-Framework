"""Sanitized, bounded, read-only Linux inventory using existing CLIs and stdlib.

No installs, service mutations, HTTP clients, environment/key/config dumps, raw
command diagnostics, addresses, process identifiers, or listener owner names.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
from pathlib import Path
import re
import selectors
import shutil
import subprocess
import time

MIB, GIB = 1024 ** 2, 1024 ** 3
MAX_OUTPUT = 4 * MIB
KEYS = ("developer", "gitlab", "runner", "registry", "apisix", "otel", "collector")
VERSION = re.compile(r"(?<![A-Za-z0-9])v?\d+\.\d+(?:\.\d+)?(?:[-+][A-Za-z0-9.-]+)?")
NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]{0,252}$")
IMAGE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,400}(?:@sha256:[a-fA-F0-9]{64})?$")
QUANTITY = re.compile(r"^[+-]?\d+(?:\.\d+)?(?:[eE][+-]?\d+|[numkKMGTPE]|[KMGTPE]i)?$")


def safe_name(value):
    return value if isinstance(value, str) and NAME.fullmatch(value) else "redacted"


def safe_image(value):
    return value if isinstance(value, str) and IMAGE.fullmatch(value) else "redacted"


def safe_version(value):
    match = VERSION.search(value) if isinstance(value, str) else None
    return match.group(0)[:80] if match else None


def quantities(values):
    if not isinstance(values, dict):
        return {}
    return {safe_name(key): value for key, value in values.items()
            if isinstance(value, str) and len(value) <= 40 and QUANTITY.fullmatch(value)}


class ReadCommands:
    """Cap individual command time/output and total command time; hide stderr."""

    def __init__(self, seconds):
        self.deadline = time.monotonic() + seconds

    def run(self, arguments, *, seconds=12, input_text=None):
        remaining = min(seconds, self.deadline - time.monotonic())
        if remaining <= 0:
            return None, "deadline_expired"
        if shutil.which(arguments[0]) is None:
            return None, "not_found"
        try:
            process = subprocess.Popen(arguments, stdin=subprocess.PIPE if input_text is not None else subprocess.DEVNULL,
                                       stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        except OSError:
            return None, "unavailable"
        if input_text is not None:
            # The fixed probe is under 4KiB and cannot fill the stdin pipe.
            if len(input_text.encode()) > 4096:
                process.kill()
                process.wait(timeout=2)
                process.stdout.close()
                process.stdin.close()
                return None, "input_limit"
            try:
                process.stdin.write(input_text.encode())
                process.stdin.close()
            except BrokenPipeError:
                pass
        output, failure = bytearray(), None
        deadline = time.monotonic() + remaining
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            while selector.get_map():
                wait = deadline - time.monotonic()
                if wait <= 0:
                    failure = "timeout"
                    break
                for key, _ in selector.select(timeout=min(wait, 0.25)):
                    chunk = os.read(key.fileobj.fileno(), 65536)
                    if not chunk:
                        selector.unregister(key.fileobj)
                    elif len(output) + len(chunk) > MAX_OUTPUT:
                        failure = "output_limit"
                        break
                    else:
                        output.extend(chunk)
                if failure:
                    break
        if failure:
            process.kill()
        try:
            code = process.wait(timeout=max(0.1, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=2)
            failure, code = failure or "timeout", -1
        finally:
            process.stdout.close()
        if failure or code:
            return None, failure or "nonzero_exit"
        return output.decode("utf-8", errors="replace"), None

    def json(self, arguments, *, seconds=12, input_text=None):
        output, reason = self.run(arguments, seconds=seconds, input_text=input_text)
        if reason:
            return None, reason
        try:
            return json.loads(output), None
        except (TypeError, ValueError):
            return None, "invalid_json"


def compose_feature_probe(commands):
    # Config only: no pull, build, container, volume, network, or daemon mutation.
    source = """services:
  fixture:
    image: fixture.invalid/no-pull:readonly
    build: !reset null
    init: true
    cpus: 0.15
    cpu_shares: 128
    mem_limit: 256m
    memswap_limit: 256m
    pids_limit: 64
    logging:
      driver: json-file
      options: {max-size: "5m", max-file: "2"}
    networks: [isolated]
networks:
  isolated: {internal: true}
"""
    value, reason = commands.json(["docker", "compose", "--project-name", "fanasa-native-feature-probe",
                                   "-f", "-", "config", "--format", "json"], input_text=source)
    if reason:
        return {"supported": False, "status": reason}
    try:
        fixture = value["services"]["fixture"]
        supported = (int(fixture["mem_limit"]) == 256 * MIB
                     and int(fixture["memswap_limit"]) == 256 * MIB
                     and float(fixture["cpus"]) == 0.15
                     and int(fixture["cpu_shares"]) == 128
                     and int(fixture["pids_limit"]) == 64
                     and fixture["init"] is True
                     and fixture["logging"] == {"driver": "json-file",
                                                 "options": {"max-size": "5m", "max-file": "2"}}
                     and not fixture.get("build") and not fixture.get("ports")
                     and value["networks"]["isolated"]["internal"] is True)
    except (KeyError, TypeError, ValueError):
        supported = False
    return {"supported": supported, "status": "supported" if supported else "unsupported_model",
            "scope": "stdin compose config only; resource caps, internal network, no ports, build reset"}


def memory_inventory():
    wanted = ("MemTotal", "MemAvailable", "SwapTotal", "SwapFree")
    result = {}
    try:
        for line in Path("/proc/meminfo").read_text().splitlines():
            key, _, value = line.partition(":")
            if key in wanted:
                result[key + "Bytes"] = int(value.split()[0]) * 1024
        result["status"] = "available" if len(result) == len(wanted) else "incomplete"
    except (OSError, ValueError, IndexError):
        result["status"] = "unavailable"
    return result


def disk_inventory(path):
    try:
        disk = os.statvfs(path)
        return {"status": "available", "totalBytes": disk.f_blocks * disk.f_frsize,
                "availableBytes": disk.f_bavail * disk.f_frsize}
    except OSError:
        return {"status": "unavailable"}


def listener_inventory():
    result = {"tcpListeningPorts": [], "udpBoundPorts": [], "status": "available"}
    for protocol, files, states in (("tcp", ("tcp", "tcp6"), {"0A"}),
                                    ("udp", ("udp", "udp6"), {"07", "01"})):
        ports = set()
        for name in files:
            try:
                for line in Path("/proc/net/" + name).read_text().splitlines()[1:]:
                    columns = line.split()
                    if len(columns) > 3 and columns[3] in states:
                        port = int(columns[1].rsplit(":", 1)[1], 16)
                        if 0 < port <= 65535:
                            ports.add(port)
            except FileNotFoundError:
                pass  # IPv6 may be disabled.
            except (OSError, ValueError, IndexError):
                result["status"] = "incomplete"
        result["tcpListeningPorts" if protocol == "tcp" else "udpBoundPorts"] = sorted(ports)
    return result


def kube_items(commands, prefix, resource, *arguments):
    value, reason = commands.json(prefix + ["--request-timeout=8s", "get", resource,
                                           *arguments, "-o", "json"])
    if reason:
        return [], reason
    if not isinstance(value, dict) or not isinstance(value.get("items"), list):
        return [], "invalid_shape"
    return value["items"], None


def inventory(seconds=90):
    commands = ReadCommands(seconds)
    report = {"schemaVersion": "fanasa.native-host-inventory.v1",
              "observedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "readOnly": True, "maximumCommandBudgetSeconds": seconds,
              "cpuCount": os.cpu_count(), "loadAverage": list(os.getloadavg()),
              "memory": memory_inventory(), "rootDisk": disk_inventory("/"),
              "listeners": listener_inventory(), "versions": {}}
    output, reason = commands.run(["k3s", "--version"])
    report["versions"]["k3s"] = {"status": "unavailable" if reason else "available",
                                 "version": safe_version(output)}
    docker, docker_reason = commands.json(["docker", "version", "--format", "{{json .}}"])
    docker = docker if isinstance(docker, dict) else {}
    client, server = docker.get("Client", {}), docker.get("Server", {})
    client = client if isinstance(client, dict) else {}
    server = server if isinstance(server, dict) else {}
    report["versions"]["docker"] = {
        "status": "unavailable" if docker_reason else "available",
        "client": safe_version(client.get("Version")), "engine": safe_version(server.get("Version")),
        "clientApi": safe_version(client.get("ApiVersion")),
        "engineApi": safe_version(server.get("ApiVersion")),
        "engineMinimumApi": safe_version(server.get("MinAPIVersion"))}
    output, compose_reason = commands.run(["docker", "compose", "version", "--short"])
    compose_version = safe_version(output)
    report["versions"]["compose"] = {"status": "unavailable" if compose_reason else "available",
                                     "version": compose_version}
    report["composeFeatureProbe"] = compose_feature_probe(commands)
    data_root, reason = commands.json(["docker", "info", "--format", "{{json .DockerRootDir}}"])
    report["dockerStorageDisk"] = (disk_inventory(data_root)
                                   if not reason and isinstance(data_root, str) and data_root.startswith("/")
                                   else {"status": "unavailable"})
    kube = (["k3s", "kubectl"] if shutil.which("k3s")
            else ["kubectl", "--kubeconfig=/etc/rancher/k3s/k3s.yaml"])
    nodes, nodes_reason = kube_items(commands, kube, "nodes")
    report["k3sNodes"] = {"status": "unavailable" if nodes_reason else "available", "items": [
        {"name": safe_name(item.get("metadata", {}).get("name")),
         "ready": next((condition.get("status") if condition.get("status") in ("True", "False", "Unknown")
                        else "Unknown" for condition in item.get("status", {}).get("conditions", [])
                        if condition.get("type") == "Ready"), "Unknown")}
        for item in nodes if isinstance(item, dict)]}
    deployments, deployments_reason = kube_items(commands, kube, "deployments", "-A")
    selected = []
    for item in deployments:
        if not isinstance(item, dict):
            continue
        metadata = item.get("metadata", {})
        name = metadata.get("name", "")
        if not isinstance(name, str) or not any(key in name.lower() for key in KEYS):
            continue
        spec, status = item.get("spec", {}), item.get("status", {})
        selected.append({"namespace": safe_name(metadata.get("namespace")), "name": safe_name(name),
                         "desired": int(spec.get("replicas", 1)), "ready": int(status.get("readyReplicas", 0)),
                         "updated": int(status.get("updatedReplicas", 0)),
                         "available": int(status.get("availableReplicas", 0)),
                         "images": [safe_image(container.get("image"))
                                    for container in spec.get("template", {}).get("spec", {}).get("containers", [])]})
    report["selectedDeployments"] = {"status": "unavailable" if deployments_reason else "available",
                                      "selection": list(KEYS), "items": selected}
    quotas, quotas_reason = kube_items(commands, kube, "resourcequotas", "-n", "tenant-fanasa")
    report["tenantQuotas"] = {"namespace": "tenant-fanasa",
                             "status": "unavailable" if quotas_reason else "available", "items": [
        {"name": safe_name(item.get("metadata", {}).get("name")),
         "hard": quantities(item.get("status", {}).get("hard", {})),
         "used": quantities(item.get("status", {}).get("used", {}))}
        for item in quotas if isinstance(item, dict)]}
    output, reason = commands.run(["systemctl", "is-active", "k3s"], seconds=5)
    report["k3sService"] = "active" if not reason and output.strip() == "active" else "unavailable_or_inactive"
    report["existingDaemonUnits"] = {}
    for unit in ("docker", "containerd"):
        output, reason = commands.run(["systemctl", "show", unit, "--property=ActiveState", "--value"], seconds=5)
        state = output.strip() if not reason else None
        report["existingDaemonUnits"][unit] = state if state in ("active", "inactive", "failed", "activating", "deactivating") else "unavailable"
    nodes_ok = bool(report["k3sNodes"]["items"]) and not nodes_reason and all(
        item["ready"] == "True" for item in report["k3sNodes"]["items"])
    deployments_ok = not deployments_reason and all(
        item["ready"] >= item["desired"] and item["updated"] >= item["desired"]
        and item["available"] >= item["desired"] for item in selected)
    gates = {
        "memoryAtLeast2GiBAvailable": report["memory"].get("MemAvailableBytes", 0) >= 2 * GIB,
        "rootDiskAtLeast4GiBAvailable": report["rootDisk"].get("availableBytes", 0) >= 4 * GIB,
        "dockerStorageAtLeast4GiBAvailable": report["dockerStorageDisk"].get("availableBytes", 0) >= 4 * GIB,
        "existingDockerEngineAvailable": not docker_reason and bool(report["versions"]["docker"]["engine"]),
        "existingComposeFeaturesAvailable": report["composeFeatureProbe"]["supported"],
        "existingK3sNodesReady": nodes_ok, "selectedDeploymentsReady": deployments_ok,
        "existingK3sServiceActive": report["k3sService"] == "active",
    }
    report["preflightGates"] = gates
    report["eligibleForBoundedNativeRun"] = all(gates.values())
    report["inventoryLimit"] = ("Snapshot of selected deployments and numeric host ports only; not a complete "
                                "service-health, capacity, or software inventory. Kubernetes quotas do not cap Docker fixtures.")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--require-ready", action="store_true", help="Exit 2 when bounded-run prerequisites are unmet.")
    args = parser.parse_args()
    if os.name != "posix" or not Path("/proc/meminfo").is_file():
        print(json.dumps({"readOnly": True, "status": "unsupported_platform", "eligibleForBoundedNativeRun": False}))
        return 2
    try:
        report = inventory()
    except Exception as error:
        # Exception text and command diagnostics can contain sensitive data.
        report = {"readOnly": True, "status": "inventory_incomplete", "errorClass": type(error).__name__,
                  "eligibleForBoundedNativeRun": False}
    print(json.dumps(report, ensure_ascii=True, indent=2))
    return 2 if args.require_ready and not report["eligibleForBoundedNativeRun"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
