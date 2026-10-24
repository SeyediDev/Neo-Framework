"""One-way private work-board mirror. Stdlib only; never writes to the source API."""
import argparse
import hashlib
import hmac
import ipaddress
import json
import os
from pathlib import Path
import re
import sqlite3
import ssl
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

STATES = {
    'Backlog': ('برنامه‌ریزی', '#64748b'), 'Ready': ('آماده', '#0ea5e9'),
    'InProgress': ('در حال انجام', '#7c3aed'), 'Blocked': ('مسدود', '#ef4444'),
    'Review': ('بازبینی', '#f59e0b'), 'Done': ('تکمیل‌شده', '#10b981'),
    'Cancelled': ('لغوشده', '#94a3b8'),
}
LABEL_PREFIX = 'فن آسا / '
BEGIN = '<!-- FANASA-WORK-MIRROR-BEGIN -->'
END = '<!-- FANASA-WORK-MIRROR-END -->'
MAX_BODY = 4_000_000

class MirrorError(Exception):
    pass

class HttpFailure(MirrorError):
    def __init__(self, status):
        self.status = status
        super().__init__('HTTP ' + str(status))

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None

class Http:
    def __init__(self, base, headers=None, ca=None):
        self.base = base.rstrip('/')
        self.headers = headers or {}
        self.opener = urllib.request.build_opener(
            NoRedirect(), urllib.request.HTTPSHandler(context=ssl.create_default_context(cafile=ca)))

    def call(self, method, path, body=None, form=False):
        if not path.startswith('/') or path.startswith('//') or '#' in path:
            raise MirrorError('Invalid relative API path')
        data = None if body is None else (urllib.parse.urlencode(body).encode() if form else json.dumps(body).encode())
        headers = dict(self.headers, **{'Content-Type': 'application/x-www-form-urlencoded' if form else 'application/json'})
        try:
            with self.opener.open(urllib.request.Request(self.base + path, data, headers, method=method), timeout=25) as response:
                raw = response.read(MAX_BODY + 1)
                if len(raw) > MAX_BODY:
                    raise MirrorError('Response exceeded size limit')
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as e:
            raise HttpFailure(e.code) from None
        except (urllib.error.URLError, TimeoutError, json.JSONDecodeError):
            raise MirrorError('Upstream unavailable or invalid response; reconcile uncertain writes') from None

def validate_url(value, allow_loopback=False):
    u = urllib.parse.urlsplit(value)
    if u.username or u.password or u.query or u.fragment or not u.hostname:
        raise MirrorError('Invalid connection URL')
    if u.scheme == 'https':
        return value.rstrip('/')
    if allow_loopback and u.scheme == 'http':
        try:
            if ipaddress.ip_address(u.hostname).is_loopback:
                return value.rstrip('/')
        except ValueError:
            pass
    raise MirrorError('HTTPS required except source API on numeric loopback')

def safe_title(value):
    # Free-form descriptions, chat text and logs never enter this function.
    if not isinstance(value, str):
        raise MirrorError('Invalid task title')
    value = re.sub(r'[\x00-\x1f\x7f\u202a-\u202e\u2066-\u2069]', ' ', value)
    value = re.sub(r'(?i)\b(?:password|secret|api[_ -]?key|bearer|token)\s*[:=]\s*\S+', '[اطلاعات محرمانه]', value)
    value = re.sub(r'\bsk-[A-Za-z0-9_-]{8,}', '[اطلاعات محرمانه]', value)
    value = re.sub(r'[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '[ایمیل]', value)
    value = re.sub(r'https?://\S+', '[پیوند]', value)
    value = re.sub(r'[A-Za-z]:[\\/][^\s]+', '[مسیر محلی]', value)
    value = value.translate(str.maketrans({'<': '‹', '>': '›', '`': '', '[': '(', ']': ')'}))
    return ' '.join(value.split())[:190] or 'کار بدون عنوان'

def project_item(raw, project_id):
    # Strict publication allowlist: never persist or send Description/AcceptanceCriteria/owner/chat/logs.
    if raw.get('projectId') != project_id or raw.get('status') not in STATES:
        raise MirrorError('Foreign project or unsupported status')
    item_id = str(uuid.UUID(raw['id']))
    key = raw.get('key', '')
    if not re.fullmatch(r'[A-Za-z0-9_.-]{1,80}', key):
        raise MirrorError('Invalid work key')
    priority = raw.get('priority')
    if priority not in ('Low', 'Normal', 'High', 'Critical'):
        raise MirrorError('Unsupported priority')
    result = {'id': item_id, 'key': key, 'title': safe_title(raw['title']), 'status': raw['status'],
              'priority': priority, 'type': raw.get('type', 'Task'), 'archived': raw.get('isArchived') is True}
    for name in ('elapsedSeconds', 'estimatedSeconds', 'estimatedTokens'):
        n = raw.get(name)
        if n is not None and (type(n) is not int or n < 0):
            raise MirrorError('Invalid usage or estimate')
        result[name] = n
    meter = raw.get('tokenMeter') or {}
    for name in ('totalTokens', 'knownTotalTokens'):
        n = meter.get(name)
        if n is not None and (type(n) is not int or n < 0):
            raise MirrorError('Invalid token meter')
        result[name] = n
    result['parentId'] = str(uuid.UUID(raw['parentWorkItemId'])) if raw.get('parentWorkItemId') else None
    return result

def marker(binding, item_id, key):
    scope = '/'.join([binding['organization_id'], binding['workspace_id'], binding['project_id'], item_id])
    sig = hmac.new(key, scope.encode(), hashlib.sha256).hexdigest()
    return '<!-- fanasa-work-mirror:v1:' + scope + ':' + sig + ' -->'

def payload(item, binding, key, parent=None):
    status = item['status']
    mark = marker(binding, item['id'], key)
    url = binding['web_url'] + '/work/' + binding['organization_id'] + '/' + binding['workspace_id'] + '/items/' + item['id']
    lines = [BEGIN, mark, '**' + STATES[status][0] + '** · ' + item['priority'], '',
             '[مشاهده و مدیریت کار](' + url + ')', '',
             'این کارت نمایشی است؛ تغییر وضعیت و اجرای ایجنت در مدیریت کار انجام می‌شود.']
    if parent:
        lines.extend(['', 'کار مادر: [' + parent['key'] + '](' + parent['url'] + ')'])
    lines.extend(['', '<details><summary>برآورد و مصرف</summary>', '',
                  '| شاخص | مقدار |', '| --- | --- |'])
    def duration(n):
        return 'نامشخص' if n is None else str(round(n / 60, 1)) + ' دقیقه'
    for caption, value in [('برآورد زمان', duration(item['estimatedSeconds'])),
                           ('زمان ثبت‌شده', duration(item['elapsedSeconds'])),
                           ('برآورد توکن', str(item['estimatedTokens']) if item['estimatedTokens'] is not None else 'نامشخص'),
                           ('مصرف کامل گزارش‌شدهٔ توکن', str(item['totalTokens']) if item['totalTokens'] is not None else 'نامشخص'),
                           ('زیرجمع توکن‌های معلوم', str(item['knownTotalTokens']) if item['knownTotalTokens'] is not None else 'نامشخص')]:
        lines.append('| ' + caption + ' | ' + value + ' |')
    lines.extend(['', 'مصرف بودجه، درصد پیشرفت نیست. اطلاعات خصوصی و متن چت به این برد منتقل نمی‌شود.',
                  '', '</details>', END])
    labels = [LABEL_PREFIX + STATES[status][0]]
    if item['archived']:
        labels.append(LABEL_PREFIX + 'بایگانی‌شده')
    return {'title': '[' + item['key'] + '] ' + item['title'], 'description': '\n'.join(lines),
            'labels': labels, 'closed': item['archived'] or status in ('Done', 'Cancelled')}

def replace_block(old, new):
    if old.count(BEGIN) != 1 or old.count(END) != 1 or old.index(END) < old.index(BEGIN):
        raise MirrorError('Managed description block changed; reconcile manually')
    return old[:old.index(BEGIN)] + new + old[old.index(END) + len(END):]

class Store:
    def __init__(self, path, binding):
        self.db = sqlite3.connect(path)
        self.db.execute('CREATE TABLE IF NOT EXISTS binding (id INTEGER PRIMARY KEY CHECK(id=1), value TEXT NOT NULL)')
        self.db.execute('CREATE TABLE IF NOT EXISTS mapping (source_id TEXT PRIMARY KEY, iid INTEGER, pending INTEGER NOT NULL DEFAULT 0)')
        encoded = json.dumps(binding, sort_keys=True)
        row = self.db.execute('SELECT value FROM binding WHERE id=1').fetchone()
        if row and row[0] != encoded:
            self.db.close()
            raise MirrorError('State belongs to a different connection')
        self.db.execute('INSERT OR IGNORE INTO binding VALUES (1,?)', (encoded,))
        self.db.commit()

    def get(self, source_id):
        row = self.db.execute('SELECT iid,pending FROM mapping WHERE source_id=?', (source_id,)).fetchone()
        return row or (None, 0)

    def pending(self, source_id):
        self.db.execute('INSERT INTO mapping(source_id,pending) VALUES(?,1) ON CONFLICT(source_id) DO UPDATE SET pending=1', (source_id,))
        self.db.commit()

    def save(self, source_id, iid):
        self.db.execute('INSERT INTO mapping VALUES(?,?,0) ON CONFLICT(source_id) DO UPDATE SET iid=excluded.iid,pending=0', (source_id, iid))
        self.db.commit()

    def reject(self, source_id):
        self.db.execute('DELETE FROM mapping WHERE source_id=? AND iid IS NULL', (source_id,))
        self.db.commit()

def read_source(source, binding):
    catalog = source.call('GET', '/catalog')
    projects = [p for p in catalog['projects'] if p['id'] == binding['project_id'] and p['key'] == binding['project_key'] and p['isEnabled']]
    if len(projects) != 1:
        raise MirrorError('Source project disabled, absent or rebound')
    items, seen, expected = [], set(), None
    for page in range(50):
        response = source.call('GET', '/items?' + urllib.parse.urlencode({'projectId': binding['project_id'], 'includeArchived': 'true', 'skip': page * 200, 'take': 200}))
        if expected is None:
            expected = response['total']
        if type(expected) is not int or expected < 0 or expected > 10000 or response['total'] != expected:
            raise MirrorError('Source changed during pagination; retry next cycle')
        for raw in response['items']:
            item = project_item(raw, binding['project_id'])
            if item['id'] in seen:
                raise MirrorError('Duplicate source ID; no publication')
            seen.add(item['id'])
            items.append(item)
        if len(items) == expected:
            return items
        if not response['items'] or len(items) > expected:
            break
    raise MirrorError('Incomplete source snapshot; no publication')

def remote_issues(target, project_id):
    result = []
    for page in range(1, 101):
        batch = target.call('GET', f'/projects/{project_id}/issues?state=all&per_page=100&page={page}')
        if not isinstance(batch, list):
            raise MirrorError('Invalid destination page')
        result.extend(batch)
        if len(batch) < 100:
            return result
    raise MirrorError('Destination exceeds configured bound')

def sync(items, remote, target, binding, key, store, bot_id, max_changes=100):
    prefix = f'/projects/{binding["gitlab_project_id"]}/issues'
    changed = created = recovered = skipped = 0
    indexed = {r['iid']: r for r in remote}
    for item in items:
        iid, pending = store.get(item['id'])
        mark = marker(binding, item['id'], key)
        matches = [r for r in remote if mark in (r.get('description') or '')]
        if len(matches) > 1:
            raise MirrorError('Duplicate destination marker; manual reconciliation required')
        if matches:
            issue = matches[0]
            if issue['author']['id'] != bot_id or issue.get('project_id') != binding['gitlab_project_id']:
                raise MirrorError('Destination marker not owned by this connection')
            if iid is not None and iid != issue['iid']:
                raise MirrorError('Destination mapping changed')
            if iid is None or pending:
                store.save(item['id'], issue['iid'])
                recovered += 1
            iid = issue['iid']
        elif iid is not None or pending:
            raise MirrorError('Mapped or uncertain Issue missing; do not recreate blindly')
        else:
            if item['archived']:
                skipped += 1
                continue
            if changed >= max_changes:
                raise MirrorError('Mutation budget exceeded; continue next cycle')
            want = payload(item, binding, key)
            store.pending(item['id'])
            try:
                issue = target.call('POST', prefix, {'title': want['title'], 'description': want['description'],
                    'labels': ','.join(want['labels']), 'confidential': True})
            except HttpFailure as e:
                if e.status in (400, 401, 403, 404, 422, 429):
                    store.reject(item['id'])
                raise
            if issue.get('author', {}).get('id') != bot_id or issue.get('project_id') != binding['gitlab_project_id']:
                raise MirrorError('Unexpected create receipt; reconcile pending Issue')
            iid = issue['iid']
            store.save(item['id'], iid)
            remote.append(issue)
            indexed[iid] = issue
            changed += 1
            created += 1
        parent = None
        if item['parentId']:
            parent_item = next((i for i in items if i['id'] == item['parentId']), None)
            parent_iid, _ = store.get(item['parentId'])
            if parent_item and parent_iid:
                parent = {'key': parent_item['key'], 'url': binding['gitlab_url'] + '/' + binding['gitlab_project_path'] + '/-/issues/' + str(parent_iid)}
        want = payload(item, binding, key, parent)
        description = replace_block(issue.get('description') or '', want['description'])
        labels = set(issue.get('labels', []))
        owned_labels = {LABEL_PREFIX + s[0] for s in STATES.values()} | {LABEL_PREFIX + 'بایگانی‌شده'}
        closed = issue['state'] == 'closed'
        if (issue['title'] == want['title'] and description == issue.get('description')
                and labels & owned_labels == set(want['labels']) and closed == want['closed'] and issue.get('confidential') is True):
            continue
        if changed >= max_changes:
            raise MirrorError('Mutation budget exceeded; continue next cycle')
        body = {'title': want['title'], 'description': description, 'confidential': True,
                'add_labels': ','.join(set(want['labels']) - labels),
                'remove_labels': ','.join((labels & owned_labels) - set(want['labels']))}
        if closed != want['closed']:
            body['state_event'] = 'close' if want['closed'] else 'reopen'
        updated = target.call('PUT', prefix + '/' + str(iid), body)
        indexed[iid] = updated
        remote[remote.index(issue)] = updated
        changed += 1
    return {'sourceItems': len(items), 'created': created, 'changed': changed, 'recoveredMappings': recovered, 'unpublishedArchived': skipped}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True)
    parser.add_argument('--preview', action='store_true')
    args = parser.parse_args()
    config = json.loads(Path(args.config).read_text())
    binding = config['binding']
    for name in ('organization_id', 'workspace_id', 'project_id'):
        binding[name] = str(uuid.UUID(binding[name]))
    binding['gitlab_url'] = validate_url(binding['gitlab_url'])
    binding['web_url'] = validate_url(binding['web_url'])
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', binding['gitlab_project_path']):
        raise MirrorError('Invalid destination project path')
    ca = config['ca_file']
    credentials = Path(os.environ.get('CREDENTIALS_DIRECTORY', config.get('credentials_directory', '')))
    src = json.loads((credentials / 'source').read_text())
    dst = json.loads((credentials / 'gitlab').read_text())
    key = (credentials / 'mapping-key').read_bytes()
    if len(key) < 32:
        raise MirrorError('Mapping signing key too short')
    issuer = Http(validate_url(config['authority']), ca=ca)
    tok = issuer.call('POST', '/protocol/openid-connect/token', dict(src, grant_type='client_credentials'), form=True)['access_token']
    source_base = validate_url(config['source_url'], allow_loopback=True)
    expected_path = '/api/orchestration/v1/organizations/' + binding['organization_id'] + '/workspaces/' + binding['workspace_id']
    if urllib.parse.urlsplit(source_base).path != expected_path:
        raise MirrorError('Source scope URL differs from binding')
    source = Http(source_base, {'Authorization': 'Bearer ' + tok}, ca=ca)
    target = Http(binding['gitlab_url'] + '/api/v4', {'PRIVATE-TOKEN': dst['token']}, ca=ca)
    project = target.call('GET', '/projects/' + str(binding['gitlab_project_id']))
    if project['path_with_namespace'] != binding['gitlab_project_path'] or project['visibility'] != 'private' or project.get('archived'):
        raise MirrorError('Destination must be the expected active private project')
    viewer = target.call('GET', '/user')
    if viewer['id'] != config['bot_user_id']:
        raise MirrorError('Destination identity differs from configured bot')
    items = read_source(source, binding)
    if args.preview:
        print(json.dumps({'preview': True, 'sourceItems': len(items), 'nonArchived': sum(not i['archived'] for i in items)}, ensure_ascii=False))
        return
    state = Path(config['state_directory'])
    state.mkdir(exist_ok=True, parents=True, mode=0o700)
    os.umask(0o077)
    # systemd is single-instance; flock also fences direct manual invocations.
    import fcntl
    with (state / 'sync.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise MirrorError('Another sync is active') from None
        store = Store(state / 'mapping.sqlite3', binding)
        result = sync(items, remote_issues(target, binding['gitlab_project_id']), target, binding, key, store, viewer['id'])
        result.update({'observedAt': int(time.time()), 'boardUrl': binding['board_url']})
        pending = state / 'last-result.tmp'
        pending.write_text(json.dumps(result, ensure_ascii=False))
        os.replace(pending, state / 'last-result.json')
        print(json.dumps(result, ensure_ascii=False))

if __name__ == '__main__':
    try:
        main()
    except (MirrorError, OSError, ValueError, KeyError) as error:
        # Never log upstream response bodies, credentials or task content.
        print(json.dumps({'sync': 'failed', 'error': str(error) if isinstance(error, MirrorError) else type(error).__name__}))
        raise SystemExit(1)
