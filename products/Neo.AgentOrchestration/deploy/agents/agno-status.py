"""Read-only operator observation. No model runs, task writes or dispatch."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import urllib.error
import urllib.request


EXPECTED_IMAGE = 'sha256:2e21f15bb986ba433b93e6b39a6be031f9b085f899f780a11d6af3b152f588cd'
EXPECTED_VERSION = '3.1.3'
BASE = 'http://127.0.0.1:17840'
TOKEN = Path('/opt/fanasa-agno/secrets/operator.token')
OUTPUT = Path('/opt/fanasa-agent-runtime/agno-status.json')


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def request(path, token=None):
    headers = {'Authorization': 'Bearer ' + token} if token else {}
    req = urllib.request.Request(BASE + path, headers=headers)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with opener.open(req, timeout=4) as response:
        body = response.read(65537)
        if response.status != 200 or len(body) > 65536:
            raise ValueError('invalid-health-response')
        return json.loads(body)


def command(args):
    result = subprocess.run(args, capture_output=True, timeout=8, check=True, text=True)
    if len(result.stdout) > 65536:
        raise ValueError('invalid-installation-response')
    return result.stdout


def installed():
    item = json.loads(command(['docker', 'inspect', 'fanasa-agno-api-1']))[0]
    if item.get('Image') != EXPECTED_IMAGE or not item.get('State', {}).get('Running'):
        return False
    version = command(['docker', 'exec', 'fanasa-agno-api-1', 'python', '-c',
                       'from importlib.metadata import version; print(version("agno"))']).strip()
    return version == EXPECTED_VERSION


def read_token():
    for parent in [TOKEN, *TOKEN.parents]:
        if parent.is_symlink():
            raise ValueError('invalid-private-token')
    info = TOKEN.stat()
    if info.st_size > 4096 or info.st_uid != 0 or info.st_mode & 0o077:
        raise ValueError('invalid-private-token')
    token = TOKEN.read_text().strip()
    if not token or any(c.isspace() for c in token):
        raise ValueError('invalid-private-token')
    return token


def healthy():
    # Authenticated reads plus anonymous rejection; never POST /runs or list sessions.
    try:
        request('/agents')
        return False
    except urllib.error.HTTPError as error:
        if error.code != 401:
            return False
    token = read_token()
    agents = request('/agents', token)
    workflows = request('/workflows', token)
    return (isinstance(agents, list) and isinstance(workflows, list)
            and {x.get('id') for x in agents if isinstance(x, dict)} == {'task-planner', 'quality-reviewer'}
            and {x.get('id') for x in workflows if isinstance(x, dict)} == {'task-preparation'})


def collect():
    present, transport = False, False
    try:
        present = installed()
        if present:
            transport = healthy()
    except (OSError, ValueError, TypeError, IndexError, KeyError, subprocess.SubprocessError):
        # Never put raw subprocess/HTTP errors, response text or credentials in report.
        transport = False
    return {'schema': 'fanasa-agno-advisory/v1',
            'observedAtUtc': datetime.now(timezone.utc).isoformat(), 'mode': 'advisory-only',
            'version': EXPECTED_VERSION if present else None, 'installed': present,
            'transportHealthy': transport,
            'reason': 'advisory-healthy' if transport else 'advisory-unreachable' if present else 'not-installed'}


def write_report(report, path=OUTPUT):
    path = Path(path)
    if not path.is_absolute():
        raise ValueError('invalid-report-path')
    for component in [path, *path.parents]:
        if component.is_symlink():
            raise ValueError('linked-report-path')
    if not path.parent.is_dir():
        raise ValueError('missing-report-directory')
    if path.exists() and not stat.S_ISREG(path.stat().st_mode):
        raise ValueError('invalid-existing-report')
    data = json.dumps(report, ensure_ascii=False).encode('utf-8')
    if len(data) > 8192:
        raise ValueError('invalid-report-size')
    fd, temporary = tempfile.mkstemp(prefix='.agno-status-', dir=path.parent)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, 'wb') as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
        temporary = None
    finally:
        if temporary is not None:
            os.unlink(temporary)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Read-only observation without writing report.')
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error('Operator identity is required; API/Web must not invoke this collector.')
    report = collect()
    if not args.check:
        write_report(report)
    print(json.dumps(report))


if __name__ == '__main__':
    main()
