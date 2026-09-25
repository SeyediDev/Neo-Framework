"""Exercise SQL Server + RabbitMQ: rollback, publisher restart, persistent saga and two workers."""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

parser = argparse.ArgumentParser()
parser.add_argument('--server-dll', required=True)
args = parser.parse_args()
server = Path(args.server_dll).resolve()
base = 'http://127.0.0.1:5089'
processes = []
logs = []

def request(path, body=None, origin=base):
    payload = None if body is None else json.dumps(body).encode()
    try:
        response = urllib.request.urlopen(urllib.request.Request(origin + path, data=payload, headers={'Content-Type': 'application/json'}), timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        text = response.read().decode()
        return response.status, json.loads(text) if text.startswith(('{', '[')) else text

def wait_for(predicate, seconds=90):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if any(p.poll() is not None for p in processes):
            raise RuntimeError('Demo exited unexpectedly')
        try:
            if predicate():
                return
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            pass
        time.sleep(.2)
    raise TimeoutError('Durable messaging verification timed out')

def start(defer=False, port=5089, initialize=False):
    log = tempfile.TemporaryFile(mode='w+', encoding='utf-8')
    logs.append(log)
    origin = f'http://127.0.0.1:{port}'
    process = subprocess.Popen(['dotnet', str(server), '--urls=' + origin,
        '--DeferDelivery=' + str(defer), '--InitializeDatabase=' + str(initialize)],
        cwd=server.parent, stdout=log, stderr=subprocess.STDOUT,
        creationflags=subprocess.CREATE_NO_WINDOW if hasattr(subprocess, 'CREATE_NO_WINDOW') else 0)
    processes.append(process)
    wait_for(lambda: request('/health/ready', origin=origin)[0] == 200)
    return process

def stop(process):
    process.terminate()
    try:
        process.wait(timeout=20)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
    processes.remove(process)

def state(order):
    return request('/state/' + order)[1]

def until(order, expected):
    wait_for(lambda: (state(order).get('saga') or {}).get('currentState') == expected)

try:
    process = start(defer=True, initialize=True)
    # This smoke uses a fresh demo DB/broker in CI; it never drops user data.
    initial_pending = request('/outbox')[1]['pendingMessages']
    rolled_back = str(uuid.uuid4())
    assert request('/orders?rollback=true', {'orderId': rolled_back})[0] == 200
    assert not state(rolled_back)['orderExists']
    assert request('/outbox')[1]['pendingMessages'] == initial_pending
    persisted = str(uuid.uuid4())
    assert request('/orders', {'orderId': persisted})[0] == 202
    assert request('/outbox')[1]['pendingMessages'] > initial_pending
    assert state(persisted)['saga'] is None
    stop(process)
    process = start()
    until(persisted, 'Completed')
    assert state(persisted)['payment']['effects'] == 1
    assert state(rolled_back)['saga'] is None
    replica = start(port=5090)
    for _ in range(6):
        order = str(uuid.uuid4())
        assert request('/orders', {'orderId': order, 'declinePayment': True, 'releaseFailures': 1})[0] == 202
        until(order, 'Cancelled')
        effect = state(order)['inventory']
        assert effect['reserveEffects'] == 1 and effect['releaseEffects'] == 1 and not effect['reserved']
    stop(replica)
    manual = str(uuid.uuid4())
    assert request('/orders', {'orderId': manual, 'declinePayment': True, 'releaseFailures': 3})[0] == 202
    until(manual, 'ManualReview')
    stop(process)
    process = start()
    assert state(manual)['saga']['currentState'] == 'ManualReview'
    assert request('/orders/' + manual + '/retry-compensation', {})[0] == 202
    until(manual, 'Cancelled')
    assert state(manual)['inventory']['releaseEffects'] == 1
    unknown = str(uuid.uuid4())
    assert request('/orders', {'orderId': unknown, 'timeoutPayment': True})[0] == 202
    until(unknown, 'ManualReview')
    assert state(unknown)['inventory']['reserved']
    assert request('/orders/' + unknown + '/retry-compensation', {})[0] == 409
    assert request('/orders', {'orderId': persisted})[0] == 409
    assert state(persisted)['payment']['effects'] == 1
    print(json.dumps({'status': 'passed', 'transport': 'RabbitMQ', 'storage': 'SQL Server',
        'checks': ['transaction-rollback', 'publisher-restart', 'two-workers', 'persistent-saga', 'compensation-after-restart', 'unknown-payment-reconciliation', 'duplicate-order']}))
except Exception:
    for log in logs:
        log.seek(0)
        print(log.read()[-14000:])
    raise
finally:
    for process in list(processes):
        stop(process)
    for log in logs:
        log.close()
