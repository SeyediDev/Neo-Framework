using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Security;

namespace Fanasa.AccessManagement.Web.Organization;

public sealed record OrgUnit(Guid Id, string Name, string Code, Guid? ParentId, string Kind, bool Active);
public sealed record OrgRole(Guid Id, string Name, string Code);
public sealed record OrgPosition(Guid Id, Guid UnitId, string Name, string Code, int Capacity, bool Active, Guid? RoleId = null);
public sealed record OrgAppointment(Guid Id, Guid PositionId, string Subject, DateTimeOffset From, DateTimeOffset? To);
public sealed record OrgChange(Guid Id, long Revision, string Actor, string Reason, DateTimeOffset RecordedAt, string Operation);
public sealed record OrgState(Guid TenantId, long Revision, OrgUnit[] Units, OrgPosition[] Positions, OrgAppointment[] Appointments, OrgChange[] Changes, OrgRole[]? Roles = null);
public sealed record OrgCommand(long ExpectedRevision, string Operation, Guid? Id, string? Name, string? Code, Guid? ParentId, string? Kind, Guid? UnitId, int Capacity, Guid? PositionId, string? Subject, DateTimeOffset? From, DateTimeOffset? To, string Reason, Guid? RoleId = null, string? RequestId = null);
public sealed record OrgReceipt(string Actor, string Fingerprint, long Revision);

public static class OrgAuthorization
{
    public static bool Allows(ClaimsPrincipal user, Guid tenant, bool write) => TenantAuthorization.Allows(user, tenant, write ? "organization.write" : "organization.read");
    public static string Subject(ClaimsPrincipal user) => user.FindFirst("sub")?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new UnauthorizedAccessException();
}

// Single-host durable adapter. Database adapter/outbox is required before horizontal scaling.
public sealed class OrganizationStore
{
    private readonly string directory;
    private readonly object gate = new();
    private readonly IAccessManagement access;
    private readonly bool requireSubscription;
    private readonly FabricDatabase? database;
    private readonly AccountingStore? accounting;
    public OrganizationStore(string directory, IAccessManagement access, bool requireSubscription = false, FabricDatabase? database = null, AccountingStore? accounting = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.access = access;
        this.requireSubscription = requireSubscription;
        this.database = database; this.accounting = accounting;
        if (accounting is not null && (database is null || !ReferenceEquals(database, accounting.Database))) throw new ArgumentException("Organization metering requires the shared transactional database.");
        Directory.CreateDirectory(this.directory);
    }
    public OrgState Read(Guid tenant)
    {
        lock (gate)
        {
            RequireTenant(tenant);
            if (database is not null) return database.Read<OrgState>("organization", tenant.ToString()) ?? new(tenant, 0, [], [], [], []);
            var path = Path.Combine(directory, tenant + ".json");
            return File.Exists(path) ? JsonSerializer.Deserialize<OrgState>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid organization data.") : new(tenant, 0, [], [], [], []);
        }
    }
    public OrgState Execute(Guid tenant, OrgCommand command, string actor)
        => database is null ? ExecuteCore(tenant, command, actor) : database.Transaction(() => ExecuteCore(tenant, command, actor));
    private OrgState ExecuteCore(Guid tenant, OrgCommand command, string actor)
    {
        lock (gate)
        {
            var state = Read(tenant);
            if (command.RequestId is not null && (string.IsNullOrWhiteSpace(command.RequestId) || command.RequestId.Length > 100)) throw new ArgumentException("شناسه درخواست معتبر نیست.");
            var fingerprint = JsonSerializer.Serialize(command with { ExpectedRevision = 0 });
            if (database is not null && command.RequestId is not null && database.Read<OrgReceipt>("organization.receipt", $"{tenant}:{command.RequestId}") is { } receipt)
            {
                if (receipt.Actor != actor || receipt.Fingerprint != fingerprint) throw new InvalidOperationException("شناسه درخواست تکراری با محتوای متفاوت.");
                return History(tenant, receipt.Revision);
            }
            if (state.Revision != command.ExpectedRevision) throw new InvalidOperationException("ساختار تغییر کرده است؛ دوباره بارگذاری کنید.");
            if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 1000)
                throw new ArgumentException("دلیل تغییر و شناسه عامل الزامی است.");
            var units = state.Units.ToList(); var positions = state.Positions.ToList(); var appointments = state.Appointments.ToList();
            var roles = (state.Roles ?? []).ToList();
            switch (command.Operation)
            {
                case "role.save":
                    ValidateLabel(command.Name, command.Code);
                    var roleId = command.Id ?? Guid.NewGuid();
                    if (command.Id.HasValue && !roles.Any(x => x.Id == roleId)) throw new KeyNotFoundException();
                    if (roles.Any(x => x.Id != roleId && x.Code.Equals(command.Code!.Trim(), StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("کد نقش سازمانی تکراری است.");
                    roles.RemoveAll(x => x.Id == roleId); roles.Add(new(roleId, command.Name!.Trim(), command.Code!.Trim()));
                    break;
                case "unit.save":
                    var unitId = command.Id ?? Guid.NewGuid();
                    if (command.Id.HasValue && !units.Any(x => x.Id == unitId)) throw new KeyNotFoundException();
                    if (command.ParentId.HasValue && !units.Any(x => x.Id == command.ParentId && x.Active)) throw new ArgumentException("واحد والد معتبر نیست.");
                    var parent = command.ParentId;
                    while (parent.HasValue)
                    {
                        if (parent == unitId) throw new ArgumentException("ساختار سازمانی نمی‌تواند چرخه داشته باشد.");
                        parent = units.Single(x => x.Id == parent).ParentId;
                    }
                    ValidateLabel(command.Name, command.Code);
                    if (units.Any(x => x.Id != unitId && x.Code.Equals(command.Code!.Trim(), StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("کد واحد تکراری است.");
                    if (command.Kind is not ("department" or "division" or "team" or "branch")) throw new ArgumentException("نوع واحد نامعتبر است.");
                    var previous = units.Find(x => x.Id == unitId);
                    if (previous is { Active: false }) throw new ArgumentException("واحد بایگانی شده قابل ویرایش نیست.");
                    units.RemoveAll(x => x.Id == unitId);
                    units.Add(new(unitId, command.Name!.Trim(), command.Code!.Trim(), command.ParentId, command.Kind, true));
                    break;
                case "unit.archive":
                    var unit = units.SingleOrDefault(x => x.Id == command.Id && x.Active) ?? throw new KeyNotFoundException();
                    if (units.Any(x => x.ParentId == unit.Id && x.Active) || positions.Any(x => x.UnitId == unit.Id && x.Active)) throw new ArgumentException("ابتدا زیرواحدها و سمت‌های فعال را منتقل یا بایگانی کنید.");
                    units[units.IndexOf(unit)] = unit with { Active = false };
                    break;
                case "position.save":
                    ValidateLabel(command.Name, command.Code);
                    if (!units.Any(x => x.Id == command.UnitId && x.Active) || command.Capacity < 1 || command.Capacity > 10000) throw new ArgumentException("واحد و ظرفیت سمت معتبر نیست.");
                    var positionId = command.Id ?? Guid.NewGuid();
                    if (command.Id.HasValue && !positions.Any(x => x.Id == positionId && x.Active)) throw new KeyNotFoundException();
                    if (positions.Any(x => x.Id != positionId && x.Code.Equals(command.Code!.Trim(), StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("کد سمت تکراری است.");
                    if (command.RoleId.HasValue && !roles.Any(x => x.Id == command.RoleId)) throw new ArgumentException("نقش سازمانی معتبر نیست.");
                    var replacement = new OrgPosition(positionId, command.UnitId!.Value, command.Name!.Trim(), command.Code!.Trim(), command.Capacity, true, command.RoleId);
                    ValidateCapacity(replacement, appointments);
                    positions.RemoveAll(x => x.Id == positionId); positions.Add(replacement);
                    break;
                case "position.archive":
                    var position = positions.SingleOrDefault(x => x.Id == command.Id && x.Active) ?? throw new KeyNotFoundException();
                    if (appointments.Any(x => x.PositionId == position.Id && (!x.To.HasValue || x.To > DateTimeOffset.UtcNow))) throw new ArgumentException("ابتدا انتصاب‌های جاری یا آینده را خاتمه دهید.");
                    positions[positions.IndexOf(position)] = position with { Active = false };
                    break;
                case "appointment.add":
                    var target = positions.SingleOrDefault(x => x.Id == command.PositionId && x.Active) ?? throw new KeyNotFoundException();
                    if (string.IsNullOrWhiteSpace(command.Subject) || !access.GetUsers(tenant).Any(x => x.KeycloakSubject == command.Subject && x.IsActive)) throw new ArgumentException("کاربر باید عضو فعال همین سازمان باشد.");
                    var start = command.From ?? throw new ArgumentException("زمان شروع الزامی است.");
                    if (command.To.HasValue && command.To <= start) throw new ArgumentException("پایان باید پس از شروع باشد.");
                    if (appointments.Any(x => x.PositionId == target.Id && x.Subject == command.Subject && start < (x.To ?? DateTimeOffset.MaxValue) && x.From < (command.To ?? DateTimeOffset.MaxValue))) throw new ArgumentException("انتصاب هم‌پوشان برای این کاربر وجود دارد.");
                    appointments.Add(new(Guid.NewGuid(), target.Id, command.Subject, start, command.To));
                    ValidateCapacity(target, appointments);
                    break;
                case "appointment.transfer":
                    var previousAppointment = appointments.SingleOrDefault(x => x.Id == command.Id && x.Subject == command.Subject) ?? throw new KeyNotFoundException();
                    var transferAt = command.From ?? throw new ArgumentException("زمان انتقال الزامی است.");
                    var nextPosition = positions.SingleOrDefault(x => x.Id == command.PositionId && x.Active) ?? throw new KeyNotFoundException();
                    if (!access.GetUsers(tenant).Any(x => x.KeycloakSubject == command.Subject && x.IsActive) || nextPosition.Id == previousAppointment.PositionId || transferAt <= previousAppointment.From || previousAppointment.To.HasValue && transferAt >= previousAppointment.To || command.To.HasValue && command.To <= transferAt) throw new ArgumentException("انتقال معتبر نیست.");
                    appointments[appointments.IndexOf(previousAppointment)] = previousAppointment with { To = transferAt };
                    if (appointments.Any(x => x.PositionId == nextPosition.Id && x.Subject == command.Subject && transferAt < (x.To ?? DateTimeOffset.MaxValue) && x.From < (command.To ?? DateTimeOffset.MaxValue))) throw new ArgumentException("انتصاب هم‌پوشان وجود دارد.");
                    appointments.Add(new(Guid.NewGuid(), nextPosition.Id, command.Subject!, transferAt, command.To));
                    ValidateCapacity(nextPosition, appointments);
                    break;
                case "appointment.end":
                    var appointment = appointments.SingleOrDefault(x => x.Id == command.Id) ?? throw new KeyNotFoundException();
                    var end = command.To ?? throw new ArgumentException("زمان پایان الزامی است.");
                    if (end <= appointment.From || (appointment.To.HasValue && end > appointment.To)) throw new ArgumentException("زمان خاتمه نامعتبر است.");
                    appointments[appointments.IndexOf(appointment)] = appointment with { To = end };
                    break;
                default: throw new ArgumentException("عملیات ناشناخته است.");
            }
            var change = new OrgChange(Guid.NewGuid(), state.Revision + 1, actor, command.Reason.Trim(), DateTimeOffset.UtcNow, command.Operation);
            var result = new OrgState(tenant, state.Revision + 1, units.ToArray(), positions.ToArray(), appointments.ToArray(), [..state.Changes, change], roles.ToArray());
            if (database is not null)
            {
                if (accounting is not null)
                {
                    var account = accounting.Read(tenant).Account;
                    var charge = new BillingCommand(account.Revision, "usage", $"organization:{tenant}:{result.Revision}", null, null, 0,
                        "organization-fabric", "organization.change", 0, 1, 0, $"organization revision {result.Revision}", null, null);
                    accounting.Execute(tenant, charge, actor);
                }
                database.Put("organization", tenant.ToString(), result);
                database.Put("organization.history", $"{tenant}:{result.Revision}", result);
                if (command.RequestId is not null) database.Put("organization.receipt", $"{tenant}:{command.RequestId}", new OrgReceipt(actor, fingerprint, result.Revision));
                database.Emit($"organization:{tenant}:{result.Revision}", "OrganizationChanged", new { TenantId = tenant, result.Revision, change.Operation, change.Actor }, change.RecordedAt);
                return result;
            }
            var path = Path.Combine(directory, tenant + ".json");
            var temporary = path + "." + Guid.NewGuid() + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, result); stream.Flush(true); }
                // Write history before publishing current revision; uncommitted history is never exposed.
                var history = Path.Combine(directory, tenant + ".history"); Directory.CreateDirectory(history);
                File.Copy(temporary, Path.Combine(history, result.Revision + ".json"), true);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return result;
        }
    }
    public OrgState History(Guid tenant, long revision)
    {
        lock (gate)
        {
            var current = Read(tenant);
            if (revision < 1 || revision > current.Revision) throw new KeyNotFoundException();
            if (database is not null) return database.Read<OrgState>("organization.history", $"{tenant}:{revision}") ?? throw new KeyNotFoundException();
            return JsonSerializer.Deserialize<OrgState>(File.ReadAllText(Path.Combine(directory, tenant + ".history", revision + ".json"))) ?? throw new InvalidDataException();
        }
    }
    private void RequireTenant(Guid tenant)
    {
        if (!access.GetTenants().Any(x => x.Id == tenant && x.IsActive)) throw new KeyNotFoundException("Tenant not found.");
        if (requireSubscription && !access.GetSubscriptions(tenant).Any(x => x.ProductKey == "organization-fabric" && x.Status == "active" && x.StartedAt <= DateTimeOffset.UtcNow && (!x.RenewsAt.HasValue || x.RenewsAt > DateTimeOffset.UtcNow)))
            throw new UnauthorizedAccessException("اشتراک ساختار سازمانی فعال نیست.");
    }
    private static void ValidateLabel(string? name, string? code)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || string.IsNullOrWhiteSpace(code) || code.Length > 80) throw new ArgumentException("نام و کد معتبر الزامی است.");
    }
    private static void ValidateCapacity(OrgPosition position, IEnumerable<OrgAppointment> appointments)
    {
        var events = appointments.Where(x => x.PositionId == position.Id).SelectMany(x => new[] { (At: x.From, Delta: 1), (At: x.To ?? DateTimeOffset.MaxValue, Delta: -1) }).OrderBy(x => x.At).ThenBy(x => x.Delta);
        var count = 0;
        foreach (var e in events) { count += e.Delta; if (count > position.Capacity) throw new ArgumentException("ظرفیت سمت در این بازه تکمیل است."); }
    }
}
