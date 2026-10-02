#!/usr/bin/env bash
set -Eeuo pipefail
environment="${1:-}"; release_id="${2:-}"; archive="${3:-}"
case "$environment" in development|production) ;; *) echo 'invalid environment' >&2; exit 2 ;; esac
[[ "$release_id" =~ ^[0-9a-f]{40}$ ]] || { echo 'release id must be a full SHA' >&2; exit 2; }
test -f "$archive"
base="/opt/fanasa-unified-portal/$environment"; release="$base/releases/$release_id"; tmp="$base/releases/.${release_id}.tmp.$$"
mkdir -p "$base/releases"; rm -rf -- "$tmp"; mkdir "$tmp"; trap 'rm -rf -- "$tmp"' EXIT
unzip -q -o -- "$archive" -d "$tmp"
test -s "$tmp/MANIFEST.json"; grep -q '"containsCredentials": false' "$tmp/MANIFEST.json"; test -f "$tmp/web/Fanasa.UnifiedPortal.Web.dll"
mv -- "$tmp" "$release"; trap - EXIT; chown -R fanasa-portal:fanasa-portal "$release"; ln -sfn "$release" "$base/current"; rm -f -- "$archive"
systemctl daemon-reload; systemctl restart "fanasa-unified-portal@$environment.service"
systemctl is-active --quiet "fanasa-unified-portal@$environment.service" || { journalctl -u "fanasa-unified-portal@$environment.service" -n 80 --no-pager >&2; exit 1; }
echo "Activated Fanasa Unified Portal $environment $release_id"
