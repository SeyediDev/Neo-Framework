#!/usr/bin/env python3
"""Root-owned pull-based CD for one protected GitLab HR development project."""
from __future__ import annotations
import hashlib
import json
import os
from pathlib import Path
import re
import ssl
import subprocess
import sys
import tempfile
import tarfile
from datetime import datetime, timezone
from urllib.parse import quote, urlencode
from urllib.request import HTTPRedirectHandler, Request, build_opener, HTTPSHandler

BASE = "https://gitlab.fanasa.net.local"
PROJECT = 2
PROJECT_PATH = "javadsayedi/organization-hr-api"
REPOSITORY = BASE + "/" + PROJECT_PATH + ".git"
REF = "develop"
JOB = "hr_verify_package"
CONFIG = Path("/etc/fanasa-organization-hr/gitlab-cd.json")
STATE = Path("/var/lib/fanasa-organization-hr/gitlab-cd")
ARTIFACTS = Path("/var/lib/fanasa-organization-hr/artifacts")
METADATA = Path("/etc/fanasa-organization-hr/deployment.json")
HELPER = "/usr/local/sbin/fanasa-organization-hr-deploy"
VERIFIER = "/usr/local/lib/fanasa-organization-hr/verify_bundle.py"
CURRENT = Path("/opt/fanasa-access-management/development/current")
MAX_ARCHIVE = 512 * 1024 * 1024


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise RuntimeError("GitLab API redirects are forbidden; credentials stay at the fixed origin.")


def protected_file(path: Path) -> bytes:
    stat = path.stat()
    if path.is_symlink() or stat.st_uid != 0 or stat.st_mode & 0o077:
        raise RuntimeError("Trusted GitLab CD configuration/credential must be root-only and unlinked.")
    return path.read_bytes()


def protected_directory(path: Path):
    if not path.is_dir() or path.is_symlink():
        raise RuntimeError("Trusted CD directory must be an unlinked real directory.")
    stat = path.stat()
    if stat.st_uid != 0 or stat.st_mode & 0o022:
        raise RuntimeError("Trusted CD directory must be root-owned and not writable by another identity.")


def cached_file(path: Path):
    if path.exists() or path.is_symlink():
        if not path.is_file() or path.is_symlink() or path.stat().st_uid != 0 or path.stat().st_mode & 0o022:
            raise RuntimeError("Cached GitLab artifacts must remain unlinked root-owned regular files.")


class Api:
    def __init__(self, token: str, ca: str):
        self.token = token
        self.opener = build_opener(NoRedirect(), HTTPSHandler(context=ssl.create_default_context(cafile=ca)))

    def request(self, path: str, payload=None, method="GET", maximum=1024 * 1024) -> bytes:
        request = Request(BASE + "/api/v4/projects/2/" + path,
                          data=json.dumps(payload).encode() if payload is not None else None,
                          headers={"PRIVATE-TOKEN": self.token, "Content-Type": "application/json"}, method=method)
        with self.opener.open(request, timeout=60) as response:
            if response.status != 200 and response.status != 201:
                raise RuntimeError("GitLab returned an unexpected API status.")
            data = response.read(maximum + 1)
            if len(data) > maximum:
                raise RuntimeError("GitLab response exceeded its bounded size.")
            return data

    def json(self, path, payload=None, method="GET"):
        return json.loads(self.request(path, payload, method))

    def artifact(self, job: int, name: str, target: Path, maximum: int):
        path = f"jobs/{job}/artifacts/" + quote(".artifacts/hr-development/" + name, safe="/")
        request = Request(BASE + "/api/v4/projects/2/" + path, headers={"PRIVATE-TOKEN": self.token})
        size = 0
        with self.opener.open(request, timeout=120) as response, target.open("xb") as stream:
            for data in iter(lambda: response.read(1024 * 1024), b""):
                size += len(data)
                if size > maximum:
                    raise RuntimeError("GitLab artifact exceeded its bounded size.")
                stream.write(data)


def select(api: Api, runner: int):
    branch = api.json("repository/branches/develop")
    sha = branch["commit"]["id"]
    if branch.get("name") != REF or branch.get("protected") is not True or not re.fullmatch(r"[a-f0-9]{40}", sha):
        raise RuntimeError("The fixed development branch must remain protected.")
    pipelines = api.json("pipelines?" + urlencode({"ref": REF, "sha": sha, "status": "success", "per_page": 20}))
    for item in pipelines:
        pipeline = api.json(f"pipelines/{int(item['id'])}")
        if (pipeline.get("project_id") != PROJECT or pipeline.get("sha") != sha or pipeline.get("ref") != REF
                or pipeline.get("status") != "success" or pipeline.get("source") not in {"push", "web"}
                or pipeline.get("web_url") != f"{BASE}/{PROJECT_PATH}/-/pipelines/{pipeline['id']}"):
            continue
        jobs = api.json(f"pipelines/{pipeline['id']}/jobs?include_retried=false&per_page=100")
        candidates = [job for job in jobs if job.get("name") == JOB]
        if len(candidates) != 1:
            raise RuntimeError("Exactly one fixed HR verification job is required.")
        job = candidates[0]
        if (job.get("status") != "success" or job.get("allow_failure") is not False or job.get("tag") is not False
                or job.get("ref") != REF or job.get("commit", {}).get("id") != sha
                or job.get("pipeline", {}).get("id") != pipeline["id"]
                or job.get("runner", {}).get("id") != runner):
            raise RuntimeError("Job, runner, pipeline and protected source bindings must all match.")
        return pipeline, job
    return None


def validate_receipt(receipt, sha):
    release = receipt.get("release", "")
    if (not re.fullmatch(r"hr-development-[a-f0-9]{12}-[a-f0-9]{12}", release)
            or receipt.get("archive") != release + ".tar.gz"
            or not re.fullmatch(r"[a-f0-9]{64}", receipt.get("sha256", ""))
            or receipt.get("sourceCommit") != sha or receipt.get("sourceKind") != "git-commit"
            or not re.fullmatch(r"[a-f0-9]{64}", receipt.get("sourceSnapshotSha256", ""))
            or not re.fullmatch(r"[a-f0-9]{64}", receipt.get("manifestSha256", ""))):
        raise RuntimeError("The HR release receipt must match this successful clean GitLab commit.")


def validate_evidence(evidence, pipeline, job):
    source = evidence.get("source", {})
    origin = evidence.get("ciOrigin", {})
    if (evidence.get("status") != "passed" or source.get("repository") != REPOSITORY
            or source.get("gitBaseCommit") != pipeline["sha"] or source.get("dirty") is not False
            or source.get("kind") != "git-commit" or origin.get("engine") != "GitLab CI"
            or origin.get("projectId") != str(PROJECT) or origin.get("pipelineId") != str(pipeline["id"])
            or origin.get("jobId") != str(job["id"]) or origin.get("commit") != pipeline["sha"]
            or origin.get("ref") != REF or origin.get("protected") is not True):
        raise RuntimeError("Downloaded CI evidence does not belong to this exact trusted GitLab job.")


def atomic_json(path: Path, value):
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False) as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
        temporary = Path(stream.name)
    temporary.chmod(0o644)
    temporary.replace(path)


def deployed_origin(evidence, commit):
    origin = evidence.get("ciOrigin", {})
    for key in ("pipelineId", "jobId"):
        if not re.fullmatch(r"[1-9][0-9]*", origin.get(key, "")):
            raise RuntimeError("The active release has no exact GitLab deployment origin.")
    pipeline = {"id": int(origin["pipelineId"]), "sha": commit}
    job = {"id": int(origin["jobId"])}
    validate_evidence(evidence, pipeline, job)
    return pipeline["id"], job["id"]


def main():
    import fcntl
    if os.geteuid() != 0 or len(sys.argv) != 1:
        raise RuntimeError("The fixed trusted timer runs only as root, without caller parameters.")
    config = json.loads(protected_file(CONFIG))
    if config.get("projectId") != PROJECT or config.get("repository") != REPOSITORY or config.get("ref") != REF:
        raise RuntimeError("Trusted GitLab CD is restricted to one HR development project.")
    runner = config["runnerId"]
    if not isinstance(runner, int) or runner < 1:
        raise RuntimeError("A fixed project runner identity is required.")
    token_file = Path("/etc/fanasa-organization-hr/gitlab-cd.token")
    token = protected_file(token_file).decode().strip()
    if not token or any(char.isspace() for char in token):
        raise RuntimeError("GitLab CD credential is invalid.")
    api = Api(token, "/usr/local/share/ca-certificates/fanasa-local.crt")
    protected_directory(CONFIG.parent)
    protected_directory(STATE.parent)
    STATE.mkdir(parents=True, exist_ok=True, mode=0o700)
    protected_directory(STATE)
    protected_directory(ARTIFACTS)
    lock_path = STATE / "poll.lock"
    cached_file(lock_path)
    descriptor = os.open(lock_path, os.O_WRONLY | os.O_CREAT | os.O_APPEND | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, "a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        selected = select(api, runner)
        if selected is None:
            print("No successful HR pipeline for the current protected develop commit.")
            return
        pipeline, job = selected
        directory = ARTIFACTS / str(job["id"])
        directory.mkdir(mode=0o700, exist_ok=True)
        protected_directory(directory)
        receipt_path = directory / "release.json"
        cached_file(receipt_path)
        if not receipt_path.exists():
            incoming_receipt = directory / "release.json.incoming"
            cached_file(incoming_receipt)
            if incoming_receipt.exists():
                incoming_receipt.unlink()
            api.artifact(job["id"], "release.json", incoming_receipt, 65536)
            validate_receipt(json.loads(incoming_receipt.read_bytes()), pipeline["sha"])
            incoming_receipt.replace(receipt_path)
        receipt = json.loads(receipt_path.read_bytes())
        validate_receipt(receipt, pipeline["sha"])
        evidence = json.loads(api.request(f"jobs/{job['id']}/artifacts/.artifacts/hr-development/verification.json"))
        validate_evidence(evidence, pipeline, job)
        archive = directory / receipt["archive"]
        cached_file(archive)
        if not archive.exists():
            incoming = archive.with_suffix(".incoming")
            if incoming.exists():
                incoming.unlink()
            api.artifact(job["id"], receipt["archive"], incoming, MAX_ARCHIVE)
            incoming.replace(archive)
        subprocess.run(["python3", VERIFIER, "--archive", str(archive), "--receipt", str(receipt_path)], check=True)
        # Single-file API evidence must be the same bytes the root verifier found inside the bundle.
        with tarfile.open(archive, "r:gz") as bundle:
            embedded = bundle.extractfile("ci-verification.json").read(4 * 1024 * 1024 + 1)
        if len(embedded) > 4 * 1024 * 1024 or json.loads(embedded) != evidence:
            raise RuntimeError("The archive and this exact GitLab job disagree about CI evidence.")
        # Re-read protected HEAD after download/verification, immediately before starting activation.
        branch = api.json("repository/branches/develop")
        if branch.get("protected") is not True or branch["commit"]["id"] != pipeline["sha"]:
            print("Verified pipeline was superseded before activation; waiting for the current commit.")
            return
        release = receipt["release"]
        if CURRENT.resolve().name != release:
            subprocess.run([HELPER, "development", release, str(archive), receipt["sha256"]], check=True, timeout=900)
        if CURRENT.resolve().name != release:
            raise RuntimeError("The bounded helper did not activate the verified release.")
        # Equivalent builds share a release ID; use the artifact actually activated, never
        # relabel the running binaries with a later pipeline that the helper did not deploy.
        active_ci = CURRENT.resolve() / "ci-verification.json"
        cached_file(active_ci)
        active_data = active_ci.read_bytes()
        if len(active_data) > 4 * 1024 * 1024:
            raise RuntimeError("Active CI evidence exceeds its bounded size.")
        actual_pipeline_id, actual_job_id = deployed_origin(json.loads(active_data), pipeline["sha"])
        directory = ARTIFACTS / str(actual_job_id)
        protected_directory(directory)
        actual_receipt = directory / "release.json"
        cached_file(actual_receipt)
        receipt = json.loads(actual_receipt.read_bytes())
        validate_receipt(receipt, pipeline["sha"])
        deployment_path = CURRENT.parent / "deployments" / release / "deployment.json"
        cached_file(deployment_path)
        deployment = json.loads(deployment_path.read_bytes())
        if (deployment.get("status") != "published" or deployment.get("release") != release
                or deployment.get("archiveSha256") != receipt["sha256"]
                or deployment.get("verifiedSource", {}).get("sourceCommit") != pipeline["sha"]):
            raise RuntimeError("The active artifact and bounded deployment receipt must agree.")
        deployed_pipeline_url = f"{BASE}/{PROJECT_PATH}/-/pipelines/{actual_pipeline_id}"
        newest_branch = api.json("repository/branches/develop")
        newer_pending = newest_branch.get("commit", {}).get("id") != pipeline["sha"]
        now = datetime.now(timezone.utc).isoformat()
        cached_file(METADATA)
        value = json.loads(METADATA.read_bytes()) if METADATA.exists() else {}
        value.update({"releaseId": release, "pipelineStatus": "verified", "pipelineUrl": f"{BASE}/{PROJECT_PATH}/-/pipelines",
                      "lastSuccessfulRun": deployed_pipeline_url, "pipelineVerifiedAt": deployment["completedAt"],
                      "automationActive": True, "gitOpsPromotion": "adapter-pending"})
        value["pending"] = [item for item in value.get("pending", []) if "source CI" not in item and "Jenkins" not in item
                            and item != "internal GitLab CI/CD execution and clean source promotion"]
        atomic_json(METADATA, value)
        report = {"schemaVersion": "fanasa.organization-hr.gitlab-cd.v1", "status": "deployed", "projectId": PROJECT,
                  "pipelineId": actual_pipeline_id, "jobId": actual_job_id, "sourceCommit": pipeline["sha"],
                  "release": release, "archiveSha256": receipt["sha256"], "pipelineUrl": deployed_pipeline_url,
                  "completedAt": now, "newerCommitPending": newer_pending,
                  "mode": "bounded-systemd-development", "gitOpsPromotion": "adapter-pending"}
        atomic_json(directory / "deployment.json", report)
        print(json.dumps(report))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # API response bodies and request headers are never logged; they may contain credential information.
        print("HR trusted GitLab CD failed: " + type(error).__name__ + ": " + str(error), file=sys.stderr)
        sys.exit(1)
