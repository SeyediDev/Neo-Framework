from pathlib import Path
import subprocess, os, json, hashlib, datetime, re, sys, shutil
ROOT=Path("E:/SJVS/Projects/Neo")
E=ROOT/"docs/fanasa-platform-control-center/workstreams/portal-readers-evidence-2026-10-23/post-pip"
ART=ROOT/".build/portal-readers-post-pip-20261023"
E.mkdir(parents=True,exist_ok=True)
ART.mkdir(parents=True,exist_ok=False)
env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT="1",DOTNET_NOLOGO="1")
def now(): return datetime.datetime.now(datetime.timezone.utc).isoformat()
def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def write(name,value): (E/name).write_text(json.dumps(value,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
def snapshot():
    bases=["products/Fanasa.AccessManagement/src","products/Fanasa.AccessManagement/tests","products/Fanasa.UnifiedPortal/src","products/Fanasa.UnifiedPortal/tests","docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/src","docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/pcc","docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report"]
    exts={".cs",".csproj",".props",".targets",".slnx",".json",".cshtml",".js",".cjs",".css"}
    paths=set()
    for base in bases:
        for p in (ROOT/base).rglob("*"):
            if p.is_file() and p.suffix in exts and not any(x in {"bin","obj",".artifacts",".publish","App_Data","evidence"} for x in p.relative_to(ROOT/base).parts):
                paths.add(p)
    for base in [".","products/Fanasa.AccessManagement","products/Fanasa.UnifiedPortal","docs/fanasa-platform-control-center/workstreams/contract-policy/implementation"]:
        for pattern in ["Directory.*.props","Directory.*.targets","*.slnx","global.json","NuGet.Config"]:
            paths.update(p for p in (ROOT/base).glob(pattern) if p.is_file())
    return {p.relative_to(ROOT).as_posix():digest(p) for p in sorted(paths)}
manifest={"startedAtUtc":now(),"scope":"local Release integration after PIP","configuration":"Release","nugetAuditDisabledForLocalRunOnly":True,"deployed":False,"commands":[],"limitations":["Synthetic identities and isolated fixture databases; no live SSO, grants, Gateway or operational acceptance.","Selected source inventory excludes generated build output and evidence; no repository-wide lock."]}
before=snapshot();write("source-before.json",before)
def run(name,args,extra=None,timeout=600):
    print("START "+name,flush=True)
    record={"name":name,"command":args,"startedAtUtc":now(),"log":name+".log"}
    active=env.copy()
    if extra: active.update(extra)
    with (E/record["log"]).open("w",encoding="utf-8") as log:
        try:
            p=subprocess.run(args,cwd=ROOT,env=active,stdout=log,stderr=subprocess.STDOUT,timeout=timeout)
            record["exitCode"]=p.returncode
        except subprocess.TimeoutExpired: record["exitCode"]=124;record["timedOut"]=True
    record["completedAtUtc"]=now();record["logSha256"]=digest(E/record["log"])
    output=(E/record["log"]).read_text(encoding="utf-8")
    if name.startswith("UI."):
        m=re.search(r"^# tests (\d+)\s*$",output,re.M);record["checks"]=int(m.group(1)) if m else None
        for field in ["pass","fail","cancelled","skipped"]:
            m=re.search(r"^# "+field+r" (\d+)\s*$",output,re.M);record[field]=int(m.group(1)) if m else None
    elif name.startswith("Run."):
        m=re.findall(r"(\d+) (?:[\w -]+ )?checks(?: passed)?",output)
        record["checks"]=int(m[-1]) if m else None
    if name.startswith("Build."):
        m=re.findall(r"(?m)^\s*(\d+) Warning\(s\)",output);record["warnings"]=int(m[-1]) if m else None
        m=re.findall(r"(?m)^\s*(\d+) Error\(s\)",output);record["errors"]=int(m[-1]) if m else None
    manifest["commands"].append(record);write("verification.json",manifest)
    print("END "+name+" exit="+str(record["exitCode"])+" checks="+str(record.get("checks"))+"\n"+"\n".join(output.splitlines()[-5:]),flush=True)
    return record["exitCode"]==0
node=shutil.which("node"); dotnet=shutil.which("dotnet")
access="products/Fanasa.AccessManagement"; portal="products/Fanasa.UnifiedPortal"
ui=["governance-ui.test.cjs","organization-calendar-ui.test.cjs","pip-ui.test.cjs","pip-governance-ui.test.cjs","platform-navigation.test.cjs","architecture-ui.test.cjs","usage-accounting-ui.test.cjs"]
run("UI.AccessManagement",[node,"--test","--test-concurrency=1","--test-reporter=tap"]+[access+"/tests/"+x for x in ui],timeout=180)
run("UI.Portal",[node,"--test","--test-concurrency=1","--test-reporter=tap"]+[portal+"/tests/"+x for x in ["portal-client.test.cjs","service-catalog.test.cjs","workspace-ui.test.cjs","workspace-transport.test.cjs"]],timeout=180)
def build(name,path):
    return run("Build."+name,[dotnet,"build",path,"-c","Release","--artifacts-path",str(ART),"-p:NuGetAudit=false","-m:1","-nr:false","-v:minimal"])
central=build("AccessManagement",access+"/Fanasa.AccessManagement.slnx")
portal_ok=build("Portal",portal+"/Fanasa.UnifiedPortal.slnx")
consumer="docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report"
projects=[
("ConsumerApplicationCatalog.Tests",access+"/tests/ConsumerApplicationCatalog.Tests/ConsumerApplicationCatalog.Tests.csproj",central),
("PublishedCatalog.Tests",access+"/tests/PublishedCatalog.Tests/PublishedCatalog.Tests.csproj",central),
("ConsumerUsage.Api.Tests",access+"/tests/ConsumerUsage.Api.Tests/ConsumerUsage.Api.Tests.csproj",central),
("ConsumerUsageReport.Tests",consumer+"/tests/ConsumerUsageReport.Tests.csproj",central),
("PortalAdapter.Tests",consumer+"/portal-adapter/tests/PortalAdapter.Tests.csproj",central),
("PortalTransport.Tests",consumer+"/portal-transport/tests/PortalTransport.Tests.csproj",True),
("Organization.Temporal.Tests",access+"/tests/Organization.Temporal.Tests/Organization.Temporal.Tests.csproj",central),
("Organization.Calendar.Tests",access+"/tests/Organization.Calendar.Tests/Organization.Calendar.Tests.csproj",central)]
ready=[]
for name,path,prereq in projects:
    if prereq and build(name,path): ready.append(name)
if central: ready.append("Organization.Tests")
if portal_ok: ready+=["Portal.Tests","Discovery.Tests","UsageProjection.Tests","UsageTransport.Tests"]
manifest["assembliesBeforeRun"]=[{"path":p.relative_to(ROOT).as_posix(),"sha256":digest(p)} for p in sorted((ART/"bin").rglob("*.dll")) if p.name.endswith(".Tests.dll") or p.name in ["Fanasa.AccessManagement.Web.dll","Fanasa.UnifiedPortal.Web.dll"]]
for name in ready:
    matches=list((ART/"bin"/name.lower()).rglob(name+".dll")) if (ART/"bin"/name.lower()).exists() else []
    if not matches: matches=list((ART/"bin"/name).rglob(name+".dll"))
    if len(matches)!=1:
        manifest["commands"].append({"name":"Run."+name,"exitCode":125,"error":"Expected exactly one test assembly","matches":len(matches)});continue
    args=[dotnet,str(matches[0])]
    if name=="Discovery.Tests": args+=["--http"]
    if name=="PortalAdapter.Tests": args+=["--export-fixtures",str(ART/"adapter-fixtures")]
    run("Run."+name,args,timeout=240)
if (ART/"adapter-fixtures").exists():
    fixture_env={"CONSUMER_USAGE_REPORT_FIXTURES":str(ART/"adapter-fixtures")}
    run("UI.ConsumerReport",[node,"--test","--test-concurrency=1","--test-reporter=tap",consumer+"/portal-adapter/tests/usage-report-view.test.cjs"],fixture_env,timeout=180)
    fixture_dir=E/"adapter-fixtures";shutil.copytree(ART/"adapter-fixtures",fixture_dir)
after=snapshot();write("source-after.json",after)
manifest["sourceInventoryCount"]=len(before)
manifest["changedSources"]=[x for x in sorted(set(before)|set(after)) if before.get(x)!=after.get(x)]
manifest["assembliesAfterRun"]=[{"path":p.relative_to(ROOT).as_posix(),"sha256":digest(p)} for p in sorted((ART/"bin").rglob("*.dll")) if p.name.endswith(".Tests.dll") or p.name in ["Fanasa.AccessManagement.Web.dll","Fanasa.UnifiedPortal.Web.dll"]]
manifest["assemblyHashesStable"]=manifest["assembliesBeforeRun"]==manifest["assembliesAfterRun"]
manifest["dotnetChecks"]=sum(x.get("checks") or 0 for x in manifest["commands"] if x["name"].startswith("Run.") and x["exitCode"]==0)
manifest["nodeChecks"]=sum(x.get("checks") or 0 for x in manifest["commands"] if x["name"].startswith("UI.") and x["exitCode"]==0)
manifest["allCommandsPassed"]=all(x["exitCode"]==0 for x in manifest["commands"])
manifest["completedAtUtc"]=now();write("verification.json",manifest)
print(json.dumps({k:manifest[k] for k in ["allCommandsPassed","dotnetChecks","nodeChecks","sourceInventoryCount","changedSources","assemblyHashesStable"]}),flush=True)
sys.exit(0 if manifest["allCommandsPassed"] and not manifest["changedSources"] and manifest["assemblyHashesStable"] else 1)
