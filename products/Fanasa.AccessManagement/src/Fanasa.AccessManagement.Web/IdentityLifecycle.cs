using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Governance;

public sealed record LifecycleCommand(Guid RequestId, long ExpectedRevision, string Operation, string Subject, string? DisplayName,
    Guid? PositionId, Guid? PreviousAppointmentId, DateTimeOffset EffectiveAt, DateTimeOffset? EndsAt, string Reason);
public sealed record LifecycleRequest(Guid Id, Guid TenantId, LifecycleCommand Command, long Version, string Status, string RequestedBy,
    DateTimeOffset CreatedAt, string? ApprovedBy, string? ReviewReason, DateTimeOffset? AppliedAt, string? Failure);

public sealed class IdentityLifecycle(FabricDatabase database, PersistentAccessManagement access, OrganizationStore organization, TimeProvider clock)
{
    private static string Key(Guid tenant, Guid id) => $"{tenant}:{id}";
    public LifecycleRequest[] List(Guid tenant) => database.TenantDocuments<LifecycleRequest>("lifecycle.request", tenant);
    public LifecycleRequest Read(Guid tenant, Guid id) => database.Read<LifecycleRequest>("lifecycle.request", Key(tenant, id)) ?? throw new KeyNotFoundException();
    private bool HasPermission(Guid tenant, string actor, string permission) => access.GetTenants().Any(x => x.Id == tenant && x.IsActive)
        && access.GetUsers(tenant).Any(x => x.KeycloakSubject == actor && x.IsActive)
        && access.GetGrants(actor).Any(x => x.TenantId == tenant && x.Permission == permission && (!x.ExpiresAt.HasValue || x.ExpiresAt > clock.GetUtcNow()));
    private void Authority(Guid tenant, string actor)
    {
        if (!HasPermission(tenant, actor, "tenancy.manage") || !HasPermission(tenant, actor, "organization.write")) throw new UnauthorizedAccessException("مجوز مدیریت عضویت و نوشتن چارت لازم است.");
    }
    public LifecycleRequest Submit(Guid tenant, LifecycleCommand command, string actor) => database.Transaction(() =>
    {
        AccessPolicyStore.Label(actor, 200); AccessPolicyStore.Label(command.Subject, 200); AccessPolicyStore.Label(command.Reason, 1000);
        if (!HasPermission(tenant, actor, "tenancy.read") || !HasPermission(tenant, actor, "organization.read")) throw new UnauthorizedAccessException();
        if (database.Read<LifecycleRequest>("lifecycle.request", Key(tenant, command.RequestId)) is { } prior)
        {
            if (prior.RequestedBy != actor || JsonSerializer.Serialize(prior.Command) != JsonSerializer.Serialize(command)) throw new InvalidOperationException("شناسه درخواست با محتوای متفاوت استفاده شده است.");
            return prior;
        }
        if (command.RequestId == Guid.Empty || command.Operation is not ("join" or "move" or "leave") || command.EffectiveAt < clock.GetUtcNow().AddMinutes(-5) || command.EndsAt.HasValue && command.EndsAt <= command.EffectiveAt) throw new ArgumentException("درخواست یا زمان‌بندی معتبر نیست.");
        var chart = command.Operation == "leave" ? database.Read<OrgState>("organization", tenant.ToString()) ?? new(tenant, 0, [], [], [], []) : organization.Read(tenant);
        if (chart.Revision != command.ExpectedRevision) throw new InvalidOperationException("نسخه چارت تغییر کرده است.");
        var member = access.GetUsers(tenant).SingleOrDefault(x => x.KeycloakSubject == command.Subject);
        if (command.Operation == "join")
        {
            AccessPolicyStore.Label(command.DisplayName, 200);
            if (member?.IsActive == true) throw new InvalidOperationException("عضو از قبل فعال است.");
            if (command.PreviousAppointmentId.HasValue) throw new ArgumentException("ورود، انتصاب قبلی ندارد.");
        }
        else if (member?.IsActive != true) throw new ArgumentException("عضو فعال همین سازمان لازم است.");
        if (command.Operation is "join" or "move")
        {
            if (!chart.Positions.Any(x => x.Id == command.PositionId && x.Active)) throw new ArgumentException("سمت فعال لازم است.");
            if (command.Operation == "move" && !chart.Appointments.Any(x => x.Id == command.PreviousAppointmentId && x.Subject == command.Subject && x.From < command.EffectiveAt && (!x.To.HasValue || x.To > command.EffectiveAt))) throw new ArgumentException("انتصاب قبلی معتبر نیست.");
        }
        else if (command.PositionId.HasValue || command.PreviousAppointmentId.HasValue || command.EndsAt.HasValue) throw new ArgumentException("خروج نیاز به سمت یا پایان انتصاب جدید ندارد.");
        return Save(new(command.RequestId, tenant, command, 1, "pending", actor, clock.GetUtcNow(), null, null, null, null), actor, "LifecycleRequested");
    });
    public LifecycleRequest Review(Guid tenant, Guid id, long version, bool approve, string actor, string reason) => database.Transaction(() =>
    {
        Authority(tenant, actor); AccessPolicyStore.Label(reason, 1000); var request = Read(tenant, id);
        if (request.Version != version || request.Status != "pending") throw new InvalidOperationException("درخواست تغییر کرده یا بررسی شده است.");
        if (request.RequestedBy == actor || request.Command.Subject == actor) throw new InvalidOperationException("تأیید یا رد درخواست باید توسط شخص مستقلی انجام شود.");
        return Save(request with { Version = version + 1, Status = approve ? "approved" : "rejected", ApprovedBy = approve ? actor : null, ReviewReason = reason.Trim() }, actor, "LifecycleReviewed");
    });
    public LifecycleRequest Cancel(Guid tenant, Guid id, long version, string actor, string reason) => database.Transaction(() =>
    {
        AccessPolicyStore.Label(reason, 1000); var request = Read(tenant, id);
        if (request.RequestedBy != actor) Authority(tenant, actor);
        if (request.Version != version || request.Status is not ("pending" or "approved")) throw new InvalidOperationException("درخواست قابل لغو نیست.");
        return Save(request with { Status = "cancelled", Version = version + 1, ReviewReason = reason.Trim() }, actor, "LifecycleCancelled");
    });
    public LifecycleRequest Apply(Guid tenant, Guid id, string actor) => database.Transaction(() =>
    {
        Authority(tenant, actor); var request = Read(tenant, id);
        if (request.Status == "executed") return request;
        if (request.Status != "approved" || request.ApprovedBy is null || request.Command.EffectiveAt > clock.GetUtcNow()) throw new InvalidOperationException("درخواست تأییدشده و سررسیدشده لازم است.");
        Authority(tenant, request.ApprovedBy); var command = request.Command;
        var member = access.GetUsers(tenant).SingleOrDefault(x => x.KeycloakSubject == command.Subject);
        if (command.Operation == "leave")
        {
            if (member?.IsActive != true) throw new InvalidOperationException("عضویت قبلاً تغییر کرده است.");
            access.SetMembership(tenant, member.Id, false, actor, command.Reason);
        }
        else
        {
            var chart = organization.Read(tenant);
            if (chart.Revision != command.ExpectedRevision) throw new InvalidOperationException("نسخه چارت تغییر کرده؛ درخواست تازه لازم است.");
            if (command.Operation == "join")
            {
                if (member?.IsActive == true) throw new InvalidOperationException("عضویت قبلاً فعال شده است.");
                if (member is null) access.Audited(() => access.AddUser(new(tenant, command.Subject, command.DisplayName)), actor, command.Reason, "TenantMemberAdded");
                else access.SetMembership(tenant, member.Id, true, actor, command.Reason);
            }
            else
            {
                if (member?.IsActive != true) throw new InvalidOperationException("عضو فعال لازم است.");
                // Remove stale direct domain grants. New role-derived access is evaluated from published policies.
                foreach (var grant in access.GetGrants(command.Subject).Where(x => x.TenantId == tenant)) access.SetGrant(grant with { ExpiresAt = clock.GetUtcNow() }, actor, command.Reason);
            }
            organization.Execute(tenant, new(command.ExpectedRevision, command.Operation == "join" ? "appointment.add" : "appointment.transfer", command.PreviousAppointmentId,
                null, null, null, null, null, 1, command.PositionId, command.Subject, command.EffectiveAt, command.EndsAt, command.Reason, RequestId: "lifecycle:" + id), actor);
        }
        return Save(request with { Status = "executed", Version = request.Version + 1, AppliedAt = clock.GetUtcNow() }, actor, "LifecycleApplied");
    });
    private LifecycleRequest Save(LifecycleRequest request, string actor, string kind)
    {
        database.Put("lifecycle.request", Key(request.TenantId, request.Id), request);
        database.Put("lifecycle.history", $"{request.TenantId}:{request.Id}:{request.Version}", request);
        database.Emit("lifecycle:" + Guid.NewGuid(), kind, new { request.TenantId, request.Id, request.Status, Operation = request.Command.Operation, Actor = actor }, clock.GetUtcNow());
        return request;
    }
    public void ApplyDue()
    {
        foreach (var request in database.DueDocuments<LifecycleRequest>("lifecycle.request", clock.GetUtcNow()))
        {
            try { Apply(request.TenantId, request.Id, request.ApprovedBy!); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
            {
                database.Transaction(() =>
                {
                    var current = Read(request.TenantId, request.Id);
                    if (current.Status == "approved") Save(current with { Status = "failed", Version = current.Version + 1, Failure = "authority-or-state-changed" }, "lifecycle-worker", "LifecycleFailed");
                    return true;
                });
            }
        }
    }
}

public sealed class LifecycleWorker(IdentityLifecycle lifecycle, ILogger<LifecycleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { lifecycle.ApplyDue(); }
            catch (Exception error) { logger.LogError(error, "Lifecycle processing failed; approved requests remain recoverable."); }
        }
    }
}
