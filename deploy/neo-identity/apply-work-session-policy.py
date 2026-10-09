#!/usr/bin/env python3
"""Apply versioned Keycloak/Web timeouts; requires existing kcadm authentication.

No passwords or tokens are read, changed or exported by this command.
Restart only the Web service after deploying a version with configurable lifetime.
"""
import argparse
import datetime
import json
import pathlib
import shutil
import subprocess


def run(*args):
    return subprocess.check_output(args, text=True)


def kc(*args):
    return run("docker", "exec", "neo-identity-keycloak-1", "/opt/keycloak/bin/kcadm.sh", *args)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--include-realm", action="store_true",
                        help="Change shared SSO timeouts too; requires explicit authorization for all realm clients")
    args = parser.parse_args()
    policy = json.loads(pathlib.Path(__file__).with_name("work-session-policy.json").read_text())
    realm_name = policy["realm"]
    realm_path = "realms/" + realm_name
    realm = json.loads(kc("get", realm_path))
    clients = json.loads(kc("get", "clients", "-r", realm_name, "-q", "clientId=" + policy["clientId"]))
    if len(clients) != 1:
        raise RuntimeError("Expected one Work Management client")
    client = clients[0]
    attributes = dict(client.get("attributes", {}))
    idle = policy["ssoSessionIdleTimeout"]
    maximum = policy["ssoSessionMaxLifespan"]
    token = policy["accessTokenLifespan"]
    minutes = policy["webSessionLifetimeMinutes"]
    if not (300 <= token <= idle <= maximum <= 86400 and 15 <= minutes <= 1440 and minutes * 60 <= maximum):
        raise RuntimeError("Invalid bounded session policy")
    effective_idle = idle if args.include_realm else min(idle, realm["ssoSessionIdleTimeout"])
    effective_max = maximum if args.include_realm else min(maximum, realm["ssoSessionMaxLifespan"])
    attributes.update({"access.token.lifespan": str(token), "client.session.idle.timeout": str(effective_idle),
                       "client.session.max.lifespan": str(effective_max)})
    env = pathlib.Path("/etc/neo-agent-orchestration.env")
    lines = env.read_text().splitlines()
    setting = "WebAuthentication__SessionLifetimeMinutes="
    matches = [i for i, line in enumerate(lines) if line.startswith(setting)]
    if len(matches) > 1:
        raise RuntimeError("Duplicate Web session settings")
    if matches:
        lines[matches[0]] = setting + str(minutes)
    else:
        lines.append(setting + str(minutes))
    print(json.dumps({"policy": policy, "realmChange": args.include_realm,
                      "effectiveIdle": effective_idle, "effectiveMax": effective_max,
                      "mode": "apply" if args.apply else "preview"}))
    if not args.apply:
        return
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    backup = pathlib.Path("/opt/neo-identity/backups/work-session-" + stamp)
    backup.mkdir(mode=0o700, parents=True)
    old_realm = {k: realm[k] for k in ("ssoSessionIdleTimeout", "ssoSessionMaxLifespan")}
    (backup / "realm-timeouts.json").write_text(json.dumps(old_realm))
    (backup / "client-attributes.json").write_text(json.dumps(client.get("attributes", {})))
    shutil.copy2(env, backup / "web.env")
    try:
        if args.include_realm:
            kc("update", realm_path, "-s", "ssoSessionIdleTimeout=" + str(idle),
               "-s", "ssoSessionMaxLifespan=" + str(maximum))
        kc("update", "clients/" + client["id"], "-r", realm_name, "-s", "attributes=" + json.dumps(attributes))
        env.write_text("\n".join(lines) + "\n")
        saved = json.loads(kc("get", realm_path))
        expected_realm = {"ssoSessionIdleTimeout": idle, "ssoSessionMaxLifespan": maximum} if args.include_realm else old_realm
        assert all(saved[k] == v for k, v in expected_realm.items())
        saved_client = json.loads(kc("get", "clients/" + client["id"], "-r", realm_name))
        assert all(saved_client["attributes"].get(k) == v for k, v in attributes.items())
    except Exception:
        if args.include_realm:
            kc("update", realm_path, *[x for k, v in old_realm.items() for x in ("-s", k + "=" + str(v))])
        kc("update", "clients/" + client["id"], "-r", realm_name, "-s", "attributes=" + json.dumps(client.get("attributes", {})))
        shutil.copy2(backup / "web.env", env)
        raise
    print(json.dumps({"status": "verified", "backup": str(backup), "webRestartRequired": True}))


if __name__ == "__main__":
    main()
