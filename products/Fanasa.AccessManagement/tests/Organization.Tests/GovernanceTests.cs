using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

static class GovernanceTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        void Reject<T>(Action action, string label) where T : Exception { try { action(); } catch (T) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }
        var root=Path.Combine(directory,"governance"); Directory.CreateDirectory(root);
        var settings=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Platform:RegistryPath"]=Path.Combine(root,"registry.db") }).Build();
        var catalog=new InMemoryAccessManagement(new PlatformRegistry(settings));
        catalog.RegisterProduct(new("organization-fabric","Organization","org",null,"fanasa.ayin",null));
        using var db=new FabricDatabase(Path.Combine(root,"fabric.db")); var access=new PersistentAccessManagement(db,catalog); var tenant=access.GetTenants().Single().Id;
        var other=access.CreateTenant("gov-other","Other").Id;
        var plan=access.RegisterPlan(new("organization-fabric","gov","Governance","payg","IRR",0,0));
        access.Subscribe(new(tenant,"organization-fabric",plan.Id,20));
        foreach(var actor in new[]{"requester","reviewer","author","publisher"}) access.AddUser(new(tenant,actor,actor));
        foreach(var actor in new[]{"requester","reviewer"}) foreach(var permission in new[]{"tenancy.read","organization.read"}) access.SetGrant(new(tenant,actor,permission,null),"admin","fixture");
        foreach(var permission in new[]{"tenancy.manage","organization.write"}) access.SetGrant(new(tenant,"reviewer",permission,null),"admin","fixture");
        var chart=new OrganizationStore(root,access,true,db); var now=DateTimeOffset.UtcNow; var clock=new GovernanceClock(now);
        OrgCommand Command(string operation,string code,Guid? unit=null,Guid? role=null)=>new(chart.Read(tenant).Revision,operation,null,code,code,null,"team",unit,2,null,null,null,null,"fixture",role);
        var unit=chart.Execute(tenant,Command("unit.save","UNIT"),"admin").Units.Single().Id;
        var roleA=chart.Execute(tenant,Command("role.save","ROLE-A"),"admin").Roles!.Single().Id;
        var roleB=chart.Execute(tenant,Command("role.save","ROLE-B"),"admin").Roles!.Single(x=>x.Code=="ROLE-B").Id;
        var positionA=chart.Execute(tenant,Command("position.save","POS-A",unit,roleA),"admin").Positions.Single().Id;
        var positionB=chart.Execute(tenant,Command("position.save","POS-B",unit,roleB),"admin").Positions.Single(x=>x.Code=="POS-B").Id;
        var policies=new AccessPolicyStore(db,access);
        PatternCommand Pattern(string permission,Guid? role,string effect="Permit",string template="role",DateTimeOffset? end=null)=>new(policies.Read(tenant).Revision,"Pattern",template,permission,effect,role,null,null,end,null,null,"fixture");
        var drafted=policies.Draft(tenant,Pattern("billing.read",roleA),"author"); var permit=drafted.Patterns.Single().Id;
        Reject<InvalidOperationException>(()=>policies.Transition(tenant,permit,drafted.Revision,"publish","author","self"),"PAP requires independent publisher");
        policies.Transition(tenant,permit,drafted.Revision,"publish","publisher","reviewed");
        Reject<InvalidOperationException>(()=>policies.Draft(tenant,Pattern("billing.read",roleA) with { ExpectedRevision=0 },"author"),"PAP rejects stale policy revisions");
        Reject<ArgumentException>(()=>policies.Draft(tenant,Pattern("platform.admin",roleA),"author"),"PAP cannot define platform administrator grants");
        Reject<ArgumentException>(()=>policies.Draft(tenant,Pattern("billing.read",null,template:"member-window"),"author"),"time pattern requires an explicit window");
        Reject<ArgumentException>(()=>policies.Draft(other,Pattern("billing.read",roleA) with { ExpectedRevision=0 },"author"),"PAP rejects cross-tenant role references");
        var lifecycle=new IdentityLifecycle(db,access,chart,clock);
        LifecycleCommand Join(string who,DateTimeOffset at)=>new(Guid.NewGuid(),chart.Read(tenant).Revision,"join",who,who,positionA,null,at,null,"join fixture");
        var command=Join("worker",now.AddMinutes(1)); var request=lifecycle.Submit(tenant,command,"requester");
        check(!access.GetUsers(tenant).Any(x=>x.KeycloakSubject=="worker"),"pending join cannot create membership or access");
        foreach(var permission in new[]{"tenancy.manage","organization.write"})
        {
            var preview=policies.Draft(tenant,Pattern(permission,null,template:"member-window",end:now.AddHours(1)),"author");
            policies.Transition(tenant,preview.Patterns.Last().Id,preview.Revision,"publish","publisher","simulation only");
        }
        check(policies.Evaluate(tenant,"author","tenancy.manage",now).Decision=="Permit","published management pattern can be simulated independently");
        Reject<UnauthorizedAccessException>(()=>lifecycle.Review(tenant,request.Id,1,true,"author","simulated privilege"),"simulated policy Permit cannot grant workflow review authority before activation");
        Reject<UnauthorizedAccessException>(()=>lifecycle.Review(tenant,request.Id,1,true,"requester","self"),"requester cannot review without management authority");
        foreach(var permission in new[]{"tenancy.manage","organization.write"}) access.SetGrant(new(tenant,"requester",permission,null),"admin","fixture");
        Reject<InvalidOperationException>(()=>lifecycle.Review(tenant,request.Id,1,true,"requester","self"),"JML prevents self-approval even with management rights");
        request=lifecycle.Review(tenant,request.Id,1,true,"reviewer","independent");
        Reject<InvalidOperationException>(()=>lifecycle.Apply(tenant,request.Id,"reviewer"),"approved future join cannot execute early");
        clock.Now=now.AddMinutes(2); var applied=lifecycle.Apply(tenant,request.Id,"reviewer");
        check(applied.Status=="executed"&&access.GetUsers(tenant).Single(x=>x.KeycloakSubject=="worker").IsActive,"approved join atomically creates membership and appointment");
        var revision=chart.Read(tenant).Revision; var eventCount=db.Outbox().Length;
        check(lifecycle.Apply(tenant,request.Id,"reviewer").Version==applied.Version&&chart.Read(tenant).Revision==revision&&db.Outbox().Length==eventCount,"JML apply replay cannot repeat mutations or events");
        check(lifecycle.Submit(tenant,command,"requester").Id==request.Id,"JML submission replay returns original receipt after time advances");
        Reject<InvalidOperationException>(()=>lifecycle.Submit(tenant,command with { Reason="changed" },"requester"),"JML idempotency key is actor and payload bound");
        var decision=policies.Evaluate(tenant,"worker","billing.read",clock.Now);
        check(decision.Decision=="Permit"&&decision.Policies.SequenceEqual(new[]{permit})&&decision.Obligations.SequenceEqual(new[]{"audit"}),"PIP derives active appointment role and PDP returns audit obligation");
        check(policies.Evaluate(tenant,"worker","billing.read",now).Decision=="NotApplicable","role access is inactive before appointment start");
        check(policies.Evaluate(other,"worker","billing.read",clock.Now).Decision=="Deny","PDP never reuses membership across tenants");
        check(policies.Evaluate(tenant,"worker","unknown",clock.Now).Decision=="NotApplicable","PDP does not permit unsupported actions");
        var beforeAudit=db.Outbox().Length; policies.Authorize(tenant,"worker","billing.read",clock.Now);
        check(db.Outbox().Length==beforeAudit+1&&db.Outbox().Last().Kind=="AccessDecisionApplied","authorization fulfills mandatory audit obligation before returning Permit");
        var denyDraft=policies.Draft(tenant,Pattern("billing.read",null,"Deny","member-window",clock.Now.AddMinutes(10)),"author"); var denyId=denyDraft.Patterns.Last().Id;
        policies.Transition(tenant,denyId,denyDraft.Revision,"publish","publisher","deny reviewed");
        access.SetGrant(new(tenant,"worker","billing.read",null),"admin","fixture");
        check(policies.Evaluate(tenant,"worker","billing.read",clock.Now).Decision=="Deny","deny-overrides beats role permit and direct grant");
        check(policies.Evaluate(tenant,"worker","billing.read",clock.Now.AddMinutes(10)).Decision=="Permit","validity window excludes exact end");
        policies.Transition(tenant,denyId,policies.Read(tenant).Revision,"retire","publisher","done");
        var savedChart=chart.Read(tenant);db.Transaction(()=>{db.Put("organization",tenant.ToString(),savedChart with { Roles=[] });return true;});
        check(policies.Evaluate(tenant,"worker","billing.read",clock.Now).Decision=="Permit","deny-overrides keeps valid direct Permit when only permit-side attribute is missing");
        db.Transaction(()=>{db.Put("organization",tenant.ToString(),savedChart);return true;});
        var missingDeny=policies.Draft(tenant,Pattern("billing.read",roleB,"Deny"),"author");var missingDenyId=missingDeny.Patterns.Last().Id;
        policies.Transition(tenant,missingDenyId,missingDeny.Revision,"publish","publisher","reviewed");
        db.Transaction(()=>{db.Put("organization",tenant.ToString(),savedChart with { Roles=[new(roleA,"A","A")] });return true;});
        check(policies.Evaluate(tenant,"worker","billing.read",clock.Now).Decision=="Indeterminate","missing deny-side reference cannot fall through to Permit");
        db.Transaction(()=>{db.Put("organization",tenant.ToString(),savedChart);return true;});
        policies.Transition(tenant,missingDenyId,policies.Read(tenant).Revision,"retire","publisher","done");
        var overnight=policies.Draft(tenant,Pattern("billing.meter",null,template:"member-window") with { UtcStartHour=22,UtcEndHour=6 },"author");
        policies.Transition(tenant,overnight.Patterns.Last().Id,overnight.Revision,"publish","publisher","overnight");
        var midnight=new DateTimeOffset(2026,10,6,23,0,0,TimeSpan.Zero);
        check(policies.Evaluate(tenant,"worker","billing.meter",midnight).Decision=="Permit"&&policies.Evaluate(tenant,"worker","billing.meter",midnight.AddHours(7)).Decision=="NotApplicable","environment window handles overnight UTC interval and exclusive end");
        var appointment=chart.Read(tenant).Appointments.Single(x=>x.Subject=="worker");
        var move=new LifecycleCommand(Guid.NewGuid(),revision,"move","worker",null,positionB,appointment.Id,clock.Now.AddMinutes(1),null,"move fixture");
        var mover=lifecycle.Submit(tenant,move,"requester");lifecycle.Review(tenant,mover.Id,1,true,"reviewer","independent");clock.Now=clock.Now.AddMinutes(2);
        lifecycle.Apply(tenant,mover.Id,"reviewer");
        check(chart.Read(tenant).Appointments.Single(x=>x.Id==appointment.Id).To==move.EffectiveAt&&chart.Read(tenant).Appointments.Any(x=>x.Subject=="worker"&&x.PositionId==positionB&&x.From==move.EffectiveAt),"approved mover ends old appointment and starts new one atomically");
        check(policies.Evaluate(tenant,"worker","billing.read",clock.Now).Decision=="NotApplicable"&&access.GetGrants("worker").All(x=>x.ExpiresAt.HasValue),"mover removes stale direct grants and old role-derived access");
        var failedJoin=lifecycle.Submit(tenant,Join("stale-worker",clock.Now),"requester");lifecycle.Review(tenant,failedJoin.Id,1,true,"reviewer","reviewed");
        chart.Execute(tenant,Command("role.save","EXTRA"),"admin");lifecycle.ApplyDue();
        check(lifecycle.Read(tenant,failedJoin.Id).Status=="failed"&&!access.GetUsers(tenant).Any(x=>x.KeycloakSubject=="stale-worker"),"scheduler fails stale chart request without partial membership");
        var canceled=lifecycle.Submit(tenant,Join("cancel-worker",clock.Now),"requester");lifecycle.Cancel(tenant,canceled.Id,1,"requester","cancel");
        check(lifecycle.Read(tenant,canceled.Id).Status=="cancelled","requester can cancel pending request");
        var revoked=lifecycle.Submit(tenant,Join("revoked-worker",clock.Now),"requester");lifecycle.Review(tenant,revoked.Id,1,true,"reviewer","reviewed");
        access.SetGrant(new(tenant,"reviewer","tenancy.manage",now.AddMinutes(-1)),"admin","revoke");lifecycle.ApplyDue();
        check(lifecycle.Read(tenant,revoked.Id).Status=="failed"&&!access.GetUsers(tenant).Any(x=>x.KeycloakSubject=="revoked-worker"),"scheduled execution rechecks reviewer authority");
        access.SetGrant(new(tenant,"reviewer","tenancy.manage",null),"admin","restore");
        var position=chart.Read(tenant).Positions.Single(x=>x.Id==positionA);chart.Execute(tenant,Command("position.save","POS-A",unit,roleA) with { Id=position.Id,Capacity=1 },"admin");
        access.AddUser(new(tenant,"occupant","Occupant"));chart.Execute(tenant,new(chart.Read(tenant).Revision,"appointment.add",null,null,null,null,null,null,1,positionA,"occupant",clock.Now,null,"fixture"),"admin");
        var full=lifecycle.Submit(tenant,Join("full-worker",clock.Now),"requester");lifecycle.Review(tenant,full.Id,1,true,"reviewer","reviewed");
        Reject<ArgumentException>(()=>lifecycle.Apply(tenant,full.Id,"reviewer"),"full position rejects lifecycle execution");
        check(!access.GetUsers(tenant).Any(x=>x.KeycloakSubject=="full-worker")&&lifecycle.Read(tenant,full.Id).Status=="approved","capacity failure rolls back membership and request state");
        var subscription=access.GetSubscriptions(tenant).Single();
        db.Transaction(()=>{db.Put("subscription",subscription.Id.ToString(),subscription with { SeatLimit=1 });return true;});
        var leave=new LifecycleCommand(Guid.NewGuid(),chart.Read(tenant).Revision,"leave","worker",null,null,null,clock.Now,null,"leave fixture");
        var leaver=lifecycle.Submit(tenant,leave,"requester");lifecycle.Review(tenant,leaver.Id,1,true,"reviewer","reviewed");lifecycle.Apply(tenant,leaver.Id,"reviewer");
        check(!access.GetUsers(tenant).Single(x=>x.KeycloakSubject=="worker").IsActive&&policies.Evaluate(tenant,"worker","billing.meter",midnight).Decision=="Deny","approved leaver closes membership and denies published policy access");
        var restarted=new IdentityLifecycle(db,new PersistentAccessManagement(db,catalog),chart,clock);
        check(restarted.Read(tenant,leaver.Id).Status=="executed"&&db.TenantDocuments<LifecycleRequest>("lifecycle.history",other).Length==0,"lifecycle state and tenant-isolated history persist across restart");
        var principal=new ClaimsPrincipal(new ClaimsIdentity([new("sub","requester"),new("tenant_permission",$"{tenant}:organization.read"),new("tenant_permission",$"{tenant}:tenancy.read")],"test"));
        var api=new GovernanceApi(policies,lifecycle,access,chart,db){ControllerContext=new ControllerContext{HttpContext=new DefaultHttpContext{User=principal}}};
        check(api.Draft(tenant,Pattern("billing.read",roleA)) is ForbidResult,"tenant reader cannot author granting policy");
        check(api.Read(other) is ForbidResult&&api.Simulate(tenant,new("reviewer","billing.read",null)) is ForbidResult,"governance API forbids cross-tenant reads and simulating another subject");
        check(api.TransitionRequest(tenant,full.Id,new(full.Version,"apply","execute")) is BadRequestObjectResult,"governance authoring API cannot execute membership changes before activation");
        access.SetSubscription(tenant,subscription.Id,"expired",null,"admin","expired fixture");
        var expiredView=(OkObjectResult)api.Read(tenant);var hiddenChart=(OrgState)expiredView.Value!.GetType().GetProperty("chart")!.GetValue(expiredView.Value)!;
        check(hiddenChart.Units.Length==0&&hiddenChart.Positions.Length==0,"expired SaaS governance view hides subscribed chart but permits offboarding navigation");
        var expiredLeave=lifecycle.Submit(tenant,leave with { RequestId=Guid.NewGuid(),ExpectedRevision=hiddenChart.Revision,Subject="occupant" },"requester");
        lifecycle.Review(tenant,expiredLeave.Id,1,true,"reviewer","expired offboarding");lifecycle.Apply(tenant,expiredLeave.Id,"reviewer");
        check(!access.GetUsers(tenant).Single(x=>x.KeycloakSubject=="occupant").IsActive,"offboarding remains possible after subscription expiry and exceeded seat quota");
    }
}
sealed class GovernanceClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
