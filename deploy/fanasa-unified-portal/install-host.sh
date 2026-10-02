#!/usr/bin/env bash
set -Eeuo pipefail
install -d -m 0755 /opt/fanasa-unified-portal/development/releases /opt/fanasa-unified-portal/production/releases
install -d -m 0750 /etc/fanasa-unified-portal
if ! id fanasa-portal >/dev/null 2>&1; then useradd --system --home /opt/fanasa-unified-portal --shell /usr/sbin/nologin fanasa-portal; fi
chown -R fanasa-portal:fanasa-portal /opt/fanasa-unified-portal
install -m 0755 fanasa-unified-portal-deploy.sh /usr/local/sbin/fanasa-unified-portal-deploy
install -m 0644 fanasa-unified-portal@.service /etc/systemd/system/fanasa-unified-portal@.service
systemctl daemon-reload
echo 'Create /etc/fanasa-unified-portal/development.env and production.env with mode 0600 before enabling a release.'
