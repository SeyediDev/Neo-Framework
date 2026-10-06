#!/usr/bin/env python3
"""Enroll an explicitly authorized Keycloak platform admin in existing tenants.

Maintenance operation: validates the IdP account, preserves backups, writes
membership/grants and audit events. Contains no credentials. Does not reset
passwords or confer privileges in third-party applications.
"""
import argparse
import datetime
import json
import pathlib
import sqlite3
import subprocess
import uuid

p = argparse.ArgumentParser()
p.add_argument('--subject', required=True)
p.add_argument('--username', required=True)
p.add_argument('--state', default='/opt/fanasa-access-management/development/state')
a = p.parse_args()
uuid.UUID(a.subject)
def kc(resource):
    return json.loads(subprocess.check_output(['docker','exec','neo-identity-keycloak-1',
        '/opt/keycloak/bin/kcadm.sh','get',resource,'-r','fanasa'], text=True))
user = kc('users/' + a.subject)
assert user['username'] == a.username and user['enabled'], 'IdP identity mismatch'
assert any(r['name'] == 'platform-admin' for r in kc('users/' + a.subject + '/role-mappings').get('realmMappings', [])), 'Platform admin role required'
state = pathlib.Path(a.state)
fabric = state / 'fabric/fabric.db'
registry = state / 'platform-registry.db'
assert fabric.is_file() and registry.is_file(), 'Existing access databases required'
now = datetime.datetime.now(datetime.timezone.utc).isoformat()
backup = state / 'admin-enrollment-backups' / datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
backup.mkdir(parents=True, mode=0o700)
for dbpath in (fabric, registry):
    with sqlite3.connect(dbpath) as source, sqlite3.connect(backup / dbpath.name) as target:
        source.backup(target)
with sqlite3.connect(fabric) as c:
    c.execute('BEGIN IMMEDIATE')
    def records(kind):
        return [json.loads(r[0]) for r in c.execute('SELECT json FROM fabric_documents WHERE kind=?', (kind,))]
    def put(kind,key,value):
        c.execute('INSERT INTO fabric_documents(kind,key,json) VALUES(?,?,?) ON CONFLICT(kind,key) DO UPDATE SET json=excluded.json', (kind,key,json.dumps(value)))
    tenants = [t for t in records('tenant') if t['IsActive']]
    members = records('membership')
    for tenant in tenants:
        tid = tenant['Id']
        member = next((m for m in members if m['TenantId'] == tid and m['KeycloakSubject'] == a.subject), None)
        if member is not None and not member['IsActive']:
            raise RuntimeError('Previously offboarded membership requires explicit reactivation')
        if member is None:
            seats = sum(m['TenantId'] == tid and m['IsActive'] for m in members) + 1
            for subscription in records('subscription'):
                if subscription['TenantId'] == tid and subscription['Status'] == 'active' and seats > subscription['SeatLimit']:
                    raise RuntimeError('Tenant seat limit exceeded')
            member = dict(Id=str(uuid.uuid4()), TenantId=tid, KeycloakSubject=a.subject, DisplayName=a.username, IsActive=True, JoinedAt=now)
            put('membership',member['Id'],member)
        for permission in ['organization.read','organization.write','billing.read','billing.write','billing.meter','tenancy.read','tenancy.manage']:
            put('tenant-grant',f'{tid}:{a.subject}:{permission}',dict(TenantId=tid,Subject=a.subject,Permission=permission,ExpiresAt=None))
        c.execute('INSERT INTO fabric_outbox(key,kind,payload,recorded) VALUES(?,?,?,?)', ('admin-enrollment:'+str(uuid.uuid4()), 'PlatformAdminEnrolled', json.dumps(dict(TenantId=tid,Subject=a.subject,Actor='authorized-platform-maintenance',Reason='User requested platform administrator access')), now))
with sqlite3.connect(registry) as c:
    active = {t['Id'] for t in tenants}
    for (tid,) in c.execute('SELECT id FROM platform_tenants').fetchall():
        if tid not in active:
            continue
        for permission in ['developer.read','developer.launch','developer.manage','developer.staging.read','developer.staging.launch','developer.staging.manage','developer.production.read','developer.production.launch','developer.production.manage']:
            c.execute('INSERT INTO platform_grants(subject,tenant,permission,expires) VALUES(?,?,?,NULL) ON CONFLICT(subject,tenant,permission) DO UPDATE SET expires=NULL', (a.subject,tid,permission))
        c.execute('INSERT INTO platform_audit(actor,operation,data,recorded) VALUES(?,?,?,?)', ('authorized-platform-maintenance','admin.enroll',json.dumps(dict(subject=a.subject,tenant=tid)),now))
print(json.dumps(dict(subject=a.subject, username=a.username, tenants=len(tenants), status='enrolled')))
