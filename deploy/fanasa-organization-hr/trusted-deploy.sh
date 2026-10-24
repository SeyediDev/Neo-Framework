#!/usr/bin/env bash
# Install this reviewed file outside the SCM workspace; source builds cannot alter trusted CD.
set -Eeuo pipefail
test "$#" -eq 1 || { printf 'One upstream build number is required.\n' >&2; exit 2; }
build_number="$1"
[[ "$build_number" =~ ^[1-9][0-9]{0,9}$ ]] || { printf 'Invalid upstream build number.\n' >&2; exit 2; }
artifact_root='/var/lib/fanasa-organization-hr/artifacts'
artifact_directory="${artifact_root}/${build_number}"
test -d "$artifact_directory" && test ! -L "$artifact_directory"
test -f "${artifact_directory}/release.json" && test ! -L "${artifact_directory}/release.json"
# The trusted job first imports artifacts from its fixed upstream job using Jenkins' authenticated API.
# The build number is the only caller input; arbitrary repository/archive/service/command parameters are absent.
read -r release archive hash < <(python3 - "${artifact_directory}/release.json" <<'METADATA'
import json,re,sys
with open(sys.argv[1],encoding='utf-8') as stream: item=json.load(stream)
release=item['release'];archive=item['archive'];digest=item['sha256']
assert re.fullmatch(r'hr-development-[a-f0-9]{12}-[a-f0-9]{12}',release)
assert archive==release+'.tar.gz'
assert re.fullmatch(r'[a-f0-9]{64}',digest)
print(release,archive,digest)
METADATA
)
archive_path="${artifact_directory}/${archive}"
python3 /usr/local/lib/fanasa-organization-hr/verify_bundle.py \
  --archive "$archive_path" --receipt "${artifact_directory}/release.json" \
  --expected-release "$release" --expected-hash "$hash"
# Helper is root-owned and permits only this service pair/environment, state-preserving atomic activation.
sudo -n /usr/local/sbin/fanasa-organization-hr-deploy development "$release" "$archive_path" "$hash"
