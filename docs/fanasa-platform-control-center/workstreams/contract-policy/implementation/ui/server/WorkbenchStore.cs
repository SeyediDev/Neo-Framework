using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Fanasa.ContractPolicy;

namespace Fanasa.ContractPolicy.Workbench;

public sealed partial class WorkbenchStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly WorkbenchDatabase _database;
    private long _mutationVersion;
    private readonly ContractPolicyModule _module;
    private readonly ContractAccessProjection _fixture;
    private readonly AccessEvaluationContext _contextFixture;
    private WorkbenchState _state;
    private static readonly string Admission = ContractPolicyEvaluator.AdmissionObligation;
    private static readonly string Resource = ContractPolicyEvaluator.ResourceObligation;

    public static readonly Scenario[] Scenarios =
    [
        new("permit", "شرایط کامل و اجرای مجاز", "هویت، عضویت، قرارداد و اعطای نقش معتبرند؛ اجرای محصول پس از دو الزام انجام می‌شود.", "Permit", "executed", []),
        new("explicit-deny", "منع صریح سیاست", "نسخهٔ سناریو عمل ذخیرهٔ واحد را صریحاً منع می‌کند؛ deny-overrides بر اجازه غالب است.", "Deny", "decision-denied", ["policy.explicitDenyActions"]),
        new("inactive-membership", "عضویت غیرفعال", "داشتن قرارداد و نقش برای عضویت غیرفعال کفایت نمی‌کند.", "Deny", "decision-denied", ["membership.active=false"]),
        new("no-grant", "حق استفاده بدون اعطای نقش", "قرارداد eligibility می‌دهد؛ نقش واقعی باید جدا از مرجع عضویت خوانده شود.", "Deny", "decision-denied", ["grant.active=false"]),
        new("stale-evidence", "شواهد منقضی", "دادهٔ قدیمی نمی‌تواند تصمیم قطعی مجاز تولید کند.", "Indeterminate", "decision-denied", ["evidence.validUntil گذشته"]),
        new("wrong-product", "درخواست محصول دیگر", "این سیاست برای محصول ثبت‌شده است و به هدف دیگری اعمال نمی‌شود.", "NotApplicable", "decision-denied", ["resource.productId=another-product"]),
        new("cross-tenant-resource", "رکورد متعلق به سازمان دیگر", "PDP درخواست را مجاز می‌داند؛ PEP مالک واقعی رکورد را از مخزن محصول بررسی و اجرا را رد می‌کند.", "Permit", "resource-tenant-mismatch", ["مخزن محصول: tenant متفاوت"]),
        new("quota-exhaustion", "سهمیهٔ قراردادی صفر", "PDP اجازهٔ مشروط می‌دهد؛ محدودیت صفر در snapshot سناریو باعث رد پذیرش واقعی می‌شود.", "Permit", "quota-exhausted", ["limits.quota.requests=0 در snapshot سناریو"]),
        new("expired-decision", "تصمیم خارج از مهلت", "تصمیم بومی در ساعت سناریو صادر شده؛ بیش از ۳۰ ثانیه گذشته و PEP اجرای آن را رد می‌کند.", "Permit", "expired-decision", ["environment.now=serverNow-31s"]),
        new("unknown-obligation", "الزام ناشناخته", "در اجرای آزمایشگاه، یک کپی جدا از تصمیم با الزام ناشناخته به guard بومی داده می‌شود؛ خروجی اصلی PDP حفظ می‌شود.", "Permit", "unknown-obligation", ["کپی دست‌کاری‌شده فقط هنگام execute؛ rawDecision بدون تغییر"])
    ];

    public WorkbenchStore(string directory)
    {
        _module = new ContractPolicyModule(Path.Combine(AppContext.BaseDirectory, "Schemas"));
        _fixture = Read<ContractAccessProjection>("Fixtures/integration-projection.v2.json");
        _contextFixture = Read<AccessEvaluationContext>("Fixtures/integration-context.v2.json");
        Directory.CreateDirectory(directory);
        _database = new WorkbenchDatabase(directory);
        if (!_database.Exists)
        {
            var legacy = Path.Combine(Path.GetFullPath(directory), "workbench-state.json");
            var imported = File.Exists(legacy);
            var seed = imported ? JsonSerializer.Deserialize<WorkbenchState>(File.ReadAllText(legacy), Json)
                ?? throw new InvalidDataException("Legacy workbench state is empty.") : Initial();
            seed.TrustedContext ??= TrustedContextFor(seed.ProjectionState.Projection);
            if (imported)
                Audit(seed, "storage-migrated", "سرور میزکار", "انتقال یک‌بارهٔ دادهٔ معتبر به SQLite؛ فایل JSON اصلی حفظ شد",
                    new { schemaVersion = WorkbenchDatabase.SchemaVersion, legacySha256 = HashFile(legacy), sourceFileRetained = true });
            ValidateDefinitions(seed);
            _database.Initialize(seed, imported);
        }
        var snapshot = _database.Read();
        ValidateDefinitions(snapshot.State);
        _state = snapshot.State;
        _mutationVersion = snapshot.MutationVersion;
    }

    private static T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)), Json)!;
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    private static string Time(DateTimeOffset instant) => instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset Instant(string instant) => DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture);
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N");
    private static string Fingerprint(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));

    private WorkbenchState Initial()
    {
        var now = DateTimeOffset.UtcNow;
        var projection = _fixture with { ValidFrom = Time(now.AddDays(-1)), EffectiveAt = Time(now.AddDays(-1)), ValidTo = Time(now.AddDays(30)) };
        var evt = Event(projection, "event-initial");
        var result = _module.Projector.Apply(null, evt, "fanasa-contracts");
        if (result.State is null) throw new InvalidDataException(string.Join(";", result.Errors));
        var p = projection.Policy;
        var state = new WorkbenchState
        {
            ProjectionState = result.State,
            TrustedContext = TrustedContextFor(projection),
            Policies = [new(p.PolicyId, p.PolicyVersion, "سیاست پایهٔ ذخیرهٔ واحد سازمانی", false,
                projection.ZoneConstraints.ToArray(), projection.Limits.Rate.RequestsPerSecond,
                projection.Limits.Rate.BurstCapacity, projection.Limits.Quota.Requests,
                "نویسندهٔ نمونه", "بازبین مستقل نمونه", "Published", Time(now), Time(now), "ورودی مصنوعی نسخهٔ ۲")],
            Tokens = projection.Limits.Rate.BurstCapacity,
            PeriodId = now.ToString("yyyy-MM", CultureInfo.InvariantCulture), LastRefillAt = Time(now)
        };
        state.Resources =
        [
            new("unit-primary", projection.ConsumerTenantId, projection.ProductId, "development", "واحد نمونهٔ سازمان مصرف‌کننده", 0, null),
            new("unit-foreign", "44444444-4444-4444-8444-444444444444", projection.ProductId, "development", "واحد نمونهٔ سازمان دیگر", 0, null)
        ];
        Audit(state, "initialized", "میزکار", "ساخت دادهٔ معتبر در پایگاه داده SQLite مستقل میزکار", new { projection.ContractVersion, p.PolicyVersion, projection.Limits.ConfigRevision });
        return state;
    }

    private static ContractProjectionEvent Event(ContractAccessProjection projection, string eventId) => new()
    {
        SchemaVersion = "2.0.0", EventId = eventId, Producer = "fanasa-contracts", AggregateVersion = projection.ContractVersion,
        OccurredAt = Time(DateTimeOffset.UtcNow), CorrelationId = Id("correlation-"), Projection = projection
    };

    private object View(WorkbenchState state)
    {
        var projection = state.ProjectionState.Projection;
        var storage = Integrity();
        return new
        {
            state.Revision,
            storage, readiness = ReadinessFor(state, storage),
            capabilities = new { mode = "isolated-local-workbench", runtime = "native-dotnet", productionConnected = false, persistent = true,
                allowedPolicyFields = new[] { "explicitDeny", "zoneConstraints", "requestsPerSecond", "burstCapacity", "monthlyQuota" },
                unsupported = new[] { "arbitrary-xacml-xml", "caller-supplied-attribute-graph", "production-admission-counters", "actual-user-authentication" } },
            native = new { assembly = "Fanasa.ContractPolicy.dll", sha256 = HashFile(Path.Combine(AppContext.BaseDirectory, "Fanasa.ContractPolicy.dll")),
                profile = "fanasa.contract-policy/2", engine = "ContractPolicyModule.Evaluator", pep = "ContractPolicyModule.Pep",
                schemas = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Schemas"), "*.json").Order().Select(path => new { name = Path.GetFileName(path), sha256 = HashFile(path) }) },
            state.Policies, state.Drafts, projection, state.ProjectionState, scenarios = Scenarios,
            attributes = new object[]
            {
                new { id="subject", label="هویت و کلاینت", source="دیتابیس منابع معتبر نمونهٔ کارگاه", trust="server-owned synthetic database", fields=new[]{"subjectId","audience","scopes"}, sample=(object)state.TrustedContext!.Subject },
                new { id="membership", label="عضویت سازمانی", source="دیتابیس عضویت نمونهٔ کارگاه", trust="server-owned synthetic database", fields=new[]{"tenantId","active","validFrom","validTo"}, sample=(object)state.TrustedContext!.Membership },
                new { id="grant", label="اعطای نقش", source="دیتابیس اعطای نقش نمونهٔ کارگاه", trust="server-owned synthetic database", fields=new[]{"role","actions","active"}, sample=(object)state.TrustedContext!.Grant },
                new { id="projection", label="snapshot پذیرفته‌شدهٔ قرارداد", source="ContractPolicyModule.Projector", trust="validated trusted-producer fixture", fields=new[]{"contractVersion","policyVersion","configRevision"}, sample=(object)projection }
            },
            usage = UsageFor(state, projection.Limits), state.Operations, state.Resources, state.History
        };
    }
    public object View() { lock (_gate) { RefreshValidatedStorage(); return View(Copy(_state)); } }
    public object History() { lock (_gate) { RefreshValidatedStorage(); return Copy(_state.History); } }
    public object Export() { lock (_gate) { RefreshValidatedStorage(); return new { format = "fanasa-isolated-workbench/2", exportedAt = Time(DateTimeOffset.UtcNow), productionConnected = false, storage = Integrity(), state = Copy(_state), catalog = Scenarios }; } }
    private static string HashFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private void Expected(long expected)
    {
        RefreshValidatedStorage();
        if (expected != _state.Revision) throw new WorkbenchError(409, "revision-conflict", "نسخهٔ مدیریتی تغییر کرده است؛ دادهٔ تازه را بخوانید.", _state.Revision);
    }
    private static string Text(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 500) throw new WorkbenchError(400, "invalid-command", $"{field} باید متن معتبر و حداکثر ۵۰۰ نویسه باشد.");
        return value.Trim();
    }
    private PolicyDocument Policy(string id)
    {
        var policy = _state.Policies.SingleOrDefault(p => p.PolicyId == id);
        if (policy is null) throw new WorkbenchError(404, "unknown-policy", "سیاست یافت نشد.");
        if (policy.Status != "Published") throw new WorkbenchError(409, "retired-policy", "سیاست بازنشسته قابل انتخاب یا اجرا نیست.", _state.Revision);
        return policy;
    }
    private ContractAccessProjection WithPolicy(ContractAccessProjection projection, PolicyDocument policy, bool advanceContract)
    {
        var changedLimits = projection.Limits.Rate.RequestsPerSecond != policy.RequestsPerSecond
            || projection.Limits.Rate.BurstCapacity != policy.BurstCapacity || projection.Limits.Quota.Requests != policy.MonthlyQuota;
        return projection with
        {
            ContractVersion = projection.ContractVersion + (advanceContract ? 1 : 0),
            ZoneConstraints = policy.ZoneConstraints.ToArray(),
            Policy = projection.Policy with { PolicyId = policy.PolicyId, PolicyVersion = policy.PolicyVersion,
                ExplicitDenyActions = policy.ExplicitDeny ? ["organization.unit.save"] : [] },
            Limits = projection.Limits with
            {
                ConfigRevision = projection.Limits.ConfigRevision + (changedLimits ? 1 : 0),
                Rate = projection.Limits.Rate with { RequestsPerSecond = policy.RequestsPerSecond, BurstCapacity = policy.BurstCapacity },
                Quota = projection.Limits.Quota with { Requests = policy.MonthlyQuota }
            }
        };
    }
    public object Draft(DraftCommand command)
    {
        lock (_gate)
        {
            Expected(command.ExpectedRevision);
            var input = command.Policy ?? throw new WorkbenchError(400, "invalid-policy", "مشخصات سیاست لازم است.");
            var issues = PolicyIssues(input);
            if (issues.Count != 0) throw new WorkbenchError(400, "invalid-policy", string.Join("; ", issues.Select(x => x.Message)));
            var author = Text(command.Author, "نویسنده"); var reason = Text(command.Reason, "دلیل");
            var name = Text(input.Name, "نام سیاست");
            var version = _state.Policies.Concat(_state.Drafts).Max(p => p.PolicyVersion) + 1;
            var draft = new PolicyDocument(Id("workbench-policy-"), version, name, input.ExplicitDeny,
                input.ZoneConstraints.ToArray(), input.RequestsPerSecond, input.BurstCapacity, input.MonthlyQuota,
                author, null, "Draft", Time(DateTimeOffset.UtcNow), null, reason, Id("draft-"));
            var errors = _module.Projections.Validate(WithPolicy(_state.ProjectionState.Projection, draft, false));
            if (errors.Count != 0) throw new WorkbenchError(400, "native-validation-failed", string.Join("; ", errors));
            var next = Copy(_state); next.Drafts.Add(draft); next.Revision++;
            Audit(next, "policy-drafted", author, reason, new { draft.DraftId, draft.PolicyId, draft.PolicyVersion, nativeValidated = true });
            Commit(next); return View(_state);
        }
    }
    public object Publish(PublishCommand command)
    {
        lock (_gate)
        {
            Expected(command.ExpectedRevision); var reviewer = Text(command.Reviewer, "بازبین"); var reason = Text(command.Reason, "دلیل");
            var draft = _state.Drafts.SingleOrDefault(p => p.DraftId == command.DraftId)
                ?? throw new WorkbenchError(404, "unknown-draft", "پیش‌نویس یافت نشد.");
            if (string.Equals(reviewer, draft.Author, StringComparison.OrdinalIgnoreCase))
                throw new WorkbenchError(409, "independent-review-required", "برچسب بازبین باید با نویسنده متفاوت باشد؛ این برچسب‌ها احراز هویت واقعی نیستند.", _state.Revision);
            var errors = _module.Projections.Validate(WithPolicy(_state.ProjectionState.Projection, draft, false));
            if (errors.Count != 0) throw new WorkbenchError(400, "native-validation-failed", string.Join("; ", errors));
            var next = Copy(_state); next.Drafts.RemoveAll(p => p.DraftId == draft.DraftId);
            next.Policies.Add(draft with { Status = "Published", Reviewer = reviewer, PublishedAt = Time(DateTimeOffset.UtcNow), Reason = reason });
            next.Revision++; Audit(next, "policy-published", reviewer, reason, new { draft.PolicyId, draft.PolicyVersion, bindingUnchanged = true });
            Commit(next); return View(_state);
        }
    }
    public object Retire(PolicyCommand command)
    {
        lock (_gate)
        {
            Expected(command.ExpectedRevision); var p = Policy(command.PolicyId); var reviewer = Text(command.Reviewer, "بازبین"); var reason = Text(command.Reason, "دلیل");
            if (string.Equals(reviewer, p.Author, StringComparison.OrdinalIgnoreCase)) throw new WorkbenchError(409, "independent-review-required", "بازبین مستقل لازم است.", _state.Revision);
            var next = Copy(_state); next.Policies[next.Policies.FindIndex(x => x.PolicyId == p.PolicyId)] = p with { Status = "Retired" };
            next.Revision++; Audit(next, "policy-retired", reviewer, reason, new { p.PolicyId, projectionBindingRetained = true, executionDisabled = true });
            Commit(next); return View(_state);
        }
    }
    public object Bind(PolicyCommand command)
    {
        lock (_gate)
        {
            Expected(command.ExpectedRevision); var p = Policy(command.PolicyId); var reviewer = Text(command.Reviewer, "بازبین"); var reason = Text(command.Reason, "دلیل");
            var previous = _state.ProjectionState.Projection;
            if (previous.Policy.PolicyId == p.PolicyId && previous.Policy.PolicyVersion == p.PolicyVersion)
                throw new WorkbenchError(409, "already-bound", "این سیاست از قبل در snapshot پذیرفته شده است.", _state.Revision);
            var nextProjection = WithPolicy(previous, p, true);
            var projected = _module.Projector.Apply(_state.ProjectionState, Event(nextProjection, Id("event-")), "fanasa-contracts");
            if (projected.Status != "applied" || projected.State is null)
                throw new WorkbenchError(409, "projection-rejected", string.Join("; ", projected.Errors.Prepend(projected.Status)), _state.Revision);
            var oldUsage = UsageFor(_state, previous.Limits);
            var next = Copy(_state); next.ProjectionState = projected.State;
            next.Tokens = Math.Min(oldUsage.Tokens, nextProjection.Limits.Rate.BurstCapacity);
            next.LastRefillAt = Time(DateTimeOffset.UtcNow);
            next.Revision++; Audit(next, "projection-accepted", reviewer, reason, new { p.PolicyId, p.PolicyVersion, nextProjection.ContractVersion, nextProjection.Limits.ConfigRevision, projector = projected.Status, producer = "fanasa-contracts (fixed synthetic identity)" });
            Commit(next); return View(_state);
        }
    }

    private static void Audit(WorkbenchState state, string type, string actor, string reason, object details)
        => state.History.Add(new(Id("audit-"), type, Time(DateTimeOffset.UtcNow), actor, reason, state.Revision, JsonSerializer.SerializeToNode(details, Json)));
    private void Commit(WorkbenchState next, Action? beforeWrite = null)
    {
        try
        {
            var expectedStateFingerprint = Fingerprint(_state);
            ValidateDefinitions(next);
            _mutationVersion = _database.Write(next, _mutationVersion, expectedStateFingerprint, ValidateDefinitions, beforeWrite);
        }
        catch (WorkbenchError) { throw; }
        catch (Exception error) when (StorageFailure(error))
        {
            if (error is System.Text.RegularExpressions.RegexMatchTimeoutException)
                throw new WorkbenchError(503, "validation-timeout", "اعتبارسنجی بومی در مهلت مقرر تمام نشد؛ دوباره تلاش کنید. هیچ تغییری ثبت نشد.");
            throw new WorkbenchError(503, "storage-invalid", "یکپارچگی یا دسترسی پایگاه داده تأیید نشد؛ هیچ اثر یا مصرفی ثبت نشد.");
        }
        _state = next;
    }
    public object Reset(RevisionCommand command)
    {
        lock (_gate)
        {
            Expected(command.ExpectedRevision); var next = Initial(); next.Revision = _state.Revision + 1;
            Audit(next, "reset", "اپراتور نمونه", "بازنشانی فقط مخزن مستقل میزکار", new { previousRevision = _state.Revision });
            Commit(next); return View(_state);
        }
    }
    private static Usage UsageFor(WorkbenchState state, AllocationLimits limits)
    {
        var now = DateTimeOffset.UtcNow; var period = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var used = state.PeriodId == period ? state.Admitted : 0;
        var tokens = Math.Min(limits.Rate.BurstCapacity, state.Tokens + Math.Max(0, (now - Instant(state.LastRefillAt)).TotalSeconds) * limits.Rate.RequestsPerSecond);
        return new(limits.AllocationId, period, used, Math.Max(0, limits.Quota.Requests - used), tokens,
            limits.ConfigRevision!.Value, state.LastAdmittedAt);
    }
}
