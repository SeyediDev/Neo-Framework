"""Run the real demo against an already-running, local Compose RabbitMQ. No broker/data cleanup."""
import argparse
import base64
import json
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--server-dll', required=True)
args = parser.parse_args()
base = 'http://127.0.0.1:5087'

def request(url, body=None):
    data = None if body is None else json.dumps(body).encode()
    headers = {'Content-Type': 'application/json'}
    if url.startswith('http://127.0.0.1:15672/'):
        headers['Authorization'] = 'Basic ' + base64.b64encode(b'neo_demo:neo_demo_local_only').decode()
    with urllib.request.urlopen(urllib.request.Request(url, data=data, headers=headers), timeout=5) as response:
        text = response.read().decode()
        return response.status, json.loads(text) if text.startswith(('{', '[')) else text

def wait_for(predicate, seconds=60):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError('Demo exited before verification')
        try:
            if predicate():
                return
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            pass
        time.sleep(.25)
    raise TimeoutError('Messaging verification deadline exceeded')

with tempfile.TemporaryFile(mode='w+', encoding='utf-8') as log:
    server = Path(args.server_dll).resolve()
    process = subprocess.Popen(['dotnet', str(server), '--urls=' + base,
        '--RabbitMq:Username=neo_demo', '--RabbitMq:Password=neo_demo_local_only',
        '--RabbitMq:EndpointPrefix=neo-demo'], stdout=log, stderr=subprocess.STDOUT, cwd=server.parent,
        creationflags=subprocess.CREATE_NO_WINDOW if hasattr(subprocess, 'CREATE_NO_WINDOW') else 0)
    try:
        wait_for(lambda: request(base + '/health/ready')[0] == 200)
        event = dict(eventId=str(uuid.uuid4()), orderId=str(uuid.uuid4()),
            occurredAt=datetime.now(timezone.utc).isoformat(), failOnce=True)
        assert request(base + '/orders', event)[0] == 202
        def completed(message):
            entries = request(base + '/observations')[1]
            return all(entries.get(f'{c}:{message["eventId"]}', {}).get('completed') for c in ['fulfillment', 'audit'])
        wait_for(lambda: completed(event))
        assert request(base + '/observations')[1][f'fulfillment:{event["eventId"]}']['attempts'] == 2
        assert request(base + '/orders', event)[0] == 202
        fence = dict(event, eventId=str(uuid.uuid4()), failOnce=False)
        assert request(base + '/orders', fence)[0] == 202
        wait_for(lambda: completed(fence))
        assert request(base + '/observations')[1][f'fulfillment:{event["eventId"]}']['attempts'] == 2
        broken = dict(event, eventId=str(uuid.uuid4()), failOnce=False, failPermanently=True)
        assert request(base + '/orders', broken)[0] == 202
        queue_url = 'http://127.0.0.1:15672/api/queues/%2F/neo-demo-fulfillment_error'
        wait_for(lambda: request(queue_url)[1].get('messages', 0) >= 1)
        entries = request(base + '/observations')[1]
        assert entries[f'fulfillment:{broken["eventId"]}'] == {'attempts': 1, 'completed': False}
        for decline, failures, unknown, expected in [(False, 0, False, 'Completed'), (True, 1, False, 'Cancelled'), (True, 3, False, 'ManualReview'), (False, 0, True, 'ManualReview')]:
            order = str(uuid.uuid4())
            assert request(base + '/sagas/orders', dict(orderId=order, declinePayment=decline, releaseFailures=failures, timeoutPayment=unknown))[0] == 202
            wait_for(lambda: request(base + '/sagas')[1].get(order, {}).get('state') == expected)
            effects = request(base + '/sagas/effects')[1]
            assert effects['inventory'][order]['reserveEffects'] == 1
            assert effects['inventory'][order]['releaseEffects'] == (1 if expected == 'Cancelled' else 0)
            if unknown:
                assert effects['inventory'][order]['reserved'] is True
                assert order not in effects['payments']
            if failures == 3:
                assert effects['inventory'][order]['releaseAttempts'] == 3
                assert request(base + f'/sagas/orders/{order}/retry-compensation', {})[0] == 202
                wait_for(lambda: request(base + '/sagas')[1].get(order, {}).get('state') == 'Cancelled')
                assert request(base + '/sagas/effects')[1]['inventory'][order]['releaseEffects'] == 1
        print(json.dumps({'status': 'passed', 'transport': 'RabbitMQ',
            'checks': ['readiness', 'publish-fanout', 'transient-retry', 'process-local-deduplication', 'error-queue', 'saga-success', 'saga-compensation', 'manual-compensation-retry', 'unknown-payment-reconciliation']}))
    except Exception:
        log.seek(0)
        print(log.read()[-12000:])
        raise
    finally:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
