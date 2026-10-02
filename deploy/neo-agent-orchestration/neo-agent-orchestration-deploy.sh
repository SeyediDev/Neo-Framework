#!/usr/bin/env bash
set -Eeuo pipefail

environment="${1:-}"
release_id="${2:-}"
archive="${3:-}"
case "$environment" in
  development|production) ;;
  *) echo "environment must be development or production" >&2; exit 2 ;;
esac
if [[ ! "$release_id" =~ ^[0-9a-f]{40}$ ]]; then
  echo "release id must be a full commit SHA" >&2; exit 2
fi
if [[ ! -f "$archive" ]]; then echo "bundle archive is missing" >&2; exit 2; fi

base="/opt/neo-agent-orchestration/$environment"
release="$base/releases/$release_id"
tmp="$base/releases/.${release_id}.tmp.$$"
mkdir -p "$base/releases"
rm -rf -- "$tmp"
mkdir "$tmp"
trap 'rm -rf -- "$tmp"' EXIT
unzip -q -o -- "$archive" -d "$tmp"
test -s "$tmp/MANIFEST.json"
grep -q '"containsCredentials": false' "$tmp/MANIFEST.json"
test -f "$tmp/api/Neo.AgentOrchestration.Api.dll"
test -f "$tmp/web/Neo.AgentOrchestration.Web.dll"
test -f "$tmp/worker/Neo.AgentOrchestration.Worker.dll"
mv -- "$tmp" "$release"
trap - EXIT
chown -R neo-orch:neo-orch "$release"
ln -sfn "$release" "$base/current"
rm -f -- "$archive"

systemctl daemon-reload
for unit in api web worker; do
  systemctl restart "neo-agent-orchestration-$unit@$environment.service"
done
for unit in api web worker; do
  systemctl is-active --quiet "neo-agent-orchestration-$unit@$environment.service" || {
    journalctl -u "neo-agent-orchestration-$unit@$environment.service" -n 80 --no-pager >&2 || true
    exit 1
  }
done
echo "Activated $environment release $release_id"
