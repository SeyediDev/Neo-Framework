#!/usr/bin/env python3
"""Fail-closed .NET 10 HR verification and immutable development release packaging."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tarfile
from urllib.parse import urlsplit
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[2]
UI = Path("docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/ui")
WEB = Path("products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Fanasa.AccessManagement.Web.csproj")
ENGINE = UI.parent / "src/Fanasa.ContractPolicy/Fanasa.ContractPolicy.csproj"
WORKBENCH = UI / "server/Fanasa.ContractPolicy.Workbench.csproj"
PROBE = UI / "server/tests/DatabaseFreshness.Probe/DatabaseFreshness.Probe.csproj"
HR3_PACKAGE = Path("deploy/fanasa-organization-hr/hr-read-host-v3-r6")
SUITES = ["Organization.Hr.Tests", "Organization.Bearer.Tests", "Organization.Tests", "Organization.Calendar.Tests",
          "Organization.Temporal.Tests", "ConsumerApplicationCatalog.Tests", "PublishedCatalog.Tests",
          "ConsumerUsage.Api.Tests", "Workbench.Boundary.Tests", "ProductRegistration.Tests"]
SOURCE_PATHS = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.config",
                "products/Fanasa.AccessManagement/Directory.Build.props", "products/Fanasa.AccessManagement/Directory.Build.targets",
                "products/Fanasa.AccessManagement/Directory.Packages.props", "products/Fanasa.AccessManagement/Fanasa.AccessManagement.slnx",
                "products/Fanasa.AccessManagement/global.json", "products/Fanasa.AccessManagement/README.md", "products/Fanasa.AccessManagement/docs/PRODUCT-REGISTRATION.md",
                "products/Fanasa.AccessManagement/docs/PIP-OPERATIONS.fa.md", "products/Fanasa.AccessManagement/docs/pip-educational-journey.md",
                "products/Fanasa.AccessManagement/src", "products/Fanasa.AccessManagement/tests",
                "docs/fanasa-platform-control-center/workstreams/contract-policy/implementation",
                "docs/fanasa-platform-control-center/workstreams/contract-policy/schemas",
                "docs/fanasa-platform-control-center/pilot/hr-api.openapi.yaml",
                "Jenkinsfile.hr-development", ".gitlab-ci.hr-development.yml", "deploy/fanasa-organization-hr"]
SOURCE_SUFFIXES = {".cs", ".cshtml", ".csproj", ".slnx", ".props", ".targets", ".json", ".js", ".mjs", ".cjs", ".css", ".conf", ".config",
                   ".html", ".svg", ".woff", ".woff2", ".ttf", ".yaml", ".yml", ".tpl", ".py", ".sh", ".ps1", ".toml", ".dockerfile", ".md", ".service", ".timer"}
# These immutable package members are test receipts, not mutable operational logs.
SEALED_EVIDENCE_PATHS = {"deploy/fanasa-organization-hr/hr-read-profile-v3/contract-policy/original-regression.log",
                         "deploy/fanasa-organization-hr/hr-read-pdp-v3/verification.log",
                         "deploy/fanasa-organization-hr/hr-read-host-v3-r3/verification.log"}
EXCLUDED_PARTS = {"bin", "obj", ".artifacts", ".build", ".tools", "node_modules", "evidence", ".runs", "data", "App_Data", "__pycache__"}
REPOSITORIES = {"https://github.com/SeyediDev/Neo-Framework.git",
                "https://gitlab.fanasa.net.local/javadsayedi/organization-hr-api.git"}
WORKSPACE_GENERATED = {"bin", "obj", ".runs", "__pycache__"}
REQUIRED_TEST_GATES = {f"tests-{suite}" for suite in SUITES} | {"tests-native-workbench", "tests-organization-temporal-ui"}
MIN_ORGANIZATION_UI_TESTS = 44


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def json_write(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def utc() -> str:
    return datetime.now(timezone.utc).isoformat()


def capture(command: list[str]) -> str:
    return subprocess.check_output(command, cwd=ROOT, text=True, encoding="utf-8", errors="replace").strip()


def source_identity(commit: str | None, allow_working_tree: bool) -> dict:
    actual = capture(["git", "rev-parse", "HEAD"])
    if not re.fullmatch(r"[a-f0-9]{40}", actual) or commit is not None and actual != commit:
        raise RuntimeError("The checkout does not match the requested exact Git commit.")
    status = capture(["git", "status", "--porcelain", "--", *SOURCE_PATHS])
    if status and not allow_working_tree:
        raise RuntimeError("Source checkout has changes. CI requires a clean exact-commit checkout.")
    files: list[dict] = []
    for relative in SOURCE_PATHS:
        path = ROOT / relative
        if not path.exists():
            continue
        candidates = [path] if path.is_file() else sorted(path.rglob("*"))
        for candidate in candidates:
            if not candidate.is_file() or candidate.is_symlink():
                continue
            name = candidate.relative_to(ROOT)
            if any(part in EXCLUDED_PARTS for part in name.parts):
                continue
            # Machine-specific settings are never part of a transferable source snapshot.
            config_name = candidate.name.casefold()
            if (config_name != "appsettings.json" and config_name.startswith("appsettings.") and candidate.suffix.lower() == ".json"
                    and ("src" in name.parts or name.is_relative_to(UI / "server"))):
                continue
            if (candidate.suffix.lower() not in SOURCE_SUFFIXES and not candidate.name.startswith("Jenkinsfile")
                    and name.as_posix() not in SEALED_EVIDENCE_PATHS and name != UI / "assets/OFL.txt"):
                continue
            files.append({"path": name.as_posix(), "sha256": sha256(candidate)})
    files = sorted({item["path"]: item for item in files}.values(), key=lambda item: item["path"])
    encoded = json.dumps(files, sort_keys=True, separators=(",", ":")).encode()
    remote = urlsplit(capture(["git", "remote", "get-url", "origin"]))
    # A CI checkout can contain a transient job credential in its origin URL. Never persist it.
    repository = f"https://{remote.hostname}{remote.path.rstrip('/').removesuffix('.git')}.git"
    if remote.scheme not in {"https", "http"} or remote.port not in {None, 443} or repository not in REPOSITORIES:
        raise RuntimeError("Source origin is not a registered HR repository.")
    return {"repository": repository, "gitBaseCommit": actual,
            "kind": "working-tree-snapshot" if status else "git-commit", "dirty": bool(status),
            "snapshotSha256": hashlib.sha256(encoded).hexdigest(), "files": files}


def ci_origin(source: dict) -> dict:
    if os.environ.get("GITLAB_CI") == "true":
        result = {"engine": "GitLab CI", "projectId": os.environ.get("CI_PROJECT_ID"),
                  "pipelineId": os.environ.get("CI_PIPELINE_ID"), "jobId": os.environ.get("CI_JOB_ID"),
                  "commit": os.environ.get("CI_COMMIT_SHA"), "ref": os.environ.get("CI_COMMIT_REF_NAME"),
                  "protected": os.environ.get("CI_COMMIT_REF_PROTECTED") == "true"}
        if (any(not re.fullmatch(r"[1-9][0-9]*", result[key] or "") for key in ("projectId", "pipelineId", "jobId"))
                or result["projectId"] != "2" or result["commit"] != source["gitBaseCommit"]
                or result["ref"] != "develop" or not result["protected"]
                or source["repository"] != "https://gitlab.fanasa.net.local/javadsayedi/organization-hr-api.git"):
            raise RuntimeError("GitLab CI origin does not match the fixed protected HR development project.")
        return result
    return {"engine": "local-operator" if source["dirty"] else "SCM-build", "commit": source["gitBaseCommit"]}


def linked(path: Path) -> bool:
    return path.is_symlink() or getattr(path, "is_junction", lambda: False)()


def plain_root(root: Path) -> None:
    if any(linked(path) for path in (root.absolute(), *root.absolute().parents)):
        raise RuntimeError("Linked build workspace/source root is not accepted.")


def source_file(root: Path, relative: str) -> Path:
    if not isinstance(relative, str):
        raise RuntimeError("Noncanonical build source path.")
    path = PurePosixPath(relative)
    if (not relative or "\\" in relative or ":" in relative
            or path.is_absolute() or path.as_posix() != relative
            or any(part in {"", ".", ".."} or part.endswith((" ", ".")) for part in relative.split("/"))):
        raise RuntimeError("Noncanonical build source path.")
    current = root
    for part in path.parts:
        current = current / part
        if linked(current):
            raise RuntimeError("Linked build source is not accepted.")
    return current


def validate_workspace(workspace: Path, source: dict) -> None:
    plain_root(workspace)
    if not workspace.is_dir():
        raise RuntimeError("The verified build workspace is unavailable.")
    expected = {item["path"]: item["sha256"] for item in source["files"]}
    if len(expected) != len(source["files"]) or not expected:
        raise RuntimeError("Build source inventory must be nonempty and unique.")
    actual = set()
    for directory, folders, files in os.walk(workspace, followlinks=False):
        for folder in list(folders):
            path = Path(directory) / folder
            if linked(path):
                raise RuntimeError("Linked directory in build workspace.")
            if folder in WORKSPACE_GENERATED:
                folders.remove(folder)
        for name in files:
            path = Path(directory) / name
            relative = path.relative_to(workspace).as_posix()
            if linked(path) or not path.is_file():
                raise RuntimeError("Linked or nonregular file in build workspace.")
            actual.add(relative)
    if actual != set(expected):
        raise RuntimeError("Build workspace contains missing or unlisted source files.")
    for relative, digest in expected.items():
        if not re.fullmatch(r"[a-f0-9]{64}", digest) or sha256(source_file(workspace, relative)) != digest:
            raise RuntimeError("Build workspace source changed: " + relative)


def prepare_workspace(output: Path, source: dict) -> Path:
    plain_root(ROOT)
    plain_root(output)
    workspace = output / "build-source"
    if workspace.exists() or linked(workspace):
        raise RuntimeError("Build workspace already exists; choose a fresh output directory.")
    workspace.mkdir(parents=True)
    for item in source["files"]:
        original = source_file(ROOT, item["path"])
        target = source_file(workspace, item["path"])
        if not original.is_file() or sha256(original) != item["sha256"]:
            raise RuntimeError("Source changed before copying to build workspace.")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(original, target)
    validate_workspace(workspace, source)
    return workspace


def validate_test_evidence(evidence: dict, output: Path, source: dict) -> None:
    if (evidence.get("status") != "passed" or evidence.get("source") != source
            or evidence.get("buildIsolation") != "verified-source-copy"):
        raise RuntimeError("Successful isolated verification of the unchanged source is required.")
    gates = evidence.get("gates", [])
    if not isinstance(gates, list) or any(not isinstance(g, dict) or not isinstance(g.get("name"), str) for g in gates):
        raise RuntimeError("Invalid verification gates.")
    indexed = {g.get("name"): g for g in gates}
    if len(indexed) != len(gates) or not REQUIRED_TEST_GATES.issubset(indexed):
        raise RuntimeError("Complete unique required test gates are required before publication.")
    for gate in gates:
        log = gate.get("log")
        digest = gate.get("logSha256")
        if (not isinstance(log, str) or re.fullmatch(r"[A-Za-z0-9._-]+\.log", log) is None
                or type(gate.get("exitCode")) is not int or gate["exitCode"] != 0
                or not isinstance(digest, str) or not re.fullmatch(r"[a-f0-9]{64}", digest)
                or not (output / log).is_file() or linked(output / log)
                or sha256(output / log) != digest):
            raise RuntimeError("Verification log is absent, changed or unsuccessful.")
    for name in REQUIRED_TEST_GATES:
        gate = indexed[name]
        field = "passedTests" if name in {"tests-native-workbench", "tests-organization-temporal-ui"} else "passedChecks"
        minimum = 15 if name == "tests-native-workbench" else MIN_ORGANIZATION_UI_TESTS if name == "tests-organization-temporal-ui" else 1
        if type(gate.get(field)) is not int or gate[field] < minimum:
            raise RuntimeError("Required test gate lacks passing checks: " + name)
        if field == "passedTests" and any(type(gate.get(k)) is not int or gate[k] != 0 for k in ("failedTests", "skippedTests")):
            raise RuntimeError("UI/native tests cannot contain failures or skipped cases.")


def run(command: list[str], output: Path, name: str, env: dict | None = None, timeout: int = 900,
        cwd: Path | None = None) -> dict:
    started = utc()
    print(f"RUN {name}", flush=True)
    output.mkdir(parents=True, exist_ok=True)
    log = output / f"{name}.log"
    with log.open("w", encoding="utf-8") as stream:
        process = subprocess.run(command, cwd=cwd or ROOT, stdout=stream, stderr=subprocess.STDOUT,
                                 env=dict(env or os.environ, DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER="1"), timeout=timeout, check=False)
    result = {"name": name, "startedAt": started, "completedAt": utc(), "exitCode": process.returncode,
              "log": log.name, "logSha256": sha256(log)}
    if process.returncode != 0:
        print(log.read_text(encoding="utf-8", errors="replace")[-12000:], file=sys.stderr)
        raise RuntimeError(f"{name} failed with exit {process.returncode}; deployment is blocked.")
    return result


def validate_hr3(args: argparse.Namespace) -> None:
    """Validate the sealed candidate against exact source; never repin or activate it."""
    output = args.output.resolve()
    evidence_path = output / "hr3-validation.json"
    if evidence_path.exists():
        raise RuntimeError("HR3 validation evidence already exists; choose a new output directory.")
    source = source_identity(args.source_commit, args.allow_working_tree)
    package = HR3_PACKAGE
    required = [package / "SOURCE-PACKAGE.json", package / "verify-package.py", package / "test-host.sh"]
    if any(not (ROOT / path).is_file() or (ROOT / path).is_symlink() for path in required):
        raise RuntimeError("The sealed HR3 actual-host validation package is unavailable.")
    gates: list[dict] = []
    evidence = {"schemaVersion": "fanasa.hr3.ci-source-gate/1", "startedAt": utc(), "status": "running",
                "source": source, "ciOrigin": ci_origin(source), "gates": gates,
                "packageManifestSha256": sha256(ROOT / package / "SOURCE-PACKAGE.json"),
                "runtimeActivation": False, "operationalAcceptance": False}
    try:
        # Integrity/host-input drift must stop execution BEFORE compiling any candidate.
        gates.append(run([sys.executable, str(package / "verify-package.py"), "--repository", str(ROOT)],
                         output, "hr3-source-integrity", timeout=120))
        gates.append(run(["sh", str(package / "test-host.sh"), str(ROOT)],
                         output, "hr3-actual-host", timeout=2700))
        if source_identity(args.source_commit, args.allow_working_tree) != source:
            raise RuntimeError("Source changed during HR3 validation; packaging remains blocked.")
        evidence["status"] = "passed"
    except Exception as error:
        evidence["status"] = "failed"
        evidence["failure"] = str(error)
        raise
    finally:
        # A failing subprocess raises before run() can return a gate receipt.
        # Bind its diagnostic bytes too, without changing the common build runner.
        evidence["logs"] = [{"log": name, "logSha256": sha256(output / name)}
                            for name in ("hr3-source-integrity.log", "hr3-actual-host.log")
                            if (output / name).is_file()]
        evidence["completedAt"] = utc()
        json_write(evidence_path, evidence)
    print(f"Verified sealed HR3 actual-host candidate: {evidence_path}")


def verify(args: argparse.Namespace) -> None:
    output = args.output.resolve()
    evidence_path = output / "verification.json"
    if evidence_path.exists():
        raise RuntimeError("Verification evidence already exists; choose a new output directory.")
    source = source_identity(args.source_commit, args.allow_working_tree)
    workspace = prepare_workspace(output, source)
    cached = getattr(args, "cached_restore", False)
    if cached and (not args.allow_working_tree or os.environ.get("GITLAB_CI") == "true"):
        raise RuntimeError("Cached restore is only allowed for explicit local working-tree verification.")
    restore_options = ["--ignore-failed-sources", "-p:NuGetAudit=false"] if cached else []
    serial = ["--disable-build-servers", "-m:1", "-nodeReuse:false", "-p:BuildInParallel=false",
              "-p:UseSharedCompilation=false", "-p:UseRazorBuildServer=false"]
    tools = {"dotnet": capture(["dotnet", "--version"]), "node": capture(["node", "--version"]),
             "python": sys.version.split()[0]}
    if not tools["dotnet"].startswith("10."):
        raise RuntimeError("This pipeline requires the .NET 10 SDK.")
    gates: list[dict] = []
    evidence = {"schemaVersion": "fanasa.organization-hr.ci.v1", "environment": "development",
                "component": "organization-hr", "startedAt": utc(), "status": "running",
                "source": source, "ciOrigin": ci_origin(source), "tools": tools, "gates": gates,
                "buildIsolation": "verified-source-copy", "buildWorkspace": "build-source",
                "restoreMode": "local-cache-audit-disabled" if cached else "configured-feeds",
                "securityScanners": "not-configured", "immutableOciArtifact": False,
                "gitOpsPromotion": "adapter-pending"}
    try:
        for suite in SUITES:
            project = Path("products/Fanasa.AccessManagement/tests") / suite / f"{suite}.csproj"
            if not (ROOT / project).is_file() or not (ROOT / project.parent / "Program.cs").is_file():
                raise RuntimeError(f"Required executable test suite {suite} is unavailable.")
            gates.append(run(["dotnet", "restore", str(project), "--nologo", "--disable-parallel", *serial, *restore_options],
                             output, f"restore-{suite}", cwd=workspace))
            gates.append(run(["dotnet", "build", str(project), "-c", "Release", "--no-restore", "--nologo",
                              *serial, "-p:ContinuousIntegrationBuild=true"],
                             output, f"build-{suite}", cwd=workspace))
            # Run exactly the built framework-dependent assembly, without launch profiles or an OS-specific apphost.
            assembly = project.parent / "bin/Release/net10.0" / f"{suite}.dll"
            if not (workspace / assembly).is_file():
                raise RuntimeError(f"Required test assembly {suite} is unavailable.")
            result = run(["dotnet", str(assembly)],
                         output, f"tests-{suite}", cwd=workspace)
            log = (output / result["log"]).read_text(encoding="utf-8", errors="replace")
            passed = sum(bool(re.match(r"^PASS(?:\s|:)", line)) and not re.search(r": \d+ checks\s*$", line)
                         for line in log.splitlines())
            if passed < 1:
                raise RuntimeError(f"{suite} exited without actual passing checks.")
            result["passedChecks"] = passed
            gates.append(result)
        for project in [ENGINE, WORKBENCH, PROBE]:
            label = project.stem
            gates.append(run(["dotnet", "restore", str(project), "--nologo", "--disable-parallel", *serial, *restore_options],
                             output, f"restore-{label}", cwd=workspace))
            gates.append(run(["dotnet", "build", str(project), "-c", "Debug", "--no-restore", "--nologo",
                              *serial], output, f"build-{label}", cwd=workspace))
        env = dict(os.environ, WORKBENCH_TEST_PYTHON=sys.executable,
                   WORKBENCH_TEST_ASSEMBLY=str(workspace / WORKBENCH.parent / "bin/Debug/net10.0/Fanasa.ContractPolicy.Workbench.dll"))
        native = run(["node", "--test", "--test-concurrency=1", "--test-reporter=tap",
                      str(UI / "server/workbench.test.mjs"), str(UI / "server/runtime-integrity.test.mjs")],
                     output, "tests-native-workbench", env=env, timeout=1200, cwd=workspace)
        tap = (output / native["log"]).read_text(encoding="utf-8", errors="replace")
        passed = re.search(r"(?m)^# pass (\d+)\s*$", tap)
        failed = re.search(r"(?m)^# fail (\d+)\s*$", tap)
        skipped = re.search(r"(?m)^# skipped (\d+)\s*$", tap)
        if not passed or int(passed[1]) < 15 or not failed or int(failed[1]) != 0 or not skipped or int(skipped[1]) != 0:
            raise RuntimeError("Native workbench TAP must contain all required tests, zero failures and zero skips.")
        native["passedTests"] = int(passed[1])
        native["failedTests"] = int(failed[1])
        native["skippedTests"] = int(skipped[1])
        gates.append(native)
        temporal_ui = run(["node", "--test", "--test-concurrency=1", "--test-reporter=tap",
                           "products/Fanasa.AccessManagement/tests/governance-ui.test.cjs",
                           "products/Fanasa.AccessManagement/tests/organization-calendar-ui.test.cjs",
                           "products/Fanasa.AccessManagement/tests/pip-ui.test.cjs",
                           "products/Fanasa.AccessManagement/tests/pip-governance-ui.test.cjs"],
                          output, "tests-organization-temporal-ui", timeout=180, cwd=workspace)
        temporal_tap = (output / temporal_ui["log"]).read_text(encoding="utf-8", errors="replace")
        passed = re.search(r"(?m)^# pass (\d+)\s*$", temporal_tap)
        failed = re.search(r"(?m)^# fail (\d+)\s*$", temporal_tap)
        skipped = re.search(r"(?m)^# skipped (\d+)\s*$", temporal_tap)
        if not passed or int(passed[1]) < MIN_ORGANIZATION_UI_TESTS or not failed or int(failed[1]) != 0 or not skipped or int(skipped[1]) != 0:
            raise RuntimeError("Organization temporal and PIP UI tests must include all required checks without failures or skips.")
        temporal_ui["passedTests"] = int(passed[1])
        temporal_ui["failedTests"] = int(failed[1])
        temporal_ui["skippedTests"] = int(skipped[1])
        gates.append(temporal_ui)
        validate_workspace(workspace, source)
        if source_identity(args.source_commit, args.allow_working_tree)["snapshotSha256"] != source["snapshotSha256"]:
            raise RuntimeError("Source changed while tests were running; evidence cannot authorize packaging.")
        evidence["status"] = "passed"
    except Exception as error:
        evidence["status"] = "failed"
        evidence["failure"] = str(error)
        raise
    finally:
        evidence["completedAt"] = utc()
        json_write(evidence_path, evidence)
    print(f"Verified all required suites: {evidence_path}")


def package(args: argparse.Namespace) -> None:
    output = args.output.resolve()
    evidence_path = output / "verification.json"
    evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
    source = source_identity(args.source_commit, args.allow_working_tree)
    validate_test_evidence(evidence, output, source)
    workspace = output / "build-source"
    validate_workspace(workspace, source)
    release = f"hr-development-{source['gitBaseCommit'][:12]}-{source['snapshotSha256'][:12]}"
    destination = output / release
    archive = output / f"{release}.tar.gz"
    if destination.exists() or archive.exists():
        raise RuntimeError("A release is immutable and must never be overwritten.")
    destination.mkdir(parents=True)
    for project, folder in [(WEB, "web"), (WORKBENCH, "workbench")]:
        if folder == "workbench":
            run(["dotnet", "build", str(ENGINE), "-c", "Release", "--no-restore", "--nologo", "-m:1",
                 "-p:UseSharedCompilation=false", "-p:ContinuousIntegrationBuild=true", "--disable-build-servers",
                 "-nodeReuse:false", "-p:BuildInParallel=false"], output, "build-release-engine", cwd=workspace)
        run(["dotnet", "publish", str(project), "-c", "Release", "--no-restore", "--nologo", "-m:1",
             "-p:UseSharedCompilation=false", "-p:ContinuousIntegrationBuild=true", "--disable-build-servers",
             "-nodeReuse:false", "-p:BuildInParallel=false", "-p:UseRazorBuildServer=false", "-p:UseAppHost=false",
             "-o", str(destination / folder)], output, f"publish-{folder}", cwd=workspace)
    web, workbench = destination / "web", destination / "workbench"
    if sha256(web / "Fanasa.ContractPolicy.dll") != sha256(workbench / "Fanasa.ContractPolicy.dll"):
        raise RuntimeError("Web and workbench native engine assemblies differ.")
    (web / "Workbench").mkdir(exist_ok=True)
    assets = web / "wwwroot/workbench"
    assets.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(workspace / UI / "index.html", web / "Workbench/index.html")
    for file in ["app.js", "styles.css", "workbench.js", "workbench.css"]:
        shutil.copyfile(workspace / UI / file, assets / file)
    shutil.copytree(workspace / UI / "assets", assets / "assets", dirs_exist_ok=True)
    if (assets / "index.html").exists():
        raise RuntimeError("Protected workbench shell must not be a public static file.")
    if len(list((workbench / "Schemas").glob("*.schema.json"))) != 10:
        raise RuntimeError("All ten native policy schemas must be packaged.")
    if not (workbench / "runtimes/linux-x64/native/libe_sqlite3.so").is_file():
        raise RuntimeError("Required Linux SQLite native runtime is missing.")
    shutil.copyfile(evidence_path, destination / "ci-verification.json")
    (destination / "evidence").mkdir()
    for gate in evidence["gates"]:
        shutil.copyfile(output / gate["log"], destination / "evidence" / gate["log"])
    files = []
    for path in sorted(destination.rglob("*")):
        if path.is_symlink():
            raise RuntimeError("Release files cannot be symbolic links.")
        if path.is_file():
            relative = path.relative_to(destination).as_posix()
            if path.name.casefold() != "appsettings.json" and path.name.casefold().startswith("appsettings.") and path.suffix.lower() == ".json":
                raise RuntimeError(f"Environment-specific settings cannot be shipped: {relative}")
            if path.suffix.lower() in {".key", ".pem", ".pfx", ".p12", ".env", ".db", ".sqlite"} or any(
                    part in {"App_Data", "data", ".users", ".vps", "keys"} for part in path.relative_to(destination).parts):
                raise RuntimeError(f"Credential or persisted-state file cannot be shipped: {relative}")
            files.append({"path": relative, "sha256": sha256(path), "size": path.stat().st_size})
    manifest = {"schemaVersion": "fanasa.organization-hr.release.v1", "release": release, "createdUtc": utc(),
                "framework": "net10.0", "environment": "development", "component": "organization-hr",
                "source": source, "verificationSha256": sha256(evidence_path),
                "webService": "fanasa-access-management@development.service",
                "workbenchService": "fanasa-policy-workbench.service", "workbenchStorage": "SQLite",
                "workbenchDatabaseSchema": 1, "workbenchRuntimeIntegrity": True,
                "hrAuthentication": "dedicated service JWT plus actual Gateway mTLS peer",
                "runtimeSecretsInArtifact": False, "databaseMigration": "none",
                "cdMode": "bounded-systemd-development", "gitOpsPromotion": "adapter-pending", "files": files}
    json_write(destination / "release-manifest.json", manifest)
    validate_workspace(workspace, source)
    if source_identity(args.source_commit, args.allow_working_tree)["snapshotSha256"] != source["snapshotSha256"]:
        raise RuntimeError("Source changed during publication.")
    with tarfile.open(archive, "w:gz") as bundle:
        for path in sorted(destination.rglob("*")):
            if path.is_file():
                bundle.add(path, arcname=path.relative_to(destination).as_posix(), recursive=False)
    digest = sha256(archive)
    (output / "release.sha256").write_text(f"{digest}  {archive.name}\n", encoding="ascii")
    json_write(output / "release.json", {"release": release, "archive": archive.name, "sha256": digest,
                                        "sourceCommit": source["gitBaseCommit"], "sourceKind": source["kind"],
                                        "sourceSnapshotSha256": source["snapshotSha256"],
                                        "manifestSha256": sha256(destination / "release-manifest.json")})
    print(json.dumps({"release": release, "archive": str(archive), "sha256": digest}))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["verify", "package", "source", "validate-hr3"])
    parser.add_argument("--output", type=Path, default=ROOT / ".artifacts/hr-development")
    parser.add_argument("--source-commit", help="Exact full40character checked-out commit")
    parser.add_argument("--allow-working-tree", action="store_true", help="Explicit local development snapshot; never claim clean Git source")
    parser.add_argument("--cached-restore", action="store_true", help="Local working-tree verification only: tolerate unavailable feeds and disable NuGet audit")
    args = parser.parse_args()
    if args.source_commit is not None and not re.fullmatch(r"[a-f0-9]{40}", args.source_commit):
        parser.error("--source-commit must be a full lower-case40character Git commit")
    if args.command == "source":
        print(json.dumps(source_identity(args.source_commit, args.allow_working_tree), ensure_ascii=False, indent=2))
    else:
        {"verify": verify, "package": package, "validate-hr3": validate_hr3}[args.command](args)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print(f"HR pipeline refused: {error}", file=sys.stderr)
        sys.exit(1)
