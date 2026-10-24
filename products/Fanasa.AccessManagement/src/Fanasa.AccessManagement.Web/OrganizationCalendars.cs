using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Persistence;
using Microsoft.Data.Sqlite;

namespace Fanasa.AccessManagement.Web.Organization;

public sealed record OrganizationMinuteWindow(int StartMinute, int EndMinute);
public sealed record OrganizationWeeklyWindow(int DayOfWeek, int StartMinute, int EndMinute);
public sealed record OrganizationDateException(DateOnly Date, string Kind, OrganizationMinuteWindow[] Windows, string Reason);
public sealed record OrganizationCalendar(Guid Id, string Code, string Name, string Kind, string TimeZoneId,
    DateOnly CoverageFrom, DateOnly CoverageThrough, OrganizationWeeklyWindow[] WeeklyWindows,
    OrganizationDateException[] Exceptions, bool Active, string Source);
public sealed record OrganizationCalendarState(Guid TenantId, long Revision, OrganizationCalendar[] Calendars);
public sealed record OrganizationCalendarCommand(long ExpectedRevision, Guid? Id, string Code, string Name, string Kind,
    string TimeZoneId, DateOnly CoverageFrom, DateOnly CoverageThrough, OrganizationWeeklyWindow[] WeeklyWindows,
    OrganizationDateException[] Exceptions, bool Active, string Source, string Reason);
public sealed record OrganizationCalendarEvaluation(bool Known, bool Allowed, string Reason, Guid CalendarId,
    long Revision, string? TimeZoneId, DateTimeOffset? LocalTime, bool IsWorkingDay);

/// <summary>
/// Tenant-owned civil-time definitions. Endpoints authorize their callers; this store enforces tenant existence,
/// strict validation, atomic compare-and-swap/history/audit and fail-closed evaluation of current persisted data.
/// A date exception replaces that civil date, including an overnight interval inherited from its predecessor.
/// </summary>
public sealed class OrganizationCalendarStore(FabricDatabase database, PersistentAccessManagement access, TimeProvider? timeProvider = null)
{
    public const string DocumentKind = "organization.calendar.v1";
    private const string HistoryKind = "organization.calendar.history.v1";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public OrganizationCalendarState Read(Guid tenant)
    {
        RequireTenant(tenant);
        var state = database.Read<OrganizationCalendarState>(DocumentKind, tenant.ToString());
        if (state is null) return new(tenant, 0, []);
        ValidatePersisted(state, tenant);
        return state;
    }

    public OrganizationCalendarState History(Guid tenant, long revision)
    {
        var current = Read(tenant);
        if (revision < 1 || revision > current.Revision) throw new KeyNotFoundException("Calendar revision not found.");
        var state = database.Read<OrganizationCalendarState>(HistoryKind, $"{tenant}:{revision}")
            ?? throw new KeyNotFoundException("Calendar revision not found.");
        ValidatePersisted(state, tenant);
        if (state.Revision != revision) throw new InvalidDataException("Invalid calendar history binding.");
        return state;
    }

    public OrganizationCalendarState Save(Guid tenant, OrganizationCalendarCommand command, string actor)
    {
        ArgumentNullException.ThrowIfNull(command);
        Label(actor, 200); Label(command.Reason, 1000);
        if (command.ExpectedRevision < 0 || command.Id == Guid.Empty) throw new ArgumentException("Invalid calendar revision or identifier.");
        var value = new OrganizationCalendar(command.Id ?? Guid.NewGuid(), command.Code, command.Name, command.Kind,
            command.TimeZoneId, command.CoverageFrom, command.CoverageThrough, command.WeeklyWindows,
            command.Exceptions, command.Active, command.Source);
        ValidateCalendar(value);
        // Retain a private copy so a caller cannot mutate arrays while a transaction is committing.
        value = value with
        {
            Code = value.Code.Trim(), Name = value.Name.Trim(), Source = value.Source.Trim(),
            WeeklyWindows = value.WeeklyWindows.Select(x => x with { }).ToArray(),
            Exceptions = value.Exceptions.Select(x => x with
            { Windows = x.Windows.Select(w => w with { }).ToArray(), Reason = x.Reason.Trim() }).ToArray()
        };
        return database.Transaction(() =>
        {
            var state = Read(tenant);
            if (state.Revision != command.ExpectedRevision) throw new InvalidOperationException("تقویم تغییر کرده است؛ دوباره بارگذاری کنید.");
            if (command.Id.HasValue && !state.Calendars.Any(x => x.Id == command.Id.Value)) throw new KeyNotFoundException("Calendar not found in this tenant.");
            if (state.Calendars.Any(x => x.Id != value.Id && string.Equals(x.Code, value.Code, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("کد تقویم در این سازمان تکراری است.");
            if (!command.Id.HasValue && state.Calendars.Length >= 100) throw new InvalidOperationException("At most 100 calendars per tenant are supported.");
            var next = new OrganizationCalendarState(tenant, checked(state.Revision + 1),
                [..state.Calendars.Where(x => x.Id != value.Id), value]);
            var historyKey = $"{tenant}:{next.Revision}";
            if (database.ReadDocument(HistoryKind, historyKey) is not null) throw new InvalidDataException("Calendar history must be immutable.");
            database.Put(DocumentKind, tenant.ToString(), next);
            database.Put(HistoryKind, historyKey, next);
            database.Emit($"organization-calendar:{historyKey}", "OrganizationCalendarChanged",
                new { TenantId = tenant, next.Revision, CalendarId = value.Id, value.Code, value.Active, Actor = actor.Trim(), Reason = command.Reason.Trim() },
                clock.GetUtcNow().ToUniversalTime());
            return next;
        });
    }

    public OrganizationCalendarEvaluation Evaluate(Guid tenant, Guid calendarId, DateTimeOffset instant, string mode = "working-time")
    {
        try
        {
            return database.IsInTransaction ? EvaluateCore(tenant, calendarId, instant, mode)
                : database.ReadSnapshot(() => EvaluateCore(tenant, calendarId, instant, mode));
        }
        catch (SqliteException)
        { return new(false, false, "calendar-storage-unavailable", calendarId, 0, null, null, false); }
    }

    private OrganizationCalendarEvaluation EvaluateCore(Guid tenant, Guid calendarId, DateTimeOffset instant, string mode)
    {
        long revision = 0;
        try
        {
            var state = Read(tenant); revision = state.Revision;
            var calendar = state.Calendars.SingleOrDefault(x => x.Id == calendarId);
            if (calendar is null) return Unknown("calendar-missing");
            if (!calendar.Active) return Unknown("calendar-inactive", calendar.TimeZoneId);
            if (mode is not ("working-time" or "non-holiday") || mode == "non-holiday" && calendar.Kind != "work")
                return Unknown("calendar-mode-invalid", calendar.TimeZoneId);
            var zone = ResolveTimeZone(calendar.TimeZoneId);
            // Conversion from an absolute instant handles both sides of a repeated DST hour without guessing an offset.
            var local = TimeZoneInfo.ConvertTime(instant, zone);
            var date = DateOnly.FromDateTime(local.DateTime);
            if (date < calendar.CoverageFrom || date > calendar.CoverageThrough)
                return Unknown("calendar-coverage-unknown", calendar.TimeZoneId, local);
            var exception = calendar.Exceptions.SingleOrDefault(x => x.Date == date);
            if (exception?.Kind == "closed") return Known(false, "calendar-date-closed", false);
            var windows = WindowsFor(calendar, date);
            var workingDay = windows.Length > 0;
            if (mode == "non-holiday") return Known(workingDay, workingDay ? "calendar-working-day" : "calendar-non-working-day", workingDay);
            var minute = local.TimeOfDay.TotalMinutes;
            var openToday = windows.Any(w => minute >= w.StartMinute && (w.EndMinute > w.StartMinute ? minute < w.EndMinute : true));
            var fromYesterday = false;
            // An explicit date override replaces that whole date. Coverage does not imply any preceding unknown date.
            if (exception is null && date > calendar.CoverageFrom)
                fromYesterday = WindowsFor(calendar, date.AddDays(-1)).Any(w => w.EndMinute < w.StartMinute && minute < w.EndMinute);
            return Known(openToday || fromYesterday,
                openToday ? "calendar-within-window" : fromYesterday ? "calendar-overnight-window" : "calendar-outside-window", workingDay);

            OrganizationCalendarEvaluation Known(bool allowed, string reason, bool isWorkingDay) =>
                new(true, allowed, reason, calendarId, revision, calendar.TimeZoneId, local, isWorkingDay);
        }
        catch (KeyNotFoundException) { return Unknown("calendar-tenant-unavailable"); }
        catch (SqliteException) { return Unknown("calendar-storage-unavailable"); }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidTimeZoneException or TimeZoneNotFoundException or OverflowException)
        { return Unknown("calendar-invalid"); }

        OrganizationCalendarEvaluation Unknown(string reason, string? zone = null, DateTimeOffset? local = null) =>
            new(false, false, reason, calendarId, revision, zone, local, false);
    }

    private void RequireTenant(Guid tenant)
    {
        if (tenant == Guid.Empty || access.GetTenants().Count(x => x.Id == tenant && x.IsActive) != 1)
            throw new KeyNotFoundException("Active tenant not found.");
    }

    private static OrganizationMinuteWindow[] WindowsFor(OrganizationCalendar calendar, DateOnly date)
    {
        var exception = calendar.Exceptions.SingleOrDefault(x => x.Date == date);
        return exception is not null ? exception.Windows : calendar.WeeklyWindows.Where(x => x.DayOfWeek == (int)date.DayOfWeek)
            .Select(x => new OrganizationMinuteWindow(x.StartMinute, x.EndMinute)).ToArray();
    }

    private static void ValidatePersisted(OrganizationCalendarState state, Guid tenant)
    {
        try
        {
            if (state.TenantId != tenant || state.Revision < 1 || state.Calendars is null || state.Calendars.Length > 100
                || state.Calendars.Any(x => x is null) || state.Calendars.Select(x => x.Id).Distinct().Count() != state.Calendars.Length
                || state.Calendars.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Calendars.Length)
                throw new ArgumentException("Invalid calendar state binding.");
            foreach (var calendar in state.Calendars) ValidateCalendar(calendar);
        }
        catch (Exception error) when (error is ArgumentException or InvalidTimeZoneException or TimeZoneNotFoundException)
        { throw new InvalidDataException("Invalid persisted organization calendar.", error); }
    }

    private static void ValidateCalendar(OrganizationCalendar calendar)
    {
        Label(calendar.Code, 80); Label(calendar.Name, 200); Label(calendar.Source, 500);
        if (calendar.Id == Guid.Empty || calendar.Kind is not ("work" or "data")) throw new ArgumentException("Invalid calendar type or identifier.");
        _ = ResolveTimeZone(calendar.TimeZoneId);
        if (calendar.CoverageFrom > calendar.CoverageThrough || calendar.CoverageThrough.DayNumber - calendar.CoverageFrom.DayNumber >= 3660)
            throw new ArgumentException("Calendar coverage must be between 1 and 3660 inclusive dates.");
        if (calendar.WeeklyWindows is null || calendar.Exceptions is null || calendar.WeeklyWindows.Length > 56 || calendar.Exceptions.Length > 2000)
            throw new ArgumentException("Calendar windows or exception count exceeds its limit.");
        var weekly = new List<(int Start, int End)>();
        foreach (var window in calendar.WeeklyWindows)
        {
            if (window is null || window.DayOfWeek is < 0 or > 6) throw new ArgumentException("Invalid weekday; use Sunday=0 through Saturday=6.");
            ValidateWindow(window.StartMinute, window.EndMinute);
            var start = window.DayOfWeek * 1440 + window.StartMinute;
            var end = window.DayOfWeek * 1440 + window.EndMinute + (window.EndMinute < window.StartMinute ? 1440 : 0);
            weekly.Add((start, Math.Min(end, 10080)));
            if (end > 10080) weekly.Add((0, end - 10080));
        }
        RejectOverlap(weekly);
        if (calendar.Exceptions.Any(x => x is null) || calendar.Exceptions.Select(x => x.Date).Distinct().Count() != calendar.Exceptions.Length)
            throw new ArgumentException("Only one exception per date is supported.");
        foreach (var exception in calendar.Exceptions)
        {
            Label(exception.Reason, 1000);
            if (exception.Date < calendar.CoverageFrom || exception.Date > calendar.CoverageThrough || exception.Kind is not ("closed" or "open")
                || exception.Windows is null || exception.Windows.Length > 16 || exception.Kind == "closed" && exception.Windows.Length != 0
                || exception.Kind == "open" && exception.Windows.Length == 0)
                throw new ArgumentException("Date exceptions must be covered and explicitly closed or open with windows.");
            var intervals = new List<(int Start, int End)>();
            foreach (var window in exception.Windows)
            {
                if (window is null) throw new ArgumentException("A minute window is required.");
                ValidateWindow(window.StartMinute, window.EndMinute);
                intervals.Add((window.StartMinute, window.EndMinute + (window.EndMinute < window.StartMinute ? 1440 : 0)));
            }
            RejectOverlap(intervals);
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        Label(id, 100);
        if (id != id!.Trim() || id != "UTC" && !TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
            throw new ArgumentException("Use an installed IANA time zone such as Asia/Tehran.");
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }

    private static void ValidateWindow(int start, int end)
    {
        if (start is < 0 or >= 1440 || end is < 0 or > 1440 || start == end)
            throw new ArgumentException("Minute windows require start 0..1439, end 0..1440 and distinct boundaries; end before start means overnight.");
    }

    private static void RejectOverlap(List<(int Start, int End)> intervals)
    {
        var sorted = intervals.OrderBy(x => x.Start).ToArray();
        for (var i = 1; i < sorted.Length; i++)
            if (sorted[i].Start < sorted[i - 1].End) throw new ArgumentException("Calendar windows must not overlap; adjacent boundaries are allowed.");
    }

    private static void Label(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException("A nonempty calendar label within its length limit is required.");
    }
}
