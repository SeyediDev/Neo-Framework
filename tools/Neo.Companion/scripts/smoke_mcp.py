"""Exercise the real MCP server over stdio. Requires Python 3.10+ and .NET 10."""
import argparse
import json
import os
from pathlib import Path
import queue
import subprocess
import threading

parser = argparse.ArgumentParser()
parser.add_argument('--server-dll', required=True)
parser.add_argument('--project-root', required=True)
parser.add_argument('--doctor-expect-code', help='Optional expected Doctor code, or none for no findings.')
args = parser.parse_args()
env = os.environ.copy()
env['NEO_PROJECT_ROOT'] = str(Path(args.project_root).resolve())
creation = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
process = subprocess.Popen(['dotnet', str(Path(args.server_dll).resolve())],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    text=True, encoding='utf-8', env=env, creationflags=creation)
messages = queue.Queue()
errors = []

def read_stdout():
    for line in process.stdout:
        try: messages.put(json.loads(line))
        except json.JSONDecodeError: messages.put({'invalid_stdout': line})

def read_stderr():
    for line in process.stderr: errors.append(line)

threading.Thread(target=read_stdout, daemon=True).start()
threading.Thread(target=read_stderr, daemon=True).start()

def send(message):
    process.stdin.write(json.dumps(message) + '\n')
    process.stdin.flush()

def request(identifier, method, params):
    send({'jsonrpc': '2.0', 'id': identifier, 'method': method, 'params': params})
    while True:
        result = messages.get(timeout=30)
        assert 'invalid_stdout' not in result, result
        if result.get('id') == identifier:
            assert 'error' not in result, result
            return result['result']

def call(identifier, name, arguments):
    result = request(identifier, 'tools/call', {'name': name, 'arguments': arguments})
    assert not result.get('isError'), result
    text = next(x['text'] for x in result['content'] if x['type'] == 'text')
    return json.loads(text)

try:
    init = request(1, 'initialize', {'protocolVersion': '2025-06-18',
        'capabilities': {}, 'clientInfo': {'name': 'neo-companion-smoke', 'version': '0.1.0'}})
    send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})
    tools = request(2, 'tools/list', {})['tools']
    expected = {'neo_inspect_project', 'neo_search_docs', 'neo_get_example', 'neo_get_telemetry_recipe', 'neo_diagnose_project'}
    assert {x['name'] for x in tools} == expected, tools
    assert all(x['annotations']['readOnlyHint'] for x in tools), tools
    docs = call(3, 'neo_search_docs', {'query': 'ICommandRepository', 'limit': 8})
    assert docs['results']
    inspection = call(4, 'neo_inspect_project', {})
    assert inspection['mode'] == 'static-declarations' and inspection['projects']
    example = call(5, 'neo_get_example', {'example': 'product-create', 'baseline': docs['baseline']})
    assert any(x['path'] == 'Application/CreateProduct.cs' for x in example['files'])
    mismatch = request(6, 'tools/call', {'name': 'neo_get_example', 'arguments': {'example': 'product-create', 'baseline': 'wrong-version'}})
    assert mismatch.get('isError') is True, mismatch
    for identifier, mode in [(7, 'manual'), (8, 'attribute')]:
        recipe = call(identifier, 'neo_get_telemetry_recipe', {'mode': mode, 'baseline': docs['baseline']})
        assert recipe['mode'] == mode and recipe['files']
        assert 'ITelementryBehaviour' in recipe['guide']
    diagnosis = call(9, 'neo_diagnose_project', {'configurationSection': 'TelemetryOptions'})
    assert diagnosis['mode'] == 'csharp-syntax-and-json' and diagnosis['limitations']
    assert diagnosis['status'] in {'review-required', 'incomplete'}
    if args.doctor_expect_code == 'none':
        assert not diagnosis['findings'], diagnosis
    elif args.doctor_expect_code:
        assert any(x['code'] == args.doctor_expect_code for x in diagnosis['findings']), diagnosis
    print(json.dumps({'status': 'passed', 'protocol': init['protocolVersion'], 'tools': sorted(expected),
        'example_files': len(example['files']), 'projects': len(inspection['projects']),
        'doctor_codes': sorted({x['code'] for x in diagnosis['findings']}),
        'checks': ['initialize', 'tool-discovery', 'read-only-annotations', 'docs-search', 'project-inspection', 'example-retrieval', 'wrong-version-error', 'telemetry-manual', 'telemetry-attribute', 'doctor-diagnosis']}, indent=2))
finally:
    process.stdin.close()
    try: process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.terminate()
        try: process.wait(timeout=5)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
