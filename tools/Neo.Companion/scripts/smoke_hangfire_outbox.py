"""Run against the isolated SQL Server from DurableMessagingDemo/compose.yaml."""
import argparse, json, pathlib, subprocess, time, urllib.request, urllib.error, uuid

parser = argparse.ArgumentParser()
parser.add_argument('--server-dll', required=True)
args = parser.parse_args()
dll = pathlib.Path(args.server_dll).resolve()
base = 'http://127.0.0.1:5091'

def request(path, data=None):
    req = urllib.request.Request(base + path, data=json.dumps(data).encode() if data is not None else None,
                                 headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=15) as response:
        text = response.read()
        return json.loads(text) if text else None

def wait_until(action, timeout=150):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        try:
            result = action()
            if result: return result
        except (OSError, urllib.error.URLError): pass
        time.sleep(.5)
    raise AssertionError('Timed out waiting for expected state')

process = None
log = pathlib.Path('hangfire-outbox-smoke.log').open('w+', encoding='utf-8')
def start():
    global process
    process = subprocess.Popen(['dotnet', str(dll)], cwd=dll.parent, stdout=log, stderr=subprocess.STDOUT)
    wait_until(lambda: request('/health/ready') is not None, 90)

def stop():
    if process and process.poll() is None:
        process.terminate()
        try: process.wait(timeout=15)
        except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=10)

try:
    start()
    rolled = str(uuid.uuid4())
    response = request('/receipts?rollback=true', {'operationId': rolled})
    assert response['rolledBack']
    assert request('/receipts/' + rolled) == {'requests': 0, 'effects': 0}
    operation = str(uuid.uuid4())
    result = request('/receipts', {'operationId': operation})
    # Committed work is in SQL before dispatch; survives an application restart.
    stop(); start()
    request('/dispatch', {})
    wait_until(lambda: request('/outbox/' + str(result['outboxId']))['outboxState'] == 4)
    assert request('/receipts/' + operation) == {'requests': 1, 'effects': 1}
    request('/dispatch', {})
    assert request('/receipts/' + operation)['effects'] == 1
    failed = str(uuid.uuid4())
    result = request('/receipts', {'operationId': failed, 'fail': True})
    request('/dispatch', {})
    state = wait_until(lambda: (r if (r := request('/outbox/' + str(result['outboxId'])))['outboxState'] in (11, 5) else None))
    assert state['processTryCount'] >= 1 and state['processError']
    assert request('/receipts/' + failed) == {'requests': 1, 'effects': 0}
    print('PASS: SQL/Hangfire transaction rollback, restart delivery, persisted success/failure, effect rollback and repeated dispatch')
except Exception:
    log.flush(); log.seek(0); print(log.read()[-24000:]); raise
finally:
    stop(); log.close()
