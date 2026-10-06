#!/usr/bin/env python3
"""Add the Platform Control Center host without removing legacy SSO callbacks.

Run on the VPS after authenticating kcadm in the Keycloak container.
No credentials are accepted or written by this script.
"""
import argparse
import datetime
import json
import pathlib
import shutil
import subprocess

OLD = "access.fanasa.net.local"
NEW = "platform.fanasa.net.local"
CONTAINER = "neo-identity-keycloak-1"


def run(*args):
    return subprocess.check_output(args, text=True)


def kc(*args):
    return run("docker", "exec", CONTAINER, "/opt/keycloak/bin/kcadm.sh", *args)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    nginx = pathlib.Path("/opt/fanasa-gateway/nginx.conf")
    env = pathlib.Path("/etc/fanasa-access-management/development.env")
    source = nginx.read_text()
    marker = "  server { listen 443 ssl; server_name " + OLD + ";"
    start = source.index(marker)
    end = source.index("  server {", start + len(marker))
    legacy = source[start:end]
    assert "proxy_pass http://127.0.0.1:5191" in legacy
    updated = source
    if "server_name " + NEW + ";" not in source:
        updated = source[:end] + legacy.replace(OLD, NEW) + source[end:]
    env_source = env.read_text()
    lines = env_source.splitlines()
    for i, line in enumerate(lines):
        if line.startswith("AllowedHosts="):
            hosts = line.split("=", 1)[1].split(";")
            if NEW not in hosts:
                lines[i] += ";" + NEW
            break
    else:
        raise RuntimeError("AllowedHosts missing; refusing to invent policy")
    clients = json.loads(kc("get", "clients", "-r", "fanasa", "-q", "clientId=fanasa-access-management-web"))
    if len(clients) != 1:
        raise RuntimeError("Expected exactly one Access Management client")
    client = clients[0]
    redirects = list(client.get("redirectUris", []))
    callback = "https://" + NEW + "/signin-oidc"
    if callback not in redirects:
        redirects.append(callback)
    attrs = dict(client.get("attributes", {}))
    logout = "https://" + NEW + "/signout-callback-oidc"
    existing = attrs.get("post.logout.redirect.uris", "")
    if logout not in existing.split("##"):
        attrs["post.logout.redirect.uris"] = "##".join(filter(None, [existing, logout]))
    before = {k: client.get(k) for k in ("rootUrl", "baseUrl", "redirectUris", "attributes") if k in client}
    if not args.apply:
        print(json.dumps({"host": NEW, "callback": callback, "legacyPreserved": True, "mode": "preview"}))
        return
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    backup = pathlib.Path("/opt/fanasa-gateway/backups/platform-host-" + stamp)
    backup.mkdir(mode=0o700)
    shutil.copy2(nginx, backup / "nginx.conf")
    shutil.copy2(env, backup / "development.env")
    (backup / "client-routing.json").write_text(json.dumps(before))
    try:
        kc("update", "clients/" + client["id"], "-r", "fanasa",
           "-s", "redirectUris=" + json.dumps(redirects),
           "-s", "attributes=" + json.dumps(attrs),
           "-s", "rootUrl=https://" + NEW, "-s", "baseUrl=https://" + NEW + "/")
        saved = json.loads(kc("get", "clients/" + client["id"], "-r", "fanasa"))
        assert callback in saved["redirectUris"]
        assert logout in saved["attributes"]["post.logout.redirect.uris"].split("##")
        nginx.write_text(updated)
        env.write_text("\n".join(lines) + "\n")
        run("docker", "exec", "fanasa-gateway-gateway-1", "nginx", "-t")
        run("systemctl", "restart", "fanasa-access-management@development.service")
        run("docker", "exec", "fanasa-gateway-gateway-1", "nginx", "-s", "reload")
    except Exception:
        shutil.copy2(backup / "nginx.conf", nginx)
        shutil.copy2(backup / "development.env", env)
        settings = [part for key, value in before.items() for part in ("-s", key + "=" + json.dumps(value))]
        kc("update", "clients/" + client["id"], "-r", "fanasa", *settings)
        run("systemctl", "restart", "fanasa-access-management@development.service")
        run("docker", "exec", "fanasa-gateway-gateway-1", "nginx", "-s", "reload")
        raise
    print(json.dumps({"host": NEW, "backup": str(backup), "legacyPreserved": True, "mode": "applied"}))


if __name__ == "__main__":
    main()
