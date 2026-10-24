# Verified official-image import

Use Python 3.12 or later. The importer accepts only the three pinned Linux/amd64 images in the official-image lock. Copy the archive, its external lock, and this script into the root-owned run directory, and provide the reviewed SHA256 values explicitly.

First verify the complete archive without accessing Docker:

```sh
python3 import-official-images.py \
  --archive images-linux-amd64.tar --archive-sha256 REVIEWED_ARCHIVE_SHA256 \
  --lock images-linux-amd64.lock.json --lock-sha256 REVIEWED_LOCK_SHA256 \
  --evidence image-verification.json --verify-only
```

For the root-owned VPS import, run the same command with a new evidence filename and omit `--verify-only`. The script requires the existing Linux/amd64 Docker server at `/var/run/docker.sock` and the existing Docker CLI. It streams the verified archive into `docker image load`, using `--platform linux/amd64` when the server supports API 1.48 or later.

Archive members are checked without extraction. Original compressed layer digests and sizes are verified, then the uncompressed tar bytes are streamed against ordered config DiffIDs with a 4 GiB limit per layer. The embedded lock must equal the external lock byte for byte. OCI and Docker tag mappings must both match the approved references.

The importer refuses to replace an existing tag whose identity, platform or DiffIDs differ. If all three images already match, it reports `already_present` and skips loading. After loading, it checks platform and ordered DiffIDs and accepts the config digest or selected manifest digest as the image ID, according to Docker's image store. It does not require the upstream multi-platform index digest.

Evidence filenames must be new. Evidence contains hashes, selected image identifiers, platform, DiffIDs and check outcomes; it excludes full configs, environments and raw Docker output. A failed or interrupted load can leave partial imports: inspect `loadAttempted` and `loadCompleted` in the evidence. The script performs no cleanup of images or other runtime objects.
