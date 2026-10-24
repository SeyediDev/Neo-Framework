from pathlib import Path
import os, subprocess, json, re, time, urllib.request, urllib.error, hashlib, sys
ROOT=Path("E:/SJVS/Projects/Neo")
ART=ROOT/".build/portal-readers-post-pip-20261023"
E=ROOT/"docs/fanasa-platform-control-center/workstreams/portal-readers-evidence-2026-10-23/post-pip"
target=ART/"bin/Fanasa.AccessManagement.Web/release/Fanasa.AccessManagement.Web.dll"
work=ART/"startup-smoke"
work.mkdir(exist_ok=False)
content=work/"content";content.mkdir();(content/"wwwroot").mkdir()
config={
"Authentication":{"Authority":"https://127.0.0.1:1/realms/fanasa","ClientId":"synthetic-local-startup","ClientSecret":""},
"Fabric":{"DataDirectory":str(work/"fabric"),"DataProtectionKeysPath":str(work/"keys"),"ImportLegacyOnStartup":False},
"Platform":{"RegistryPath":str(work/"platform/platform-registry.db"),"ConsumerCatalog":{"Enabled":False}},
"Organization":{"DataDirectory":str(work/"organization"),"RequireSubscription":True,"HrApi":{"Enabled":False},"Bearer":{"Enabled":False},"TemporalAccess":{"Enabled":False},"MeterChanges":False},
"Accounting":{"DataDirectory":str(work/"accounting")},
"Pip":{"Transport":{"Bindings":[]},"Secrets":{}},
"UsageMetering":{"Organization":{"Enabled":False},"Outbox":{"Enabled":False}},
"Payments":{"Zarinpal":{"Enabled":False}},
"ContractPolicy":{k:{"Enabled":False} for k in ["Organization","Producer","GatewayRequest","GatewayContext","GatewayDecision"]},
"ConsumerUsageReport":{"Enabled":False},"AllowedHosts":"127.0.0.1;localhost"
}
(content/"appsettings.json").write_text(json.dumps(config),encoding="utf-8")
env={k:v for k,v in os.environ.items() if k.upper() in {"PATH","SYSTEMROOT","WINDIR","TEMP","TMP","USERPROFILE","LOCALAPPDATA","PROGRAMFILES","PROGRAMFILES(X86)","DOTNET_ROOT","DOTNET_ROOT(X86)","COMSPEC"}}
env.update({"DOTNET_ENVIRONMENT":"Production","ASPNETCORE_ENVIRONMENT":"Production","DOTNET_CLI_TELEMETRY_OPTOUT":"1","DOTNET_NOLOGO":"1"})
report={"scope":"actual Web process with synthetic settings and fresh local state","checks":[],"webDllSha256":hashlib.sha256(target.read_bytes()).hexdigest(),"contentRoot":str(content),"configuration":config,"liveIdentityOrGatewayTested":False,"externalConnectionMonitoringPerformed":False}
log=E/"Startup.Web.log"
p=None
try:
    with log.open("w",encoding="utf-8") as output:
        p=subprocess.Popen(["dotnet",str(target),"--contentRoot",str(content),"--urls","http://127.0.0.1:0"],cwd=content,env=env,stdout=output,stderr=subprocess.STDOUT)
        deadline=time.monotonic()+40; port=None
        while time.monotonic()<deadline and p.poll() is None:
            text=log.read_text(encoding="utf-8")
            m=re.search(r"Now listening on: http://127.0.0.1:(\d+)",text)
            if m: port=m.group(1);break
            time.sleep(.2)
        if port is None: raise RuntimeError("Host did not start listening")
        client=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPHandler())
        for path,expected in [("/health/live",200),("/api/platform/published-services",401),("/api/platform/application-tenants?subject=ffffffff-ffff-4fff-bfff-ffffffffffff",401)]:
            try:
                with client.open("http://127.0.0.1:"+port+path,timeout=5) as r: status=r.status; body=r.read(65536).decode()
            except urllib.error.HTTPError as error: status=error.code;body=error.read(65536).decode()
            check={"path":path,"expectedStatus":expected,"actualStatus":status,"passed":status==expected}
            if path=="/health/live": check["body"]=json.loads(body);check["passed"]=check["passed"] and check["body"].get("status")=="Healthy"
            report["checks"].append(check)
        report["allPassed"]=all(x["passed"] for x in report["checks"]) and len(report["checks"])==3
except Exception as error: report["failureType"]=type(error).__name__;report["allPassed"]=False
finally:
    if p is not None:
        if p.poll() is None:
            p.terminate()
            try: p.wait(timeout=8)
            except subprocess.TimeoutExpired: p.kill();p.wait(timeout=5)
        report["processStopped"]=p.poll() is not None
        report["processExitCodeAfterTermination"]=p.returncode
    report["logSha256"]=hashlib.sha256(log.read_bytes()).hexdigest()
    (E/"startup-verification.json").write_text(json.dumps(report,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print(json.dumps(report,ensure_ascii=False,indent=2))
sys.exit(0 if report["allPassed"] else 1)
