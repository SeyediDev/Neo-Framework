from pathlib import Path
import subprocess, json, hashlib, datetime, re, sys, shutil, os
R=Path("E:/SJVS/Projects/Neo")
A=R/".build/portal-readers-post-pip-20261023"
E=R/"docs/fanasa-platform-control-center/workstreams/portal-readers-evidence-2026-10-23/post-pip"
initial=json.loads((E/"verification.json").read_text(encoding="utf-8"))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def now(): return datetime.datetime.now(datetime.timezone.utc).isoformat()
def parse_checks(output):
    m=re.findall(r"(\d+) (?:[\w -]+ )?checks(?: passed)?",output)
    if m: return int(m[-1])
    m=re.search(r"PASS (\d+) groups; (\d+) assertions",output)
    return int(m.group(2)) if m else None
result={"startedAtUtc":now(),"scope":"final normalized Release build and corrected fixture layout","commands":[],"sourceEditsMade":False,"operationalChangesMade":False,"initialVerification":"verification.json"}
latest={x["name"].removeprefix("Run."):dict(x) for x in initial["commands"] if x["name"].startswith("Run.")}
for name,x in latest.items():
    if x.get("checks") is None: x["checks"]=parse_checks((E/x["log"]).read_text(encoding="utf-8"))
def suite_files(name):
    base=A/"bin"/name/"release"
    return {p.name:sha(p) for p in base.glob("*.dll") if p.name in [name+".dll","Fanasa.AccessManagement.Web.dll","Fanasa.ContractPolicy.dll","Fanasa.UnifiedPortal.Web.dll"]}
before={name:suite_files(name) for name in latest}
def command(name,args,cwd=R,timeout=600):
    print("START "+name,flush=True)
    rec={"name":name,"command":args,"cwd":str(cwd),"log":name+".log","startedAtUtc":now()}
    with (E/rec["log"]).open("w",encoding="utf-8") as log:
        try: rec["exitCode"]=subprocess.run(args,cwd=cwd,stdout=log,stderr=subprocess.STDOUT,timeout=timeout).returncode
        except subprocess.TimeoutExpired: rec["exitCode"]=124;rec["timedOut"]=True
    out=(E/rec["log"]).read_text(encoding="utf-8")
    rec.update({"completedAtUtc":now(),"logSha256":sha(E/rec["log"])})
    if "Run." in name: rec["checks"]=parse_checks(out)
    result["commands"].append(rec)
    print("END "+name+" exit="+str(rec["exitCode"])+" checks="+str(rec.get("checks"))+"\n"+"\n".join(out.splitlines()[-5:]),flush=True)
    (E/"integration-final-verification.json").write_text(json.dumps(result,indent=2)+"\n",encoding="utf-8")
    return rec
rec=command("Final.Build.AccessManagement",["dotnet","build","products/Fanasa.AccessManagement/Fanasa.AccessManagement.slnx","-c","Release","--no-restore","--artifacts-path",str(A),"-p:NuGetAudit=false","-p:ShouldUnsetParentConfigurationAndPlatform=false","-m:1","-nr:false","-v:minimal"])
after={name:suite_files(name) for name in latest}
result["runtimeFilesBeforeNormalization"]=before;result["runtimeFilesAfterNormalization"]=after
changed=[name for name in before if before[name]!=after[name]]
result["suitesWhoseRuntimeFilesChanged"]=changed
for name in changed:
    if latest[name]["exitCode"]==0:
        args=["dotnet",str(A/"bin"/name/"release"/(name+".dll"))]
        if name=="Discovery.Tests": args+=["--http"]
        if name=="PortalAdapter.Tests": args+=["--export-fixtures",str(A/"adapter-fixtures-final")]
        latest[name]=command("Final.Run."+name,args,timeout=300)
stage=A/"portal-staging";output=stage/"tests/Discovery.Tests/bin/Release/net10.0"
shutil.copytree(A/"bin/Discovery.Tests/release",output)
assets=stage/"src/Fanasa.UnifiedPortal.Web/wwwroot"
shutil.copytree(R/"products/Fanasa.UnifiedPortal/src/Fanasa.UnifiedPortal.Web/wwwroot",assets)
latest["Discovery.Tests"]=command("Final.Run.Discovery.Tests",["dotnet",str(output/"Discovery.Tests.dll"),"--http"],cwd=stage,timeout=240)
result["fixtureLayoutCorrection"]="Discovery HTTP fixture assumes the conventional bin/Release/net10.0 layout; copied unchanged built assemblies and current static assets into an isolated compatible layout."
command("Final.Startup.Web",[sys.executable,str(E/"startup-smoke-retry.py")],timeout=180)
startup=json.loads((E/"startup-verification-retry.json").read_text(encoding="utf-8"))
result["startupVerification"]="startup-verification-retry.json";result["startupPassed"]=startup["allPassed"]
result["selectedFinalTestRuns"]=list(latest.values())
result["dotnetChecks"]=sum(x.get("checks") or 0 for x in latest.values() if x["exitCode"]==0)
result["nodeChecks"]=initial["nodeChecks"]
result["runtimeAssemblyCopies"]={}
for name in ["Fanasa.AccessManagement.Web.dll","Fanasa.ContractPolicy.dll","Fanasa.UnifiedPortal.Web.dll"]:
    copies=[p for p in (A/"bin").rglob(name) if p.parent.name=="release"]
    result["runtimeAssemblyCopies"][name]={"allCopiesIdentical":len({sha(p) for p in copies})==1,"copies":[{"path":p.relative_to(R).as_posix(),"sha256":sha(p)} for p in copies]}
result["startupAssemblyMatchesFinal"]=startup["webDllSha256"]==sha(A/"bin/Fanasa.AccessManagement.Web/release/Fanasa.AccessManagement.Web.dll")
before_source=json.loads((E/"source-before.json").read_text(encoding="utf-8"))
changed_source=[p for p,h in before_source.items() if not (R/p).exists() or sha(R/p)!=h]
result["changedSources"]=changed_source
result["sourceInventoryCount"]=len(before_source)
result["historicalInitialFailures"]=["Discovery HTTP fixture path was incompatible with --artifacts-path; see Run.Discovery.Tests.log.","First actual-host health request exceeded the initial five-second client timeout; see Startup.Web.log and startup-verification.json. The final run uses a fresh state root and a 45-second request timeout."]
result["allFinalChecksPassed"]=all(x["exitCode"]==0 for x in latest.values()) and all(x.get("checks") is not None for x in latest.values()) and all(x["exitCode"]==0 for x in result["commands"]) and all(x["allCopiesIdentical"] for x in result["runtimeAssemblyCopies"].values()) and startup["allPassed"] and result["startupAssemblyMatchesFinal"] and not changed_source
result["completedAtUtc"]=now()
(E/"integration-final-verification.json").write_text(json.dumps(result,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print(json.dumps({k:result[k] for k in ["allFinalChecksPassed","dotnetChecks","nodeChecks","changedSources","suitesWhoseRuntimeFilesChanged","startupPassed","startupAssemblyMatchesFinal"]}),flush=True)
sys.exit(0 if result["allFinalChecksPassed"] else 1)
