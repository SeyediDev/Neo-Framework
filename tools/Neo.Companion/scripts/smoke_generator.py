"""Generate, compile and run a real CRUD resource; all data stays in a unique artifact directory."""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--cli-dll', required=True)
parser.add_argument('--configuration', default='Release', choices=['Debug', 'Release'])
parser.add_argument('--skip-framework-build', action='store_true', help='Use framework DLLs already built in the selected configuration.')
args = parser.parse_args()
companion = Path(__file__).resolve().parents[1]
repo = companion.parents[1]
artifacts = companion / 'artifacts'
artifacts.mkdir(exist_ok=True)
cli = Path(args.cli_dll).resolve()
creation = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0

def run(command, cwd=None, success=True):
    result = subprocess.run(command, cwd=cwd, text=True, encoding='utf-8', capture_output=True, timeout=900, creationflags=creation)
    if (result.returncode == 0) != success:
        raise AssertionError(result.stdout + result.stderr)
    return result.stdout

with tempfile.TemporaryDirectory(prefix='generated feature ', dir=artifacts) as temporary:
    root = Path(temporary).resolve()
    assert root.is_relative_to(artifacts.resolve())  # Scope recursive cleanup to this newly created artifact directory.
    output = root / 'Books App'
    command = ['dotnet', str(cli), 'new', 'feature', 'Book', '--namespace', 'Generated.Inventory', '--route', 'books', '--output', str(output), '--neo-root', str(repo)]
    run(command)
    before = {x.name: hashlib.sha256(x.read_bytes()).hexdigest() for x in output.iterdir()}
    run(command, success=False)
    assert before == {x.name: hashlib.sha256(x.read_bytes()).hexdigest() for x in output.iterdir()}
    invalid = command.copy()
    invalid[4] = '../escape'
    run(invalid, success=False)
    print('Generator input and no-overwrite checks passed.', flush=True)
    build = ['dotnet', 'build', str(output / 'Book.csproj'), '-c', args.configuration, '-m:1', '-nr:false', '-p:UseSharedCompilation=false']
    if args.skip_framework_build:
        build.append('-p:BuildProjectReferences=false')
    run(build)
    print('Generated project compiled.', flush=True)
    dll = output / 'bin' / args.configuration / 'net10.0' / 'Book.dll'
    report = run(['dotnet', str(dll), '--doctor'], cwd=output)
    assert json.loads(report.strip().splitlines()[-1])['Healthy'], report
    database = output / 'neo-crud-demo.db'
    assert not database.exists(), 'Doctor created a database'
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    http = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    token = secrets.token_urlsafe(32)
    environment = os.environ.copy()
    # Do not accidentally inherit a developer's SQL Server or rollback settings.
    environment.update(DemoToken=token, Concurrency='Optimistic', DemonstrateRollback='false')
    for key in list(environment):
        if key.lower() == 'connectionstrings__demo':
            del environment[key]

    def request(path, method='GET', body=None, authenticated=False):
        headers = {'Content-Type': 'application/json'}
        if authenticated:
            headers['Authorization'] = 'Bearer ' + token
        url = path if path.startswith(base + '/') else base + path
        req = urllib.request.Request(url, data=json.dumps(body).encode() if body is not None else None, method=method, headers=headers)
        try:
            response = http.open(req, timeout=20)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            content = response.read()
            return response.status, response.headers, json.loads(content) if content else None

    log_path = root / 'application.log'
    def start(rollback=False):
        environment['DemonstrateRollback'] = str(rollback)
        log = log_path.open('a', encoding='utf-8')
        process = subprocess.Popen(['dotnet', str(dll), '--urls', base], cwd=output, env=environment, stdout=log, stderr=log, creationflags=creation)
        deadline = time.monotonic() + 180
        try:
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise AssertionError(log_path.read_text(encoding='utf-8'))
                try:
                    if request('/health/ready')[0] == 200:
                        return process, log
                except (OSError, urllib.error.URLError):
                    pass
                time.sleep(.25)
            raise AssertionError('Generated application startup timed out.\n' + log_path.read_text(encoding='utf-8'))
        except BaseException:
            stop(process, log)
            raise

    def stop(process, log):
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=15)
        log.close()

    process, log = start()
    try:
        assert request('/books', 'POST', {'name': 'Book', 'persianName': 'کتاب'})[0] == 401
        status, headers, item = request('/books', 'POST', {'name': 'Book', 'persianName': 'کتاب'}, True)
        assert status == 201, (status, item)
        assert request(headers['Location'])[2] == item
        change = {'data': {'name': 'Updated book', 'persianName': 'کتاب جدید'}, 'expectedVersion': item['version']}
        path = '/books/' + item['id']
        assert request(path, 'PUT', change, True)[0] == 200
        stale = request(path, 'PUT', change, True)
        assert stale[0] == 409 and stale[2]['code'] == 'stale_version', stale
        assert request(path + '?expectedVersion=' + item['version'], 'DELETE', authenticated=True)[0] == 409
        assert request('/books?pageSize=1&sort=name')[2]['items'][0]['data']['name'] == 'Updated book'
    finally:
        stop(process, log)
    process, log = start(rollback=True)
    try:
        assert request('/books', 'POST', {'name': 'Rolled back', 'persianName': 'برگشت'}, True)[0] == 400
    finally:
        stop(process, log)
    with closing(sqlite3.connect(database)) as db:
        assert db.execute('select count(*) from Book').fetchone()[0] == 1
        assert db.execute('select count(*) from BookTranslation').fetchone()[0] == 1
        assert db.execute('select count(*) from OutboxMessage').fetchone()[0] == 2
    print('Generated app: Doctor without writes, authorization, Location, update, stale-version conflict and entity/translation/Outbox rollback passed.', flush=True)
