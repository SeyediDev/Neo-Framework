#!/usr/bin/env bash
set -Eeuo pipefail
if [[ "${EUID}" -ne 0 ]]; then echo "run as root" >&2; exit 2; fi
command -v dotnet >/dev/null || { echo "Install the .NET 10 runtime before enabling services." >&2; exit 2; }
command -v unzip >/dev/null || { echo "Install unzip before running host setup." >&2; exit 2; }
install -d -m 0750 /opt/neo-agent-orchestration/development/releases /opt/neo-agent-orchestration/production/releases
install -d -m 0750 /etc/neo-agent-orchestration
install -d -m 0750 /etc/sudoers.d
getent group neo-orch >/dev/null || groupadd --system neo-orch
id neo-orch >/dev/null 2>&1 || useradd --system --gid neo-orch --home-dir /nonexistent --shell /usr/sbin/nologin neo-orch
getent group neo-deploy >/dev/null || groupadd --system neo-deploy
id neo-deploy >/dev/null 2>&1 || useradd --system --gid neo-deploy --groups neo-orch --home-dir /nonexistent --shell /usr/sbin/nologin neo-deploy
chown -R neo-orch:neo-orch /opt/neo-agent-orchestration
install -m 0755 neo-agent-orchestration-deploy.sh /usr/local/sbin/neo-agent-orchestration-deploy
install -m 0755 neo-deploy-shell /usr/local/sbin/neo-deploy-shell
usermod -d /home/neo-deploy -s /usr/local/sbin/neo-deploy-shell neo-deploy
install -d -m 0700 -o neo-deploy -g neo-deploy /home/neo-deploy/.ssh
install -m 0644 neo-agent-orchestration-api@.service /etc/systemd/system/fanasa-agentic-api@.service
install -m 0644 neo-agent-orchestration-web@.service /etc/systemd/system/fanasa-agentic-web@.service
install -m 0644 neo-agent-orchestration-worker@.service /etc/systemd/system/fanasa-agentic-worker@.service
cat >/etc/sudoers.d/neo-agent-orchestration <<'EOF'
neo-deploy ALL=(root) NOPASSWD: /usr/local/sbin/neo-agent-orchestration-deploy
EOF
chmod 0440 /etc/sudoers.d/neo-agent-orchestration
systemctl daemon-reload
echo "Host scaffold installed. Configure /etc/neo-agent-orchestration/*.env before starting services."
