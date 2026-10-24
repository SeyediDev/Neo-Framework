python3 - <<'PY'
import subprocess, pathlib, json, os, datetime
def run(args):
    p=subprocess.run(args,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,timeout=12)
    if p.returncode: return None
    return p.stdout
def read_config(pid,cwd):
    try:
        env=dict(x.split("=",1) for x in pathlib.Path("/proc/"+str(pid)+"/environ").read_bytes().decode().split("\0") if "=" in x)
    except Exception as e: return {"error":type(e).__name__}
    en=env.get("DOTNET_ENVIRONMENT",env.get("ASPNETCORE_ENVIRONMENT","Production"))
    root=pathlib.Path("/proc/"+str(pid)+"/root")
    flat={}; files=[]
    def flatten(v,prefix=""):
        if isinstance(v,dict):
            for k,x in v.items(): flatten(x,(prefix+":"+k).strip(":"))
        else: flat[prefix.lower()]=v
    for name in ["appsettings.json","appsettings."+en+".json"]:
        path=root/pathlib.Path(cwd).relative_to("/")/name
        if path.exists():
            try: flatten(json.loads(path.read_text(encoding="utf-8-sig"))); files.append({"name":name,"readable":True})
            except Exception as e: files.append({"name":name,"readable":False,"error":type(e).__name__})
    for k,v in env.items():
        if "__" in k: flat[k.replace("__",":").lower()]=v
    args=pathlib.Path("/proc/"+str(pid)+"/cmdline").read_bytes().decode().split("\0")
    for i,arg in enumerate(args):
        if arg.startswith("--"):
            pair=arg[2:].split("=",1)
            if len(pair)==2: flat[pair[0].lower()]=pair[1]
            elif i+1<len(args) and args[i+1] and not args[i+1].startswith("--"): flat[pair[0].lower()]=args[i+1]
    runtime[cwd]=flat
    keys=["Platform:ServiceCatalog:ClientId","Platform:ServiceCatalog:ClientProductKey","Platform:ConsumerCatalog:Enabled","Platform:ServiceCatalog:OfficialLinkHosts","Platform:RegistryPath","Fabric:DataDirectory"]
    out={}
    for key in keys:
        val=flat.get(key.lower())
        out[key]={"present":val is not None,"nonblank":val is not None and bool(str(val).strip()),"value":val}
    portal_keys=[k for k in flat if any(x in k for x in ["catalog","discovery","publishedservice","applicationtenant","platform"]) and not any(x in k for x in ["secret","password","token","credential","certificate","key"])]
    if "portal" in cwd.lower():
        out["portalSafeSettings"]={k:flat[k] for k in portal_keys if not isinstance(flat[k],dict)}
    return {"environment":en,"files":files,"selected":out}
runtime={}
units=json.loads(run(["systemctl","list-units","--all","--type=service","--output=json","--no-pager"]) or "[]")
selected=[x["unit"] for x in units if any(s in x["unit"] for s in ["fanasa-access-management@development","fanasa-unified-portal@development"])]
out={"observedAtUtc":datetime.datetime.now(datetime.timezone.utc).isoformat(),"mode":"read-only","units":[]}
for unit in selected:
    props=run(["systemctl","show",unit,"-p","ActiveState","-p","SubState","-p","MainPID","-p","WorkingDirectory"])
    vals=dict(l.split("=",1) for l in (props or "").splitlines() if "=" in l)
    pid=int(vals.get("MainPID","0")); item={"unit":unit,"properties":vals}
    if pid:
        cwd=os.readlink("/proc/"+str(pid)+"/cwd")
        item["processCwd"]=cwd; item["configuration"]=read_config(pid,cwd)
        dll=pathlib.Path("/proc/"+str(pid)+"/root")/pathlib.Path(cwd).relative_to("/")/"Fanasa.AccessManagement.Web.dll"
        if dll.exists():
            import hashlib
            item["webDllSha256"]=hashlib.sha256(dll.read_bytes()).hexdigest()
    out["units"].append(item)

import urllib.request, urllib.parse, urllib.error, ssl, sqlite3, base64
def config_for(part):
    return next((v for k,v in runtime.items() if part in k),{})
central=config_for("fanasa-access-management"); portal=config_for("fanasa-unified-portal")
def get(v,k): return v.get(k.lower())
def request(url,data=None,token=None):
    headers={"Accept":"application/json"}
    if token: headers["Authorization"]="Bearer "+token
    if data is not None: headers["Content-Type"]="application/x-www-form-urlencoded"
    req=urllib.request.Request(url,data=data,headers=headers)
    opener=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPSHandler(context=ssl.create_default_context()))
    try:
        with opener.open(req,timeout=12) as r: return r.status,r.read(1048577),r.headers.get("Content-Type")
    except urllib.error.HTTPError as e: return e.code,e.read(1048577),e.headers.get("Content-Type")
authority=get(portal,"PlatformControlCenter:Authority") or get(portal,"Authentication:Authority")
base=get(portal,"PlatformControlCenter:BaseUrl")
client=get(portal,"PlatformControlCenter:ClientId")
secret=get(portal,"PlatformControlCenter:ClientSecret")
scope=get(portal,"PlatformControlCenter:Scope") or "platform.catalog"
probe={"tokenPersisted":False,"tlsVerification":"system CA and hostname verification"}
try:
    if urllib.parse.urlsplit(authority).hostname!="sso.fanasa.net.local": raise ValueError("Unexpected authority")
    if urllib.parse.urlsplit(base).hostname not in ("127.0.0.1","localhost"): raise ValueError("Unexpected API target")
    form=urllib.parse.urlencode({"grant_type":"client_credentials","client_id":client,"client_secret":secret,"scope":scope}).encode()
    status,body,_=request(authority.rstrip("/")+"/protocol/openid-connect/token",form)
    probe["tokenEndpointStatus"]=status
    if status==200:
        token=json.loads(body).get("access_token")
        claims=json.loads(base64.urlsafe_b64decode(token.split(".")[1]+"==="))
        aud=claims.get("aud",[]); aud=[aud] if isinstance(aud,str) else aud
        probe["tokenClaimDiagnostics"]={"azpMatchesExistingClient":claims.get("azp")==client,"catalogScopePresent":"platform.catalog" in claims.get("scope","").split(),"expectedAudiencePresent":"fanasa-access-management-web" in aud}
        probe["responses"]=[]
        for path in ["/api/platform/published-services","/api/platform/application-tenants?subject=ffffffff-ffff-4fff-bfff-ffffffffffff"]:
            status,body,ctype=request(base.rstrip("/")+path,token=token)
            try: error=json.loads(body).get("error")
            except Exception: error=None
            probe["responses"].append({"path":path,"status":status,"contentType":ctype,"error":error})
        token=None; body=None; claims=None
except Exception as e: probe["failureType"]=type(e).__name__
out["httpProbe"]=probe
def ci(v,k):
    return next((x for a,x in v.items() if a.casefold()==k.casefold()),None) if isinstance(v,dict) else None
def open_ro(path):
    c=sqlite3.connect(pathlib.Path(path).as_uri()+"?mode=ro",uri=True,timeout=5)
    c.execute("PRAGMA query_only=ON")
    return c
storage={"mode":"SQLite mode=ro; query_only=ON","observedAtUtc":datetime.datetime.now(datetime.timezone.utc).isoformat()}
try:
    with open_ro(get(central,"Platform:RegistryPath")) as c:
        tables={x[0] for x in c.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        names=["platform_products","platform_application_offers","platform_application_grants"]
        storage["registryTables"]={t:{"present":t in tables,"rows":c.execute("SELECT COUNT(*) FROM "+t).fetchone()[0] if t in tables else None} for t in names}
        products=[json.loads(x[0]) for x in c.execute("SELECT data FROM platform_products LIMIT 2001")]
        storage["productJsonValid"]=True
    with open_ro(pathlib.Path(get(central,"Fabric:DataDirectory"))/"fabric.db") as c:
        docs={}
        for kind in ["client","tenant","membership","platform.service-publication.v1"]:
            rows=c.execute("SELECT json FROM fabric_documents WHERE kind=? LIMIT 2001",(kind,)).fetchall()
            docs[kind]=[json.loads(x[0]) for x in rows]
        storage["fabricDocumentCountsBounded"]={k:len(v) for k,v in docs.items()}
        storage["fabricSelectedJsonValid"]=True
        clients=[x for x in docs["client"] if ci(x,"Key")==client]
        storage["existingPortalClient"]={"matches":len(clients)}
        if len(clients)==1:
            cl=clients[0]
            p=[x for x in products if ci(x,"Id")==ci(cl,"ProductId")]
            info=storage["existingPortalClient"]
            info.update({"active":ci(cl,"IsActive"),"clientType":ci(cl,"ClientType"),"catalogScopePresent":"platform.catalog" in (ci(cl,"AllowedScopes") or []),"linkedProductMatches":len(p)})
            if len(p)==1:
                info["linkedProductKey"]=ci(p[0],"Key")
                info["activeOwnerTenantMatches"]=sum(1 for t in docs["tenant"] if ci(t,"Id")==ci(p[0],"TenantId") and ci(t,"IsActive") is True)
except Exception as e: storage["failureType"]=type(e).__name__
out["storagePrerequisites"]=storage

print(json.dumps(out,ensure_ascii=False,indent=2))
PY
