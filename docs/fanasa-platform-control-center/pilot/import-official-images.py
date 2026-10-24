#!/usr/bin/env python3
"""Verify a pinned official-image archive, then load it through existing Docker.

Uses only the Python standard library. --verify-only never accesses Docker.
Archive members and layer files are streamed; nothing is extracted.
"""

import argparse
import gzip
import hashlib
import http.client
import io
import json
import os
import re
import shutil
import socket
import stat
import subprocess
import sys
import tarfile
import zlib
from urllib.parse import quote


SCHEMA = "fanasa.official-image-lock.v1"
SOCKET_PATH = "/var/run/docker.sock"
DOCKER_HOST = "unix:///var/run/docker.sock"
CHUNK = 1024 * 1024
JSON_LIMIT = 20 * 1024 * 1024
LAYER_LIMIT = 4 * 1024**3
SHA = re.compile(r"sha256:[0-9a-f]{64}\Z")
HEX = re.compile(r"[0-9a-f]{64}\Z")
MANIFEST_TYPES = {
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
}
INDEX_TYPES = {
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
}
CONFIG_TYPES = {
    "application/vnd.oci.image.config.v1+json",
    "application/vnd.docker.container.image.v1+json",
}
GZIP_TYPES = {
    "application/vnd.oci.image.layer.v1.tar+gzip",
    "application/vnd.docker.image.rootfs.diff.tar.gzip",
}
RAW_TYPES = {
    "application/vnd.oci.image.layer.v1.tar",
    "application/vnd.docker.image.rootfs.diff.tar",
}
APPROVED = {
    "library/python": ("3.12.10-slim-bookworm", "python:3.12.10-slim-bookworm"),
    "library/redis": ("7.4.6-bookworm", "redis:7.4.6-bookworm"),
    "apache/apisix": ("3.14.1-debian", "apache/apisix:3.14.1-debian"),
}


class Refusal(Exception):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


def require(condition, code):
    if not condition:
        raise Refusal(code)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate_json_key")
        result[key] = value
    return result


def reject_constant(_value):
    raise Refusal("nonfinite_json_number")


def decode_json(raw):
    require(len(raw) <= JSON_LIMIT, "json_size_limit")
    try:
        return json.loads(raw, object_pairs_hook=unique_object,
                          parse_constant=reject_constant)
    except (ValueError, UnicodeError, RecursionError):
        raise Refusal("invalid_json") from None


def expected_hash(value):
    value = value.lower()
    require(HEX.fullmatch(value) is not None, "invalid_expected_sha256")
    return value


def descriptor(value, types):
    require(isinstance(value, dict), "invalid_descriptor")
    require(isinstance(value.get("digest"), str)
            and SHA.fullmatch(value["digest"]) is not None, "invalid_digest")
    size = value.get("size")
    require(type(size) is int and 0 <= size <= LAYER_LIMIT, "invalid_blob_size")
    require(value.get("mediaType") in types, "unsupported_media_type")
    return {key: value[key] for key in ("digest", "size", "mediaType")}


def same_descriptor(actual, expected, types):
    require(descriptor(actual, types) == descriptor(expected, types),
            "descriptor_mismatch")


def diff_id(value):
    require(isinstance(value, str) and SHA.fullmatch(value) is not None,
            "invalid_diff_id")
    return value


def blob_path(desc):
    return "blobs/sha256/" + desc["digest"].split(":", 1)[1]


def validate_lock(lock):
    require(isinstance(lock, dict) and lock.get("schemaVersion") == SCHEMA,
            "invalid_lock_schema")
    require(lock.get("platform") == {"os": "linux", "architecture": "amd64"},
            "lock_platform_mismatch")
    require(lock.get("registry") == "registry-1.docker.io", "registry_not_approved")
    images = lock.get("images")
    require(isinstance(images, list) and len(images) == len(APPROVED),
            "image_allowlist_mismatch")
    seen = set()
    expected = {}
    layers = {}
    for image in images:
        require(isinstance(image, dict), "invalid_image_record")
        repository = image.get("repository")
        require(repository in APPROVED and repository not in seen,
                "image_allowlist_mismatch")
        seen.add(repository)
        tag, docker_tag = APPROVED[repository]
        require(image.get("tag") == tag and image.get("dockerTag") == docker_tag
                and image.get("reference") == "docker.io/" + repository + ":" + tag,
                "image_allowlist_mismatch")
        source = image.get("sourceIndex")
        descs = [descriptor(image.get("manifest"), MANIFEST_TYPES),
                 descriptor(image.get("config"), CONFIG_TYPES),
                 descriptor(source, INDEX_TYPES)]
        image_layers = image.get("layers")
        image_diffs = image.get("diffIds")
        require(isinstance(image_layers, list) and len(image_layers) <= 256
                and isinstance(image_diffs, list)
                and len(image_layers) == len(image_diffs), "layer_count_mismatch")
        for layer, expected_diff in zip(image_layers, image_diffs):
            desc = descriptor(layer, GZIP_TYPES | RAW_TYPES)
            require(diff_id(layer.get("diffId")) == diff_id(expected_diff),
                    "lock_diff_id_mismatch")
            previous = layers.get(desc["digest"])
            current = (desc, expected_diff)
            require(previous is None or previous == current, "conflicting_shared_layer")
            layers[desc["digest"]] = current
            descs.append(desc)
        for desc in descs:
            path = blob_path(desc)
            require(path not in expected or expected[path] == desc,
                    "conflicting_blob_descriptor")
            expected[path] = desc
    require(type(lock.get("uniqueCompressedLayerBytes")) is int
            and lock["uniqueCompressedLayerBytes"] == sum(x[0]["size"] for x in layers.values()),
            "compressed_byte_total_mismatch")
    require(lock.get("totalUncompressedLayerBytes") is None,
            "unsupported_uncompressed_byte_total")
    return images, expected, layers


def stream_hash(stream):
    digest = hashlib.sha256()
    total = 0
    while True:
        chunk = stream.read(CHUNK)
        if not chunk:
            break
        total += len(chunk)
        digest.update(chunk)
    return digest.hexdigest(), total


class HashReader(io.RawIOBase):
    def __init__(self, source):
        self.source = source
        self.digest = hashlib.sha256()
        self.count = 0

    def readable(self):
        return True

    def read(self, size=-1):
        raw = self.source.read(size)
        self.digest.update(raw)
        self.count += len(raw)
        return raw


def verify_layer(source, desc, expected_diff):
    raw = HashReader(source)
    content = gzip.GzipFile(fileobj=raw, mode="rb") if desc["mediaType"] in GZIP_TYPES else raw
    digest = hashlib.sha256()
    uncompressed = 0
    try:
        while True:
            chunk = content.read(CHUNK)
            if not chunk:
                break
            uncompressed += len(chunk)
            require(uncompressed <= LAYER_LIMIT, "uncompressed_layer_size_limit")
            digest.update(chunk)
        # Account for the complete original blob even if a decoder buffered less.
        while raw.read(CHUNK):
            pass
    except (OSError, EOFError, zlib.error):
        raise Refusal("invalid_layer_compression") from None
    finally:
        if content is not raw:
            content.close()
    require(raw.count == desc["size"]
            and "sha256:" + raw.digest.hexdigest() == desc["digest"],
            "layer_blob_mismatch")
    require("sha256:" + digest.hexdigest() == expected_diff, "layer_diff_id_mismatch")
    return uncompressed


def verify_archive(stream, images, expected, layers, lock_raw, evidence):
    seen = set()
    metadata = {}
    uncompressed_bytes = 0
    try:
        with tarfile.open(fileobj=stream, mode="r|*") as archive:
            for member in archive:
                name = member.name
                require(name and "\\" not in name and not name.startswith("/")
                        and all(part not in ("", ".", "..") for part in name.rstrip("/").split("/")),
                        "unsafe_archive_path")
                canonical = name.rstrip("/") if member.isdir() else name
                require(canonical not in seen, "duplicate_archive_member")
                seen.add(canonical)
                if member.isdir():
                    require(canonical in ("blobs", "blobs/sha256") and member.size == 0,
                            "unexpected_archive_directory")
                    continue
                require(member.type in (tarfile.REGTYPE, tarfile.AREGTYPE), "unsafe_archive_type")
                require(name in expected or name in
                        ("oci-layout", "index.json", "manifest.json", "fanasa-image-lock.json"),
                        "unexpected_archive_member")
                source = archive.extractfile(member)
                require(source is not None, "unreadable_archive_member")
                if name in expected:
                    desc = expected[name]
                    require(member.size == desc["size"], "blob_size_mismatch")
                    if desc["digest"] in layers:
                        layer_desc, layer_diff = layers[desc["digest"]]
                        uncompressed_bytes += verify_layer(source, layer_desc, layer_diff)
                    else:
                        require(member.size <= JSON_LIMIT, "json_size_limit")
                        raw = source.read(JSON_LIMIT + 1)
                        require(len(raw) == desc["size"]
                                and "sha256:" + hashlib.sha256(raw).hexdigest() == desc["digest"],
                                "metadata_blob_mismatch")
                        metadata[name] = decode_json(raw)
                else:
                    require(member.size <= JSON_LIMIT, "json_size_limit")
                    raw = source.read(JSON_LIMIT + 1)
                    require(len(raw) == member.size, "archive_member_size_mismatch")
                    if name == "fanasa-image-lock.json":
                        require(raw == lock_raw, "embedded_lock_mismatch")
                    else:
                        metadata[name] = decode_json(raw)
    except tarfile.TarError:
        raise Refusal("invalid_tar_archive") from None
    require(set(expected).issubset(seen)
            and {"oci-layout", "index.json", "manifest.json", "fanasa-image-lock.json"}.issubset(seen),
            "missing_archive_member")
    require(metadata["oci-layout"] == {"imageLayoutVersion": "1.0.0"}, "invalid_oci_layout")
    oci_index = metadata["index.json"]
    require(isinstance(oci_index, dict) and oci_index.get("schemaVersion") == 2
            and oci_index.get("mediaType") == "application/vnd.oci.image.index.v1+json",
            "invalid_oci_index")
    entries = oci_index.get("manifests")
    require(isinstance(entries, list) and len(entries) == len(images), "oci_tag_mapping_mismatch")
    oci_by_name = {}
    for entry in entries:
        require(isinstance(entry, dict) and isinstance(entry.get("annotations"), dict),
                "oci_tag_mapping_mismatch")
        reference = entry["annotations"].get("io.containerd.image.name")
        require(isinstance(reference, str) and reference not in oci_by_name,
                "oci_tag_mapping_mismatch")
        oci_by_name[reference] = entry
    docker_entries = metadata["manifest.json"]
    require(isinstance(docker_entries, list) and len(docker_entries) == len(images),
            "docker_tag_mapping_mismatch")
    docker_by_tag = {}
    for entry in docker_entries:
        require(isinstance(entry, dict) and isinstance(entry.get("RepoTags"), list)
                and len(entry["RepoTags"]) == 1 and isinstance(entry["RepoTags"][0], str)
                and entry["RepoTags"][0] not in docker_by_tag, "docker_tag_mapping_mismatch")
        docker_by_tag[entry["RepoTags"][0]] = entry
    for image in images:
        manifest = metadata[blob_path(image["manifest"])]
        require(isinstance(manifest, dict) and manifest.get("schemaVersion") == 2
                and manifest.get("mediaType") == image["manifest"]["mediaType"],
                "invalid_image_manifest")
        same_descriptor(manifest.get("config"), image["config"], CONFIG_TYPES)
        actual_layers = manifest.get("layers")
        require(isinstance(actual_layers, list) and len(actual_layers) == len(image["layers"]),
                "manifest_layer_count_mismatch")
        for actual, expected_layer in zip(actual_layers, image["layers"]):
            same_descriptor(actual, expected_layer, GZIP_TYPES | RAW_TYPES)
        config = metadata[blob_path(image["config"])]
        require(isinstance(config, dict) and config.get("os") == "linux"
                and config.get("architecture") == "amd64", "config_platform_mismatch")
        require(config.get("rootfs") == {"type": "layers", "diff_ids": image["diffIds"]},
                "config_diff_id_mismatch")
        source = image.get("sourceIndex")
        if source is not None:
            source_index = metadata[blob_path(source)]
            require(isinstance(source_index, dict) and source_index.get("schemaVersion") == 2
                    and source_index.get("mediaType") == source["mediaType"], "invalid_source_index")
            candidates = source_index.get("manifests")
            require(isinstance(candidates, list), "invalid_source_index")
            selected = [item for item in candidates if isinstance(item, dict)
                        and isinstance(item.get("platform"), dict)
                        and item["platform"].get("os") == "linux"
                        and item["platform"].get("architecture") == "amd64"
                        and not item["platform"].get("variant")]
            require(len(selected) == 1, "source_platform_ambiguous")
            same_descriptor(selected[0], image["manifest"], MANIFEST_TYPES)
        entry = oci_by_name.get(image["reference"])
        require(isinstance(entry, dict)
                and entry.get("platform") == {"os": "linux", "architecture": "amd64"}
                and entry["annotations"].get("org.opencontainers.image.ref.name") == image["tag"],
                "oci_tag_mapping_mismatch")
        same_descriptor(entry, image["manifest"], MANIFEST_TYPES)
        entry = docker_by_tag.get(image["dockerTag"])
        require(isinstance(entry, dict) and entry.get("Config") == blob_path(image["config"])
                and entry.get("Layers") == [blob_path(layer) for layer in image["layers"]]
                and set(entry) == {"Config", "RepoTags", "Layers"}, "docker_tag_mapping_mismatch")
    evidence["verifiedCompressedLayerBytes"] = sum(value[0]["size"] for value in layers.values())
    evidence["verifiedUncompressedLayerBytes"] = uncompressed_bytes
    evidence["cases"].extend({"name": name, "passed": True} for name in (
        "tar_paths_types_members", "embedded_lock", "blob_digests_sizes",
        "source_platform_selection", "manifest_descriptors", "config_platform_diff_ids",
        "streamed_layer_diff_ids", "oci_tag_mapping", "docker_tag_mapping"))


class UnixDocker(http.client.HTTPConnection):
    def __init__(self):
        super().__init__("localhost", timeout=15)

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(SOCKET_PATH)


def docker_get(path, allow_missing=False):
    connection = UnixDocker()
    try:
        connection.request("GET", path, headers={"Accept": "application/json"})
        response = connection.getresponse()
        if response.status == 404 and allow_missing:
            return None
        require(response.status == 200, "docker_read_failed")
        raw = response.read(JSON_LIMIT + 1)
        return decode_json(raw)
    except (OSError, http.client.HTTPException):
        raise Refusal("docker_unavailable") from None
    finally:
        connection.close()


def inspect_image(image):
    return docker_get("/images/" + quote(image["dockerTag"], safe="") + "/json", allow_missing=True)


def checked_inspection(actual, image, require_present):
    if actual is None:
        require(not require_present, "loaded_image_missing")
        return None
    require(isinstance(actual, dict), "invalid_docker_inspection")
    identity = actual.get("Id")
    require(identity in (image["config"]["digest"], image["manifest"]["digest"]),
            "existing_tag_differs" if not require_present else "loaded_identity_mismatch")
    require(actual.get("Os") == "linux" and actual.get("Architecture") == "amd64",
            "docker_platform_mismatch")
    rootfs = actual.get("RootFS")
    require(isinstance(rootfs, dict) and rootfs.get("Type") == "layers"
            and rootfs.get("Layers") == image["diffIds"], "docker_diff_id_mismatch")
    digests = actual.get("RepoDigests", [])
    require(isinstance(digests, list), "invalid_docker_inspection")
    approved_prefixes = (image["reference"].split(":", 1)[0] + "@",
                         image["dockerTag"].split(":", 1)[0] + "@")
    clean_digests = [value for value in digests if isinstance(value, str)
                     and value.startswith(approved_prefixes)
                     and SHA.fullmatch(value.rsplit("@", 1)[-1]) is not None]
    return {"dockerTag": image["dockerTag"], "actualImageId": identity,
            "actualRepoDigests": clean_digests,
            "platform": {"os": "linux", "architecture": "amd64"},
            "rootfsDiffIds": image["diffIds"],
            "manifestDigest": image["manifest"]["digest"],
            "configDigest": image["config"]["digest"]}


def file_identity(stream):
    info = os.fstat(stream.fileno())
    require(stat.S_ISREG(info.st_mode), "archive_not_regular_file")
    return info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns


def import_images(stream, identity, images, evidence):
    require(sys.platform.startswith("linux") and hasattr(socket, "AF_UNIX"),
            "import_requires_linux")
    try:
        require(stat.S_ISSOCK(os.stat(SOCKET_PATH).st_mode), "docker_socket_missing")
    except OSError:
        raise Refusal("docker_socket_missing") from None
    docker = shutil.which("docker")
    require(docker is not None, "docker_cli_missing")
    evidence["dockerAccessed"] = True
    version = docker_get("/version")
    require(isinstance(version, dict) and version.get("Os") == "linux"
            and version.get("Arch") == "amd64", "docker_server_platform_mismatch")
    api = version.get("ApiVersion")
    require(isinstance(api, str) and re.fullmatch(r"[0-9]+\.[0-9]+", api),
            "invalid_docker_api_version")
    api_parts = tuple(int(part) for part in api.split("."))
    evidence["dockerApiVersion"] = api
    before = [checked_inspection(inspect_image(image), image, False) for image in images]
    evidence["cases"].append({"name": "existing_tags_compatible", "passed": True})
    require(file_identity(stream) == identity, "archive_changed")
    if all(item is not None for item in before):
        evidence["images"] = before
        evidence["status"] = "already_present"
        return
    args = [docker, "--host", DOCKER_HOST, "image", "load"]
    if api_parts >= (1, 48):
        args.extend(["--platform", "linux/amd64"])
    evidence["loadPlatformFilterUsed"] = api_parts >= (1, 48)
    stream.seek(0)
    evidence["loadAttempted"] = True
    # Explicit local socket and minimal environment ignore contexts and registry credentials.
    try:
        result = subprocess.run(args, stdin=stream, stdout=subprocess.DEVNULL,
                                stderr=subprocess.DEVNULL, timeout=900,
                                env={"PATH": os.environ.get("PATH", "")}, check=False)
    except subprocess.TimeoutExpired:
        raise Refusal("docker_load_timeout") from None
    except OSError:
        raise Refusal("docker_load_unavailable") from None
    require(result.returncode == 0, "docker_load_failed")
    evidence["loadCompleted"] = True
    require(file_identity(stream) == identity, "archive_changed")
    evidence["images"] = [checked_inspection(inspect_image(image), image, True) for image in images]
    evidence["cases"].append({"name": "loaded_identity_platform_diff_ids", "passed": True})
    evidence["status"] = "imported"


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True)
    parser.add_argument("--archive-sha256", required=True)
    parser.add_argument("--lock", required=True)
    parser.add_argument("--lock-sha256", required=True)
    parser.add_argument("--evidence", required=True)
    parser.add_argument("--verify-only", action="store_true")
    return parser.parse_args()


def main():
    args = parse_args()
    evidence = {"schemaVersion": "fanasa.official-image-import-evidence.v1", "status": "refused",
                "verifyOnly": args.verify_only, "dockerAccessed": False,
                "loadAttempted": False, "loadCompleted": False, "cases": [], "images": []}
    exit_code = 1
    try:
        # Exclusive evidence creation refuses accidental replacement of existing evidence.
        evidence_file = open(args.evidence, "x", encoding="utf-8")
    except OSError:
        print(json.dumps({"status": "refused", "failure": "evidence_write_failed"}))
        return 1
    try:
        lock_expected = expected_hash(args.lock_sha256)
        archive_expected = expected_hash(args.archive_sha256)
        with open(args.lock, "rb") as lock_file:
            lock_raw = lock_file.read(JSON_LIMIT + 1)
        require(len(lock_raw) <= JSON_LIMIT, "lock_size_limit")
        lock_actual = hashlib.sha256(lock_raw).hexdigest()
        evidence["lockSha256"] = lock_actual
        require(lock_actual == lock_expected, "lock_sha256_mismatch")
        lock = decode_json(lock_raw)
        images, expected, layers = validate_lock(lock)
        evidence["platform"] = lock["platform"]
        evidence["cases"].append({"name": "lock_sha256_schema_allowlist", "passed": True})
        with open(args.archive, "rb") as archive:
            identity = file_identity(archive)
            archive_actual, archive_size = stream_hash(archive)
            evidence["archiveSha256"] = archive_actual
            evidence["archiveBytes"] = archive_size
            require(archive_actual == archive_expected, "archive_sha256_mismatch")
            evidence["cases"].append({"name": "archive_sha256", "passed": True})
            archive.seek(0)
            verify_archive(archive, images, expected, layers, lock_raw, evidence)
            require(file_identity(archive) == identity, "archive_changed")
            if args.verify_only:
                evidence["status"] = "verified"
                evidence["images"] = [{"dockerTag": image["dockerTag"],
                                       "manifestDigest": image["manifest"]["digest"],
                                       "configDigest": image["config"]["digest"],
                                       "rootfsDiffIds": image["diffIds"], "actualImageId": None}
                                      for image in images]
            else:
                import_images(archive, identity, images, evidence)
        exit_code = 0
    except Refusal as error:
        evidence["failure"] = {"code": error.code}
    except KeyboardInterrupt:
        evidence["failure"] = {"code": "interrupted"}
    except Exception:
        evidence["failure"] = {"code": "invalid_input_or_runtime_failure"}
    finally:
        try:
            json.dump(evidence, evidence_file, indent=2, sort_keys=True)
            evidence_file.write("\n")
            evidence_file.close()
        except OSError:
            print(json.dumps({"status": "refused", "failure": "evidence_write_failed"}))
            return 1
    print(json.dumps({"status": evidence["status"], "casesPassed": len(evidence["cases"]),
                      "dockerAccessed": evidence["dockerAccessed"],
                      "loadAttempted": evidence["loadAttempted"],
                      "failure": evidence.get("failure")}))
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
