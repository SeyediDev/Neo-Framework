#!/bin/sh
set -eu
package=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository=${1:-"$package/../../.."}
repository=$(CDPATH= cd -- "$repository" && pwd)
project="$package/tests/Fanasa.HrReadHost.Tests.csproj"
build() {
  operation=$1
  shift
  dotnet "$operation" "$project" "$@" --disable-build-servers \
    -p:EnableHr3Host=true -p:UseSharedCompilation=false \
    "-p:Hr3RepositoryRoot=$repository/" \
    "-p:Hr3ProfileRoot=$repository/deploy/fanasa-organization-hr/hr-read-profile-v3" \
    "-p:Hr3PdpRoot=$repository/deploy/fanasa-organization-hr/hr-read-pdp-v3" \
    "-p:CustomAfterMicrosoftCommonTargets=$package/integration/Hr3Host.targets"
}
build restore
build build --no-restore -v:minimal
dotnet "$package/tests/bin/Debug/net10.0/Fanasa.HrReadHost.Tests.dll"
