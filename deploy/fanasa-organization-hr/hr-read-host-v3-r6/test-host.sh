#!/bin/sh
set -eu
package=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository=${1:-"$package"}
if [ "$#" -eq 0 ]; then
  while [ ! -f "$repository/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Fanasa.AccessManagement.Web.csproj" ]; do
    [ "$repository" != / ] || { echo 'Pass the actual host repository as argument 1.' >&2; exit 1; }
    repository=$(dirname -- "$repository")
  done
fi
repository=$(CDPATH= cd -- "$repository" && pwd)
artifact_base="$repository/.build/hr3-host-r6-artifacts"
if [ "$#" -gt 2 ]; then
  echo 'Usage: test-host.sh [repository [fresh-artifacts-path]]' >&2
  exit 1
fi
if [ "$#" -eq 2 ]; then
  case "$2" in
    /*) artifacts=$(realpath -m -- "$2") ;;
    *) artifacts=$(realpath -m -- "$repository/$2") ;;
  esac
  case "$artifacts" in
    "$artifact_base"/*) ;;
    *) echo 'Artifacts path must be beneath repository/.build/hr3-host-r6-artifacts.' >&2; exit 1 ;;
  esac
  [ ! -e "$artifacts" ] && [ ! -L "$artifacts" ] || { echo 'Artifacts path already exists; stale artifacts are not accepted.' >&2; exit 1; }
  mkdir -p -- "$artifact_base"
  mkdir -- "$artifacts"
else
  mkdir -p -- "$artifact_base"
  artifacts=$(mktemp -d "$artifact_base/run-XXXXXXXX")
fi
printf 'HR3_ARTIFACTS_PATH=%s\n' "$artifacts"
DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1
export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER
project="$package/tests/Fanasa.HrReadHost.Tests.csproj"
build() {
  operation=$1
  shift
  dotnet "$operation" "$project" "$@" --disable-build-servers --artifacts-path "$artifacts" \
    -p:EnableHr3Host=true -p:UseSharedCompilation=false -p:UseRazorBuildServer=false \
    -p:BuildInParallel=false -p:RestoreDisableParallel=true -m:1 -nodeReuse:false \
    "-p:Hr3RepositoryRoot=$repository/" \
    "-p:Hr3ProfileRoot=$repository/deploy/fanasa-organization-hr/hr-read-profile-v3" \
    "-p:Hr3PdpRoot=$repository/deploy/fanasa-organization-hr/hr-read-pdp-v3" \
    "-p:CustomAfterMicrosoftCommonTargets=$package/integration/Hr3Host.targets"
}
build restore --disable-parallel
build build --no-restore -v:minimal
bin_root="$artifacts/bin/Fanasa.HrReadHost.Tests"
set -- "$bin_root"/*/Fanasa.HrReadHost.Tests.dll
if [ "$#" -ne 1 ] || [ ! -f "$1" ]; then
  echo "Expected exactly one fresh HR3 test DLL under $bin_root/<pivot>." >&2
  exit 1
fi
printf 'HR3_TEST_DLL=%s\n' "$1"
dotnet "$1"
