import json, queue, subprocess, threading, hashlib, uuid, shutil
from pathlib import Path
root = Path(__file__).resolve().parents[1]
test_root = Path.cwd()/'.build'/('neo-react-lsp-test-'+uuid.uuid4().hex)
test_root.mkdir(parents=True)
workspace = test_root/'workspace'
workspace.mkdir(exist_ok=True)
types = str(root/'node_modules/@types')
(workspace/'tsconfig.json').write_text(json.dumps({'compilerOptions':{'strict':True,'allowJs':True,'checkJs':True,'jsx':'react-jsx','target':'ES2022','moduleResolution':'node','typeRoots':[types]}}))
app = workspace/'App.tsx'
dependency = workspace/'Badge.tsx'
app_text = 'import { Badge } from "./Badge";\nconst label="فارسی 😀"; export const view=<Badge count="wrong"/>;\n'
dependency_text = 'export function Badge(props:{count:number}) { return <span>{props.count}</span>; }\n'
app.write_text(app_text,encoding='utf-8',newline=''); dependency.write_text(dependency_text,encoding='utf-8',newline='')

class Session:
    def __init__(self):
        self.process = subprocess.Popen([shutil.which('node'),str(root/'server.mjs'),str(test_root/'timing.jsonl')],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        self.messages = queue.Queue(); self.seq = 0
        def read():
            try:
                while True:
                    headers = {}
                    while True:
                        line = self.process.stdout.readline()
                        if not line: raise EOFError()
                        if line == b'\r\n': break
                        name,value = line.decode().split(':',1); headers[name.lower()] = value.strip()
                    size = int(headers['content-length']); body = bytearray()
                    while len(body)<size:
                        chunk = self.process.stdout.read(size-len(body))
                        if not chunk: raise EOFError()
                        body.extend(chunk)
                    self.messages.put(json.loads(body))
            except Exception as error: self.messages.put(error)
        threading.Thread(target=read,daemon=True).start()
    def send(self,method,params=None,request=False):
        value = {'jsonrpc':'2.0','method':method}
        if params is not None: value['params']=params
        if request: self.seq+=1; value['id']=self.seq
        body=json.dumps(value,ensure_ascii=False).encode()
        self.process.stdin.write(f'Content-Length: {len(body)}\r\n\r\n'.encode()+body); self.process.stdin.flush()
        if not request: return
        message=self.messages.get(timeout=300)
        if isinstance(message,Exception): raise message
        assert message.get('id')==self.seq, message
        return message
    def open(self):
        result=self.send('initialize',{'rootUri':workspace.as_uri()+'/'},True)
        assert result['result']['capabilities']['diagnosticProvider']
        self.send('initialized',{})
        self.send('textDocument/didOpen',{'textDocument':{'uri':app.as_uri(),'version':1,'languageId':'typescriptreact','text':app.read_text(encoding='utf-8')}})
    def pull(self): return self.send('textDocument/diagnostic',{'textDocument':{'uri':app.as_uri()}},True)
    def close(self):
        try:
            self.send('shutdown',request=True); self.send('exit')
            self.process.wait(timeout=10)
            assert self.process.returncode==0
        finally:
            if self.process.poll() is None: self.process.kill(); self.process.wait(timeout=10)

evidence={}
s=Session()
try:
    s.open(); first=s.pull()['result']
    assert [x['code'] for x in first['items']]==[2322]
    assert first['resultId']==hashlib.sha256(app_text.encode()).hexdigest()
    symbol=app_text.splitlines()[1].index('<Badge')+1
    utf16=len(app_text.splitlines()[1][:symbol].encode('utf-16-le'))//2
    definition=s.send('textDocument/definition',{'textDocument':{'uri':app.as_uri()},'position':{'line':1,'character':utf16}},True)['result']
    assert definition[0]['uri']==dependency.as_uri()
    app.write_text(app_text.replace('count="wrong"','count={42}'),encoding='utf-8',newline='')
    rejected=s.pull(); assert 'error' in rejected and 'result' not in rejected
    evidence.update(documentMutationRejected=True,oldReportNotReplayed=True,unicodeDefinitionVerified=True)
finally: s.close()
s=Session()
try:
    s.open(); current=s.pull()['result']; assert current['items']==[] and current['resultId']!=first['resultId']
    dependency.write_text(dependency_text.replace('count:number','count:string'),encoding='utf-8',newline='')
    rejected=s.pull(); assert 'error' in rejected
    evidence.update(newSessionHealthy=True,newSnapshotHasDistinctResultId=True,dependencyMutationRejected=True)
finally:
    s.close(); app.write_text(app_text,encoding='utf-8',newline=''); dependency.write_text(dependency_text,encoding='utf-8',newline='')
evidence.update(scope='Independent snapshot server protocol tests; existing Neo client tested separately',serverSha256=hashlib.sha256((root/'server.mjs').read_bytes()).hexdigest())
(test_root/'evidence.json').write_text(json.dumps(evidence,indent=2),encoding='utf-8',newline='')
print(json.dumps(evidence,indent=2))

print('Evidence:',test_root/'evidence.json')
