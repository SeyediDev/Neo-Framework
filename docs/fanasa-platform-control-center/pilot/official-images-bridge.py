"""Export only three locked official Linux/amd64 images without a Docker daemon.

Anonymous pull tokens and signed blob URLs stay in memory. Fetched bytes keep
their actual media types/digests. Output is a deterministic hybrid OCI/Docker tar.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import gzip
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import tarfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

MIB, GIB = 1024 ** 2, 1024 ** 3
REGISTRY = "registry-1.docker.io"
IMAGES = (("library/python", "3.12.10-slim-bookworm", "python:3.12.10-slim-bookworm"),
          ("library/redis", "7.4.6-bookworm", "redis:7.4.6-bookworm"),
          ("apache/apisix", "3.14.1-debian", "apache/apisix:3.14.1-debian"))
INDEX_TYPES = {"application/vnd.oci.image.index.v1+json",
               "application/vnd.docker.distribution.manifest.list.v2+json"}
MANIFEST_TYPES = {"application/vnd.oci.image.manifest.v1+json",
                  "application/vnd.docker.distribution.manifest.v2+json"}
CONFIG_TYPES = {"application/vnd.oci.image.config.v1+json",
                "application/vnd.docker.container.image.v1+json"}
GZIP_TYPES = {"application/vnd.oci.image.layer.v1.tar+gzip",
              "application/vnd.docker.image.rootfs.diff.tar.gzip"}
RAW_TYPES = {"application/vnd.oci.image.layer.v1.tar", "application/vnd.docker.image.rootfs.diff.tar"}
DIGEST = re.compile(r"^sha256:[a-f0-9]{64}$")
MAX_METADATA, MAX_LAYER, MAX_COMPRESSED, MAX_UNCOMPRESSED_LAYER = 2 * MIB, 512 * MIB, 2 * GIB, 4 * GIB
RESERVE = 4 * GIB


class BridgeError(Exception):
    def __init__(self, reason):
        self.reason = reason
        super().__init__(reason)


def require(condition, reason):
    if not condition:
        raise BridgeError(reason)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode()


def digest(data):
    return "sha256:" + hashlib.sha256(data).hexdigest()


def descriptor(value, types=None):
    require(isinstance(value, dict) and DIGEST.fullmatch(value.get("digest", ""))
            and isinstance(value.get("size"), int) and 0 < value["size"]
            and isinstance(value.get("mediaType"), str), "invalid_descriptor")
    require(not value.get("urls"), "foreign_blob_urls_forbidden")
    if types is not None:
        require(value["mediaType"] in types, "unsupported_media_type")
    return {key: value[key] for key in ("digest", "size", "mediaType")}


def blob_path(work, sha):
    require(DIGEST.fullmatch(sha), "invalid_blob_digest")
    for ancestor in (work, work / "blobs", work / "blobs" / "sha256"):
        require(not ancestor.is_symlink(), "cache_ancestor_symlink_forbidden")
        require(not ancestor.exists() or ancestor.is_dir(), "cache_ancestor_not_directory")
    result = work / "blobs" / "sha256" / sha.split(":", 1)[1]
    require(not result.is_symlink(), "cache_symlink_forbidden")
    return result


def file_digest(path, budget=None):
    result, size = hashlib.sha256(), 0
    with path.open("rb") as stream:
        while chunk := stream.read(MIB):
            if budget:
                budget.check()
            result.update(chunk)
            size += len(chunk)
    return "sha256:" + result.hexdigest(), size


def verify_file(path, expected, budget=None):
    require(path.is_file() and not path.is_symlink(), "verified_blob_missing")
    actual, size = file_digest(path, budget)
    require(actual == expected["digest"] and size == expected["size"], "blob_hash_or_size_mismatch")


def disk_guard(path, required=0):
    current = path if path.is_dir() else path.parent
    require(shutil.disk_usage(current).free >= RESERVE + required, "disk_headroom_below_4gib")


def atomic_bytes(path, data):
    require(not path.exists() and not path.is_symlink(), "output_already_exists")
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".partial-" + uuid.uuid4().hex)
    try:
        with temporary.open("xb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


class Budget:
    def __init__(self, seconds):
        require(1 <= seconds <= 1800, "invalid_time_budget")
        self.deadline = time.monotonic() + seconds

    def check(self):
        require(time.monotonic() < self.deadline, "job_deadline_expired")

    def timeout(self):
        self.check()
        return max(1, min(30, self.deadline - time.monotonic()))


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        require(urllib.parse.urlsplit(newurl).scheme == "https", "insecure_blob_redirect")
        target = super().redirect_request(req, fp, code, msg, headers, newurl)
        if target is not None and urllib.parse.urlsplit(newurl).hostname != REGISTRY:
            target.remove_header("Authorization")
        return target


class Registry:
    def __init__(self, budget):
        self.budget = budget
        self.opener = urllib.request.build_opener(SafeRedirect())
        self.tokens = {}

    def token(self, repository):
        current = self.tokens.get(repository)
        if current and current[1] > time.monotonic() + 30:
            return current[0]
        query = urllib.parse.urlencode({"service": "registry.docker.io", "scope": "repository:" + repository + ":pull"})
        with self.opener.open("https://auth.docker.io/token?" + query, timeout=self.budget.timeout()) as response:
            raw = response.read(262145)
            require(len(raw) <= 262144, "auth_response_limit")
            value = json.loads(raw)
        token = value.get("token") or value.get("access_token")
        require(isinstance(token, str) and token, "anonymous_pull_token_unavailable")
        self.tokens[repository] = (token, time.monotonic() + min(int(value.get("expires_in", 300)), 300))
        return token

    def response(self, repository, suffix):
        require(repository in {item[0] for item in IMAGES}, "non_allowlisted_repository")
        req = urllib.request.Request("https://" + REGISTRY + "/v2/" + repository + "/" + suffix,
                                     headers={"Accept": ", ".join(sorted(INDEX_TYPES | MANIFEST_TYPES)),
                                              "User-Agent": "FanasaOfficialImageBridge/1.0"})
        # Unredirected headers avoid sending anonymous registry tokens to blob CDNs.
        req.add_unredirected_header("Authorization", "Bearer " + self.token(repository))
        return self.opener.open(req, timeout=self.budget.timeout())

    def metadata(self, repository, suffix, expected=None):
        for attempt in range(2):
            self.budget.check()
            try:
                with self.response(repository, suffix) as response:
                    raw = response.read(MAX_METADATA + 1)
                    require(len(raw) <= MAX_METADATA, "metadata_size_limit")
                    if suffix.startswith("manifests/"):
                        value = json.loads(raw)
                        media_type = response.headers.get("Content-Type", "").split(";", 1)[0].strip().lower()
                        require(isinstance(value, dict)
                                and value.get("mediaType") in INDEX_TYPES | MANIFEST_TYPES
                                and media_type == value["mediaType"], "registry_manifest_content_type_mismatch")
                        require(expected is None or value["mediaType"] == expected["mediaType"],
                                "registry_manifest_content_type_mismatch")
                        actual = digest(raw)
                        require(response.headers.get("Docker-Content-Digest") == actual, "registry_manifest_header_mismatch")
                    if expected is not None:
                        require(digest(raw) == expected["digest"] and len(raw) == expected["size"], "metadata_hash_or_size_mismatch")
                    return raw
            except urllib.error.HTTPError as error:
                if error.code == 401:
                    self.tokens.pop(repository, None)
                if attempt or error.code not in (401, 429, 500, 502, 503, 504):
                    raise
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                if attempt:
                    raise
            time.sleep(1)
        raise BridgeError("metadata_retry_exhausted")

    def layer(self, repository, expected, work):
        path = blob_path(work, expected["digest"])
        if path.exists():
            verify_file(path, expected, self.budget)
            return path, False
        require(expected["size"] <= MAX_LAYER, "layer_size_limit")
        path.parent.mkdir(parents=True, exist_ok=True)
        for attempt in range(2):
            self.budget.check()
            disk_guard(path, expected["size"])
            temporary = path.with_name(path.name + ".partial-" + uuid.uuid4().hex)
            try:
                hashed, size = hashlib.sha256(), 0
                with self.response(repository, "blobs/" + expected["digest"]) as response, temporary.open("xb") as stream:
                    length = response.headers.get("Content-Length")
                    require(not length or int(length) == expected["size"], "blob_content_length_mismatch")
                    while chunk := response.read(MIB):
                        self.budget.check()
                        size += len(chunk)
                        require(size <= expected["size"], "blob_exceeds_descriptor_size")
                        disk_guard(temporary)
                        hashed.update(chunk)
                        stream.write(chunk)
                    stream.flush()
                    os.fsync(stream.fileno())
                require(size == expected["size"] and "sha256:" + hashed.hexdigest() == expected["digest"], "blob_hash_or_size_mismatch")
                os.replace(temporary, path)
                return path, True
            except urllib.error.HTTPError as error:
                if error.code == 401:
                    self.tokens.pop(repository, None)
                if attempt or error.code not in (401, 429, 500, 502, 503, 504):
                    raise
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                if attempt:
                    raise
            finally:
                if temporary.exists():
                    temporary.unlink()
            time.sleep(1)
        raise BridgeError("blob_retry_exhausted")


def cache_metadata(work, raw):
    path = blob_path(work, digest(raw))
    expected = {"digest": digest(raw), "size": len(raw)}
    if path.exists():
        verify_file(path, expected)
    else:
        atomic_bytes(path, raw)


def unique_layers(lock):
    result = {}
    for image in lock["images"]:
        for layer in image["layers"]:
            previous = result.get(layer["digest"])
            require(previous is None or previous[:2] == (layer, image["repository"])
                    or (previous[0] == layer), "inconsistent_shared_layer")
            result.setdefault(layer["digest"], (layer, image["repository"]))
    return result


def lock_images(work, output, registry):
    require(not output.exists(), "lock_output_already_exists")
    work.mkdir(parents=True, exist_ok=True)
    disk_guard(work)
    images = []
    for repository, tag, docker_tag in IMAGES:
        raw_index = registry.metadata(repository, "manifests/" + tag)
        index = json.loads(raw_index)
        require(index.get("schemaVersion") == 2 and index.get("mediaType") in INDEX_TYPES, "unsupported_tag_index")
        source_index = {"digest": digest(raw_index), "size": len(raw_index), "mediaType": index["mediaType"]}
        matches = [value for value in index.get("manifests", [])
                   if value.get("platform", {}).get("os") == "linux"
                   and value.get("platform", {}).get("architecture") == "amd64"
                   and not value.get("platform", {}).get("variant")]
        require(len(matches) == 1, "ambiguous_linux_amd64_manifest")
        manifest_descriptor = descriptor(matches[0], MANIFEST_TYPES)
        raw_manifest = registry.metadata(repository, "manifests/" + manifest_descriptor["digest"], manifest_descriptor)
        manifest = json.loads(raw_manifest)
        require(manifest.get("schemaVersion") == 2 and manifest.get("mediaType") == manifest_descriptor["mediaType"], "manifest_media_type_mismatch")
        config_descriptor = descriptor(manifest["config"], CONFIG_TYPES)
        raw_config = registry.metadata(repository, "blobs/" + config_descriptor["digest"], config_descriptor)
        config = json.loads(raw_config)
        require(config.get("os") == "linux" and config.get("architecture") == "amd64"
                and config.get("rootfs", {}).get("type") == "layers", "config_platform_or_rootfs_mismatch")
        diff_ids = config["rootfs"]["diff_ids"]
        require(isinstance(diff_ids, list) and all(DIGEST.fullmatch(value) for value in diff_ids), "invalid_diff_ids")
        layers = [descriptor(value, GZIP_TYPES | RAW_TYPES) for value in manifest["layers"]]
        require(len(layers) == len(diff_ids) and layers, "layer_diff_id_count_mismatch")
        for layer, diff_id in zip(layers, diff_ids):
            require(layer["size"] <= MAX_LAYER, "layer_size_limit")
            layer["diffId"] = diff_id
        for raw in (raw_index, raw_manifest, raw_config):
            cache_metadata(work, raw)
        image = {"repository": repository, "tag": tag, "reference": "docker.io/" + repository + ":" + tag,
                 "dockerTag": docker_tag, "sourceIndex": source_index, "manifest": manifest_descriptor,
                 "config": config_descriptor, "layers": layers, "diffIds": diff_ids}
        images.append(image)
        print(json.dumps({"stage": "locked-metadata", "image": docker_tag,
                          "manifestDigest": manifest_descriptor["digest"], "layerCount": len(layers)}), flush=True)
    lock = {"schemaVersion": "fanasa.official-image-lock.v1", "platform": {"os": "linux", "architecture": "amd64"},
            "registry": REGISTRY, "observedAtUtc": datetime.now(timezone.utc).isoformat(),
            "images": images, "totalUncompressedLayerBytes": None}
    layers = unique_layers(lock)
    lock["uniqueCompressedLayerBytes"] = sum(value[0]["size"] for value in layers.values())
    require(lock["uniqueCompressedLayerBytes"] <= MAX_COMPRESSED, "compressed_image_set_limit")
    raw = canonical(lock) + b"\n"
    atomic_bytes(output, raw)
    print(json.dumps({"stage": "lock-complete", "lockSha256": digest(raw).split(":")[1],
                      "imageCount": len(images), "uniqueLayerCount": len(layers),
                      "uniqueCompressedLayerBytes": lock["uniqueCompressedLayerBytes"],
                      "totalUncompressedLayerBytes": None}), flush=True)


def load_lock(path, expected_sha, work, budget):
    with path.open("rb") as stream:
        raw = stream.read(MAX_METADATA + 1)
    require(len(raw) <= MAX_METADATA and digest(raw) == "sha256:" + expected_sha, "lock_hash_or_size_mismatch")
    lock = json.loads(raw)
    require(lock.get("schemaVersion") == "fanasa.official-image-lock.v1" and lock.get("registry") == REGISTRY
            and lock.get("platform") == {"os": "linux", "architecture": "amd64"}
            and [(image["repository"], image["tag"], image["dockerTag"]) for image in lock["images"]] == list(IMAGES),
            "lock_scope_mismatch")
    for image in lock["images"]:
        require(image["reference"] == "docker.io/" + image["repository"] + ":" + image["tag"], "lock_reference_mismatch")
        for key, types in (("sourceIndex", INDEX_TYPES), ("manifest", MANIFEST_TYPES), ("config", CONFIG_TYPES)):
            value = descriptor(image[key], types)
            verify_file(blob_path(work, value["digest"]), value, budget)
        index = json.loads(blob_path(work, image["sourceIndex"]["digest"]).read_bytes())
        matches = [descriptor(value, MANIFEST_TYPES) for value in index.get("manifests", [])
                   if value.get("platform", {}).get("os") == "linux"
                   and value.get("platform", {}).get("architecture") == "amd64"
                   and not value.get("platform", {}).get("variant")]
        require(matches == [image["manifest"]], "locked_index_selection_mismatch")
        manifest = json.loads(blob_path(work, image["manifest"]["digest"]).read_bytes())
        config = json.loads(blob_path(work, image["config"]["digest"]).read_bytes())
        require(manifest["mediaType"] == image["manifest"]["mediaType"]
                and descriptor(manifest["config"], CONFIG_TYPES) == image["config"], "locked_manifest_mismatch")
        require(config.get("os") == "linux" and config.get("architecture") == "amd64"
                and config.get("rootfs", {}).get("type") == "layers"
                and config.get("rootfs", {}).get("diff_ids") == image["diffIds"], "locked_config_mismatch")
        require(len(image["layers"]) == len(manifest["layers"]) == len(image["diffIds"]), "locked_layer_count_mismatch")
        for layer, original, diff_id in zip(image["layers"], manifest["layers"], image["diffIds"]):
            require(descriptor(layer, GZIP_TYPES | RAW_TYPES) == descriptor(original, GZIP_TYPES | RAW_TYPES)
                    and layer.get("diffId") == diff_id and DIGEST.fullmatch(diff_id), "locked_layer_mismatch")
    require(sum(value[0]["size"] for value in unique_layers(lock).values()) == lock["uniqueCompressedLayerBytes"]
            and lock["uniqueCompressedLayerBytes"] <= MAX_COMPRESSED, "locked_total_size_mismatch")
    return lock, raw


def uncompressed_layer(path, layer, budget):
    opener = gzip.open if layer["mediaType"] in GZIP_TYPES else Path.open
    hashed, size = hashlib.sha256(), 0
    with (gzip.open(path, "rb") if opener is gzip.open else path.open("rb")) as stream:
        while chunk := stream.read(MIB):
            budget.check()
            size += len(chunk)
            require(size <= MAX_UNCOMPRESSED_LAYER, "uncompressed_layer_limit")
            hashed.update(chunk)
    require("sha256:" + hashed.hexdigest() == layer["diffId"], "uncompressed_diff_id_mismatch")
    return size


def archive_models(lock):
    oci = {"schemaVersion": 2, "mediaType": "application/vnd.oci.image.index.v1+json", "manifests": []}
    docker = []
    for image in lock["images"]:
        oci["manifests"].append({**image["manifest"], "platform": {"os": "linux", "architecture": "amd64"},
                                "annotations": {"io.containerd.image.name": image["reference"],
                                                "org.opencontainers.image.ref.name": image["tag"]}})
        docker.append({"Config": "blobs/sha256/" + image["config"]["digest"].split(":")[1],
                       "RepoTags": [image["dockerTag"]],
                       "Layers": ["blobs/sha256/" + layer["digest"].split(":")[1] for layer in image["layers"]]})
    return oci, docker


def tar_info(name, size=0, directory=False):
    entry = tarfile.TarInfo(name)
    entry.size, entry.mtime, entry.uid, entry.gid = size, 0, 0, 0
    entry.uname = entry.gname = ""
    entry.mode = 0o755 if directory else 0o644
    if directory:
        entry.type = tarfile.DIRTYPE
    return entry


def export_images(lock_path, lock_sha, work, output, registry):
    lock, lock_raw = load_lock(lock_path, lock_sha, work, registry.budget)
    require(not output.exists(), "archive_output_already_exists")
    output.parent.mkdir(parents=True, exist_ok=True)
    layers = unique_layers(lock)
    # Conservative allowance for both cache and tar plus metadata/tar overhead.
    disk_guard(work, 2 * lock["uniqueCompressedLayerBytes"] + 16 * MIB)
    disk_guard(output.parent, lock["uniqueCompressedLayerBytes"] + 16 * MIB)
    uncompressed = {}
    for sha, (layer, repository) in layers.items():
        path, downloaded = registry.layer(repository, layer, work)
        size = uncompressed_layer(path, layer, registry.budget)
        uncompressed[sha] = size
        print(json.dumps({"stage": "verified-layer", "digest": sha, "compressedBytes": layer["size"],
                          "uncompressedBytes": size, "downloaded": downloaded}), flush=True)
    oci, docker = archive_models(lock)
    additions = {"oci-layout": canonical({"imageLayoutVersion": "1.0.0"}),
                 "index.json": canonical(oci), "manifest.json": canonical(docker),
                 "fanasa-image-lock.json": lock_raw}
    expected = {}
    for image in lock["images"]:
        for value in [image["sourceIndex"], image["manifest"], image["config"], *image["layers"]]:
            expected[value["digest"]] = value
    temporary = output.with_name(output.name + ".partial-" + uuid.uuid4().hex)
    try:
        with tarfile.open(temporary, "w", format=tarfile.USTAR_FORMAT) as archive:
            archive.addfile(tar_info("blobs", directory=True))
            archive.addfile(tar_info("blobs/sha256", directory=True))
            for name in sorted(additions):
                archive.addfile(tar_info(name, len(additions[name])), io.BytesIO(additions[name]))
            for sha in sorted(expected):
                registry.budget.check()
                value = expected[sha]
                path = blob_path(work, sha)
                verify_file(path, value, registry.budget)
                disk_guard(temporary)
                with path.open("rb") as stream:
                    archive.addfile(tar_info("blobs/sha256/" + sha.split(":")[1], value["size"]), stream)
        archive_sha, archive_size = file_digest(temporary, registry.budget)
        os.replace(temporary, output)
    finally:
        if temporary.exists():
            temporary.unlink()
    report = {"schemaVersion": "fanasa.official-image-export.v1", "platform": lock["platform"],
              "archiveSha256": archive_sha.split(":")[1], "archiveBytes": archive_size, "lockSha256": lock_sha,
              "uniqueCompressedLayerBytes": lock["uniqueCompressedLayerBytes"],
              "uniqueUncompressedLayerBytes": sum(uncompressed.values()),
              "uncompressedLayerBytes": uncompressed, "manifestMediaTypesPreserved": True,
              "diffIdsVerified": True, "format": "hybrid OCI image layout plus Docker manifest.json",
              "images": [{"reference": image["reference"], "manifestDigest": image["manifest"]["digest"],
                          "configDigest": image["config"]["digest"], "diffIds": image["diffIds"]} for image in lock["images"]]}
    report_path = output.with_name(output.name + ".report.json")
    atomic_bytes(report_path, canonical(report) + b"\n")
    print(json.dumps({"stage": "export-complete", **report}), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    lock = commands.add_parser("lock", help="Fetch only official metadata/config blobs; no image layers.")
    lock.add_argument("--work-dir", required=True)
    lock.add_argument("--lock-file", required=True)
    lock.add_argument("--max-seconds", type=int, default=180)
    export = commands.add_parser("export", help="Fetch locked layers and write one verified offline archive.")
    export.add_argument("--work-dir", required=True)
    export.add_argument("--lock", required=True)
    export.add_argument("--lock-sha256", required=True)
    export.add_argument("--archive", required=True)
    export.add_argument("--max-seconds", type=int, default=900)
    args = parser.parse_args()
    try:
        work = Path(args.work_dir).resolve()
        require(not work.is_symlink(), "work_directory_symlink")
        registry = Registry(Budget(args.max_seconds))
        if args.command == "lock":
            lock_images(work, Path(args.lock_file).resolve(), registry)
        else:
            require(re.fullmatch(r"[a-f0-9]{64}", args.lock_sha256), "invalid_lock_sha256")
            export_images(Path(args.lock).resolve(), args.lock_sha256, work, Path(args.archive).resolve(), registry)
        return 0
    except Exception as error:
        status = {"stage": "bridge-failed", "errorClass": type(error).__name__}
        if isinstance(error, BridgeError):
            status["failure"] = error.reason
        elif isinstance(error, urllib.error.HTTPError):
            status["httpStatus"] = error.code
        elif isinstance(error, urllib.error.URLError):
            status["transportReasonClass"] = type(error.reason).__name__
        print(json.dumps(status), flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
