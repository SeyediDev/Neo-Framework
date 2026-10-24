#!/usr/bin/env python3
"""Operator-only verified HR bundle to local OCI image adapter; never push or deploy."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import uuid

DIRECTORY = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("hr_verified_bundle", DIRECTORY.parent / "verify_bundle.py")
BUNDLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BUNDLE)
REPOSITORY = "https://gitlab.fanasa.net.local/javadsayedi/organization-hr-api.git"
BASE_IMAGE = "mcr.microsoft.com/dotnet/aspnet@sha256:0fa044f682cb7d93a5a90401a00c626c66f7b00b86922be9441f869eae039f80"
BASE_PATTERN = r"mcr\.microsoft\.com/dotnet/aspnet@sha256:[a-f0-9]{64}"
COMPONENTS = {"web": "Fanasa.AccessManagement.Web.dll", "workbench": "Fanasa.ContractPolicy.Workbench.dll"}
SECRET_SUFFIXES = {".key", ".pem", ".pfx", ".p12", ".env", ".db", ".db3", ".sqlite", ".sqlite3", ".kubeconfig"}
SECRET_DIRECTORIES = {"app_data", "data", "keys", "secrets", ".users", ".vps", ".ssh", ".aws", ".kube"}
PUBLIC_DEVELOPMENT_TEMPLATE = {"Authentication": {"Authority": "http://127.0.0.1:18080/realms/fanasa",
                                                 "ClientId": "fanasa-access-management-web", "ClientSecret": ""}}
RUNTIME_TRANSFORMS = {"web": ["remove-known-public-development-template"], "workbench": []}


def sha(path):
    return BUNDLE.digest(path)


def encode(value):
    return (json.dumps(value, sort_keys=True, ensure_ascii=False, indent=2) + "\n").encode()


def regular(path, max_size=None):
    path = path.absolute()
    if any(part.is_symlink() for part in [path, *path.parents]) or not path.is_file():
        raise ValueError("Input must be a regular file without linked path components")
    if max_size and path.stat().st_size > max_size:
        raise ValueError("Input exceeds its bounded size")
    return path


def fresh(path):
    path = path.absolute()
    if path.exists() or path.is_symlink() or any(part.is_symlink() for part in path.parents):
        raise ValueError("Output requires a fresh unlinked destination")
    if not path.parent.is_dir():
        raise ValueError("Output parent must already exist")
    return path


def clean_origin(ci, receipt):
    source, origin = ci.get("source", {}), ci.get("ciOrigin", {})
    if (ci.get("status") != "passed" or source.get("repository") != REPOSITORY
            or source.get("kind") != "git-commit" or source.get("dirty") is not False
            or source.get("gitBaseCommit") != receipt.get("sourceCommit")
            or origin.get("engine") != "GitLab CI" or origin.get("projectId") != "2"
            or origin.get("ref") != "develop" or origin.get("protected") is not True
            or origin.get("commit") != source.get("gitBaseCommit")
            or any(not isinstance(origin.get(key), str) or not re.fullmatch(r"[1-9][0-9]*", origin[key])
                   for key in ["pipelineId", "jobId"])):
        raise ValueError("Successful clean protected project2 GitLab origin is required")
    return origin


def settings_without_secrets(value):
    if isinstance(value, dict):
        for key, item in value.items():
            if (re.search(r"(?:password|clientsecret|privatekey|access_token|refresh_token|credential|secret)$", key, re.I)
                    and item not in (None, "", False)):
                raise ValueError("Runtime settings contain a nonempty credential")
            settings_without_secrets(item)
    elif isinstance(value, list):
        for item in value:
            settings_without_secrets(item)


def minimal_base_settings(value):
    # ASP.NET overlays mounted Development settings on this file; unrelated base
    # endpoints or integrations could otherwise remain enabled after that merge.
    def normalized(items):
        result = {}
        for key, item in items.items():
            lower = key.casefold()
            if lower in result or ":" in key:
                raise ValueError("Ambiguous base configuration field")
            result[lower] = item
        return result
    value = normalized(value)
    if set(value) - {"authentication", "allowedhosts", "logging", "pip"}:
        raise ValueError("Base appsettings must contain only minimal inert defaults")
    authentication = value.get("authentication", {})
    if not isinstance(authentication, dict):
        raise ValueError("Base authentication settings must be an object")
    authentication = normalized(authentication)
    if set(authentication) - {"authority", "clientid", "clientsecret"} or any(item not in (None, "") for item in authentication.values()):
        raise ValueError("Base authentication identity must be configured externally")
    if "allowedhosts" in value and value["allowedhosts"] not in {"localhost;127.0.0.1", "127.0.0.1;localhost"}:
        raise ValueError("Base AllowedHosts must remain loopback-only")
    if "pip" in value:
        if not isinstance(value["pip"], dict):
            raise ValueError("Base PIP settings must contain only empty secrets and bindings")
        pip = normalized(value["pip"])
        if (set(pip) != {"secrets", "transport"}
                or not isinstance(pip["secrets"], dict) or pip["secrets"]
                or not isinstance(pip["transport"], dict)):
            raise ValueError("Base PIP settings must contain only empty secrets and bindings")
        transport = normalized(pip["transport"])
        if (set(transport) != {"bindings"}
                or not isinstance(transport["bindings"], list) or transport["bindings"]):
            raise ValueError("Base PIP transport must contain only an empty bindings array")
    if "logging" in value:
        if not isinstance(value["logging"], dict):
            raise ValueError("Base logging settings must be an object")
        logging = normalized(value["logging"])
        if set(logging) != {"loglevel"} or not isinstance(logging["loglevel"], dict):
            raise ValueError("Only standard console log levels are allowed in base settings")
        for key, level in normalized(logging["loglevel"]).items():
            if not re.fullmatch(r"[a-z0-9_.-]+", key) or not isinstance(level, str) or level.casefold() not in {"trace", "debug", "information", "warning", "error", "critical", "none"}:
                raise ValueError("Invalid base log level")


def safe_runtime(path, relative):
    if (path.is_symlink() or path.suffix.lower() in SECRET_SUFFIXES
            or any(part.lower() in SECRET_DIRECTORIES for part in Path(relative).parts)
            or path.name.lower().startswith(".env")
            or path.name.lower() in {"credentials.json", "systemd.env", "config.toml"}):
        raise ValueError("Runtime state or credential files cannot enter an image")
    if path.name.lower().startswith("appsettings") and path.suffix.lower() == ".json":
        if path.stat().st_size > BUNDLE.MAX_JSON:
            raise ValueError("Runtime settings exceed their bounded size")
        settings = BUNDLE.read_json(path.read_bytes())
        if relative == "web/appsettings.Development.json":
            if settings != PUBLIC_DEVELOPMENT_TEMPLATE:
                raise ValueError("Development template must match the exact known public source defaults")
            return
        if path.name != "appsettings.json":
            raise ValueError("Environment-specific runtime settings must be mounted externally")
        settings_without_secrets(settings)
        minimal_base_settings(settings)


def dockerfile(component, base_image, binding):
    labels = {"org.opencontainers.image.source": REPOSITORY, "org.opencontainers.image.revision": binding["sourceCommit"],
              "net.fanasa.environment": "development", "net.fanasa.original-archive-sha256": binding["archiveSha256"],
              "net.fanasa.component": component, "net.fanasa.policy-engine-sha256": binding["policyEngineSha256"],
              "net.fanasa.ci.pipeline": binding["ciOrigin"]["pipelineId"], "net.fanasa.ci.job": binding["ciOrigin"]["jobId"]}
    label_lines = "\n".join("LABEL " + key + "=" + json.dumps(value) for key, value in labels.items())
    port = 5191 if component == "web" else 4187
    environment = "ASPNETCORE_URLS=http://127.0.0.1:5191" if component == "web" else "WORKBENCH_PORT=4187 WORKBENCH_DATA_DIR=/state/workbench"
    transform = "RUN rm -f /app/web/appsettings.Development.json\n" if component == "web" else ""
    return (f"FROM {base_image}\nUSER 0\n"
            "RUN apt-get update && apt-get install -y --no-install-recommends python3 ca-certificates "
            "&& rm -rf /var/lib/apt/lists/* && python3 --version && install -d -o 1654 -g 1654 /state\n"
            f"{label_lines}\nWORKDIR /app/{component}\n"
            f"COPY --chown=1654:1654 {component}/ /app/{component}/\n"
            f"{transform}"
            "COPY --chown=1654:1654 adapter/ /adapter/\n"
            f"ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 PYTHONDONTWRITEBYTECODE=1 {environment}\n"
            f"EXPOSE {port}\nUSER 1654:1654\n"
            f'HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 CMD ["python3", "/adapter/health.py", "{component}"]\n'
            f'ENTRYPOINT ["dotnet", "{COMPONENTS[component]}"]\n').encode()


def inventory(context):
    result = []
    for path in sorted(context.rglob("*")):
        if path.is_symlink():
            raise ValueError("Context contains a linked file or directory")
        if path.is_file() and path.relative_to(context).as_posix() != "prepare.json":
            result.append({"path": path.relative_to(context).as_posix(), "sha256": sha(path), "size": path.stat().st_size})
    if len(result) > BUNDLE.MAX_FILES or sum(item["size"] for item in result) > BUNDLE.MAX_EXPANDED:
        raise ValueError("Prepared context exceeds bounded size")
    return result


def prepare(archive, receipt, context, base_image=BASE_IMAGE, runtime_validator=None):
    if not re.fullmatch(BASE_PATTERN, base_image):
        raise ValueError("Pinned official ASP.NET runtime digest is required")
    archive, receipt = regular(archive, BUNDLE.MAX_ARCHIVE), regular(receipt, BUNDLE.MAX_JSON)
    validator = regular(runtime_validator or DIRECTORY.parent / "runtime_config.py", BUNDLE.MAX_JSON)
    context = fresh(context)
    archive_hash, receipt_hash, validator_hash = sha(archive), sha(receipt), sha(validator)
    stage = Path(tempfile.mkdtemp(prefix=".hr-oci-prepare-", dir=context.parent))
    try:
        inputs = stage / "inputs"
        inputs.mkdir()
        archive_copy, receipt_copy = inputs / archive.name, inputs / "release.json"
        shutil.copyfile(archive, archive_copy)
        shutil.copyfile(receipt, receipt_copy)
        if sha(archive_copy) != archive_hash or sha(receipt_copy) != receipt_hash:
            raise ValueError("Authoritative inputs changed while copying")
        result = BUNDLE.verify(archive_copy, archive_hash, None, receipt_copy, stage / "verified")
        verified = stage / "verified"
        original_receipt = BUNDLE.read_json(receipt_copy.read_bytes())
        ci = BUNDLE.read_json((verified / "ci-verification.json").read_bytes())
        origin = clean_origin(ci, original_receipt)
        output = stage / "context"
        output.mkdir()
        evidence = output / "evidence"
        evidence.mkdir()
        shutil.copyfile(receipt_copy, evidence / "original-release.json")
        for name in ["release-manifest.json", "ci-verification.json"]:
            shutil.copyfile(verified / name, evidence / ("original-" + name))
        for component in COMPONENTS:
            for path in (verified / component).rglob("*"):
                if path.is_file():
                    safe_runtime(path, path.relative_to(verified).as_posix())
            shutil.copytree(verified / component, output / component)
        adapter = output / "adapter"
        adapter.mkdir()
        for name in ["health.py", "state_probe.py"]:
            shutil.copyfile(DIRECTORY / name, adapter / name)
        shutil.copyfile(validator, adapter / "runtime_config.py")
        binding = {"release": result["release"], "archiveName": archive.name, "archiveSha256": archive_hash,
                   "originalReceiptSha256": receipt_hash, "originalManifestSha256": original_receipt["manifestSha256"],
                   "originalCiSha256": sha(verified / "ci-verification.json"), "sourceCommit": result["sourceCommit"],
                   "sourceSnapshotSha256": result["sourceSnapshotSha256"], "ciOrigin": origin,
                   "policyEngineSha256": sha(verified / "web/Fanasa.ContractPolicy.dll"),
                   "runtimeTransforms": RUNTIME_TRANSFORMS,
                   "baseImage": base_image, "baseDigest": base_image.split("@")[1],
                   "probeSha256": sha(adapter / "health.py"), "stateProbeSha256": sha(adapter / "state_probe.py"),
                   "runtimeValidatorSha256": validator_hash}
        recipes = {}
        for component in COMPONENTS:
            recipe = output / ("Dockerfile." + component)
            recipe.write_bytes(dockerfile(component, base_image, binding))
            recipes[component] = sha(recipe)
        binding["recipeSha256"] = recipes
        (output / ".dockerignore").write_bytes(b"evidence/\nprepare.json\n")
        entries = inventory(output)
        report = {"schemaVersion": "fanasa.organization-hr.oci.prepare.v1", "status": "context-prepared",
                  "environment": "development", "trustBasis": "operator-authoritative-receipt-not-signature",
                  "binding": binding, "contextSnapshotSha256": hashlib.sha256(encode(entries)).hexdigest(), "files": entries,
                  "registryStatus": "registry-not-pushed", "argoStatus": "Argo-not-installed", "promotion": "pending"}
        report_bytes = encode(report)
        if len(report_bytes) > BUNDLE.MAX_JSON:
            raise ValueError("Prepared context evidence exceeds bounded size")
        (output / "prepare.json").write_bytes(report_bytes)
        if sha(archive) != archive_hash or sha(receipt) != receipt_hash or sha(validator) != validator_hash:
            raise ValueError("Original authoritative input changed during preparation")
        os.rename(output, context)
        return report
    finally:
        shutil.rmtree(stage)


def checked_context(context):
    context = context.absolute()
    if not context.is_dir() or any(path.is_symlink() for path in [context, *context.parents]):
        raise ValueError("Prepared context must be an unlinked directory")
    report = BUNDLE.read_json(regular(context / "prepare.json", BUNDLE.MAX_JSON).read_bytes())
    if (report.get("schemaVersion") != "fanasa.organization-hr.oci.prepare.v1" or report.get("environment") != "development"
            or report.get("status") != "context-prepared" or report.get("trustBasis") != "operator-authoritative-receipt-not-signature"):
        raise ValueError("Unknown prepared context contract")
    entries = inventory(context)
    if entries != report.get("files") or hashlib.sha256(encode(entries)).hexdigest() != report.get("contextSnapshotSha256"):
        raise ValueError("Prepared context changed; local build refused")
    binding = report["binding"]
    if binding.get("runtimeTransforms") != RUNTIME_TRANSFORMS:
        raise ValueError("Prepared runtime transformations differ from the fixed adapter")
    receipt = BUNDLE.read_json((context / "evidence/original-release.json").read_bytes())
    ci = BUNDLE.read_json((context / "evidence/original-ci-verification.json").read_bytes())
    manifest = BUNDLE.read_json((context / "evidence/original-release-manifest.json").read_bytes())
    if (clean_origin(ci, receipt) != binding.get("ciOrigin") or ci["source"] != manifest.get("source")
            or binding.get("sourceCommit") != receipt.get("sourceCommit")
            or binding.get("sourceSnapshotSha256") != receipt.get("sourceSnapshotSha256")
            or binding.get("archiveSha256") != receipt.get("sha256") or binding.get("release") != receipt.get("release")
            or binding.get("originalManifestSha256") != receipt.get("manifestSha256")
            or binding.get("originalCiSha256") != manifest.get("verificationSha256")):
        raise ValueError("Prepared original source/receipt identity changed")
    if (not re.fullmatch(BASE_PATTERN, binding.get("baseImage", ""))
            or binding.get("baseDigest") != binding["baseImage"].split("@")[1]):
        raise ValueError("Prepared base image is not a pinned official runtime")
    manifested = {item["path"]: item for item in manifest["files"] if item["path"].split("/")[0] in COMPONENTS}
    actual = {entry["path"]: entry for entry in entries if entry["path"].split("/")[0] in COMPONENTS}
    if actual != manifested:
        raise ValueError("Prepared runtime binaries differ from the original verified manifest")
    if any(actual[component + "/Fanasa.ContractPolicy.dll"]["sha256"] != binding.get("policyEngineSha256") for component in COMPONENTS):
        raise ValueError("Both role images must retain the same verified policy engine")
    for entry in actual.values():
        safe_runtime(context / entry["path"], entry["path"])
    for component in COMPONENTS:
        recipe = context / ("Dockerfile." + component)
        if recipe.read_bytes() != dockerfile(component, binding["baseImage"], binding) or sha(recipe) != binding["recipeSha256"][component]:
            raise ValueError("Prepared recipe is outside the fixed binary-only adapter")
    for key, path in [("originalReceiptSha256", "evidence/original-release.json"),
                      ("originalManifestSha256", "evidence/original-release-manifest.json"),
                      ("originalCiSha256", "evidence/original-ci-verification.json"),
                      ("probeSha256", "adapter/health.py"), ("stateProbeSha256", "adapter/state_probe.py"),
                      ("runtimeValidatorSha256", "adapter/runtime_config.py")]:
        if binding.get(key) != sha(context / path):
            raise ValueError("Prepared provenance or runtime probe binding changed")
    return report


def docker(args, timeout=180, json_output=False):
    process = subprocess.run(["docker", *args], text=True, encoding="utf-8", errors="replace",
                             capture_output=True, check=False, timeout=timeout)
    if process.returncode != 0:
        raise RuntimeError("Local Docker command failed: " + " ".join(args[:2]) + "\n" + process.stderr[-2000:])
    if len(process.stdout) > 4 * 1024 * 1024:
        raise RuntimeError("Local Docker response exceeded evidence bound")
    return json.loads(process.stdout) if json_output else process.stdout.strip()


def container_args(component, image_id, name, volume):
    result = ["run", "--detach", "--name", name, "--network", "none", "--read-only", "--user", "1654:1654",
              "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--memory", "512m", "--cpus", "1",
              "--pids-limit", "128", "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m,uid=1654,gid=1654",
              "--mount", "type=volume,src=" + volume + ",dst=/state"]
    environment = {"ASPNETCORE_ENVIRONMENT": "Development"}
    if component == "web":
        environment.update({"Authentication__Authority": "https://identity.invalid/realms/fanasa",
                            "Authentication__ClientId": "oci-synthetic-smoke", "Organization__HrApi__Enabled": "false",
                            "Fabric__DataDirectory": "/state/fabric", "Fabric__DataProtectionKeysPath": "/state/keys",
                            "Platform__RegistryPath": "/state/platform-registry.db",
                            "Organization__DataDirectory": "/state/organization", "Accounting__DataDirectory": "/state/accounting"})
    for key, value in environment.items():
        result.extend(["--env", key + "=" + value])
    return [*result, image_id]


def smoke(component, image_id):
    suffix = uuid.uuid4().hex
    volume, name = "hr-oci-smoke-" + suffix, "hr-oci-smoke-" + suffix
    docker(["volume", "create", volume])
    active = False
    try:
        # Root only initializes ownership of this newly created isolated synthetic volume.
        docker(["run", "--rm", "--network", "none", "--read-only", "--user", "0:0", "--cap-drop", "ALL",
                "--cap-add", "CHOWN", "--security-opt", "no-new-privileges", "--memory", "128m", "--pids-limit", "32",
                "--entrypoint", "chown", "--mount", "type=volume,src=" + volume + ",dst=/state", image_id, "1654:1654", "/state"])
        states = []
        for attempt in range(2):
            docker(container_args(component, image_id, name, volume))
            active = True
            for retry in range(30):
                try:
                    health = docker(["exec", name, "python3", "/adapter/health.py", component], timeout=10, json_output=True)
                    if health != {"mode": component, "status": "healthy"}:
                        raise ValueError("Unexpected smoke health evidence")
                    break
                except RuntimeError:
                    state = docker(["inspect", "--format", "{{.State.Running}}", name])
                    if state != "true" or retry == 29:
                        raise RuntimeError("Synthetic runtime failed its health probe")
                    time.sleep(1)
            states.append(docker(["exec", name, "python3", "/adapter/state_probe.py", component], timeout=20, json_output=True))
            docker(["stop", "--time", "10", name], timeout=20)
            docker(["rm", name])
            active = False
        if states[0] != states[1] or states[0].get("integrity") != "ok" or states[0].get("foreignKeys") != 0:
            raise ValueError("Synthetic application state was not preserved across container recreation")
        return {"component": component, "health": "passed", "sqlite": states[0], "statePreserved": True,
                "nonrootUid": 1654, "readOnlyRootfs": True, "network": "none", "state": "fresh-synthetic-volume",
                "hrAuthenticationVerified": False, "ssoVerified": False}
    finally:
        if active:
            docker(["rm", "--force", name])
        docker(["volume", "rm", volume])


def build_smoke(context, receipt_output):
    context = context.absolute()
    output = fresh(receipt_output)
    report = checked_context(context)
    prepare_hash = sha(context / "prepare.json")
    images, checks = {}, []
    for component in COMPONENTS:
        tag = "fanasa/hr-" + component + ":" + report["binding"]["release"] + "-oci-" + report["contextSnapshotSha256"][:12]
        docker(["build", "--platform", "linux/amd64", "--pull", "--tag", tag, "--file", str(context / ("Dockerfile." + component)), str(context)], timeout=1200)
        image = docker(["image", "inspect", tag], json_output=True)[0]
        image_id, config = image.get("Id", ""), image.get("Config", {})
        labels = config.get("Labels", {})
        if (not re.fullmatch(r"sha256:[a-f0-9]{64}", image_id) or image.get("Os") != "linux"
                or image.get("Architecture") != "amd64" or config.get("User") != "1654:1654"
                or config.get("WorkingDir") != "/app/" + component
                or config.get("Entrypoint") != ["dotnet", COMPONENTS[component]]
                or labels.get("net.fanasa.original-archive-sha256") != report["binding"]["archiveSha256"]
                or labels.get("org.opencontainers.image.revision") != report["binding"]["sourceCommit"]
                or labels.get("net.fanasa.ci.pipeline") != report["binding"]["ciOrigin"]["pipelineId"]
                or labels.get("net.fanasa.ci.job") != report["binding"]["ciOrigin"]["jobId"]
                or labels.get("net.fanasa.component") != component
                or labels.get("net.fanasa.policy-engine-sha256") != report["binding"]["policyEngineSha256"]):
            raise ValueError("Built local image does not match the fixed runtime contract")
        engine_hash = docker(["run", "--rm", "--network", "none", "--read-only", "--user", "1654:1654",
                              "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--memory", "128m", "--pids-limit", "32",
                              "--entrypoint", "python3", image_id, "-c",
                              "import hashlib;from pathlib import Path;print(hashlib.sha256(Path('Fanasa.ContractPolicy.dll').read_bytes()).hexdigest())"])
        if engine_hash != report["binding"]["policyEngineSha256"]:
            raise ValueError("Built image policy engine differs from the verified original binary")
        checks.append(smoke(component, image_id))
        images[component] = {"localTag": tag, "localImageId": image_id, "registryManifestDigest": None}
    if checked_context(context) != report or sha(context / "prepare.json") != prepare_hash:
        raise ValueError("Context changed during local build; no verified OCI receipt can be issued")
    result = {"schemaVersion": "fanasa.organization-hr.oci.v1", "status": "local-image-verified", "environment": "development",
              "trustBasis": "operator-authoritative-receipt-not-signature", "binding": report["binding"],
              "prepareSha256": prepare_hash, "contextSnapshotSha256": report["contextSnapshotSha256"],
              "images": images, "smoke": checks, "registryStatus": "registry-not-pushed", "argoStatus": "Argo-not-installed",
              "promotion": "pending", "adapterSha256": sha(Path(__file__))}
    with output.open("xb") as stream:
        stream.write(encode(result))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare_parser = commands.add_parser("prepare")
    for name in ["archive", "receipt", "context"]:
        prepare_parser.add_argument("--" + name, type=Path, required=True)
    prepare_parser.add_argument("--base-image", default=BASE_IMAGE)
    prepare_parser.add_argument("--runtime-validator", type=Path, default=DIRECTORY.parent / "runtime_config.py")
    build_parser = commands.add_parser("build-smoke")
    build_parser.add_argument("--context", type=Path, required=True)
    build_parser.add_argument("--receipt-output", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "prepare":
            result = prepare(args.archive, args.receipt, args.context, args.base_image, args.runtime_validator)
        else:
            result = build_smoke(args.context, args.receipt_output)
        print(json.dumps(result, ensure_ascii=False))
        return 0
    except (OSError, ValueError, KeyError, TypeError, RuntimeError, subprocess.SubprocessError) as error:
        print("HR OCI adapter refused: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
