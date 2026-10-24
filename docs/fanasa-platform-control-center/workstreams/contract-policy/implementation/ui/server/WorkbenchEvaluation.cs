using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.ContractPolicy;

namespace Fanasa.ContractPolicy.Workbench;

public sealed partial class WorkbenchStore
{
    public DecisionRun Evaluate(EvaluateCommand command)
    {
        lock (_gate)
        {
            RefreshValidatedStorage();
            var scenario = Scenarios.SingleOrDefault(x => x.Id == command.ScenarioId)
                ?? throw new WorkbenchError(400, "unknown-scenario", "سناریو باید از کاتالوگ سرور انتخاب شود.");
            var projection = Copy(_state.ProjectionState.Projection);
            var selection = "accepted";
            if (command.PolicyId is not null)
            {
                projection = WithPolicy(projection, Policy(command.PolicyId), false);
                selection = "candidate";
            }
            if (scenario.Id == "explicit-deny")
                projection = projection with { Policy = projection.Policy with { ExplicitDenyActions = ["organization.unit.save"] } };
            if (scenario.Id == "quota-exhaustion")
                projection = projection with { Limits = projection.Limits with { ConfigRevision = projection.Limits.ConfigRevision + 1, Quota = projection.Limits.Quota with { Requests = 0 } } };
            var now = DateTimeOffset.UtcNow;
            var evaluationNow = scenario.Id == "expired-decision" ? now.AddSeconds(-31) : now;
            var source = _state.TrustedContext!;
            var context = source with
            {
                RequestId = Id("workbench-"),
                Environment = source.Environment with { Now = Time(evaluationNow) },
                Evidence = source.Evidence with { ContractVersion = projection.ContractVersion, PolicyVersion = projection.Policy.PolicyVersion, ValidUntil = Time(evaluationNow.AddSeconds(60)) }
            };
            context = scenario.Id switch
            {
                "inactive-membership" => context with { Membership = context.Membership with { Active = false } },
                "no-grant" => context with { Grant = context.Grant with { Active = false } },
                "stale-evidence" => context with { Evidence = context.Evidence with { ValidUntil = Time(evaluationNow.AddSeconds(-1)) } },
                "wrong-product" => context with { Resource = context.Resource with { ProductId = "another-product" } },
                _ => context
            };
            var errors = _module.Projections.Validate(projection);
            if (errors.Count != 0) throw new WorkbenchError(500, "invalid-server-scenario", string.Join("; ", errors));
            var decision = _module.Evaluator.Evaluate(projection, context);
            var storedResource = _state.Resources.Single(x => x.ResourceId == (scenario.Id == "cross-tenant-resource" ? "unit-foreign" : "unit-primary"));
            var proposedName = command.ProposedUnitName?.Trim() ?? storedResource.Name;
            if (proposedName.Length is < 2 or > 120)
                throw new WorkbenchError(400, "invalid-unit-name", "نام واحد برای ذخیره باید بین ۲ و ۱۲۰ نویسه باشد.");
            var run = new DecisionRun(Id("decision-"), scenario.Id, decision, projection, context,
            [
                new("PAP", "مخزن سیاست مستقل میزکار", "typed/native-validated", "نویسنده و بازبین برچسب نمونه‌اند؛ سیاست منتشرشده immutable است."),
                new("PIP", "منابع معتبر نمونه در دیتابیس کارگاه", "server-owned synthetic database", "مرورگر سناریو و نام پیشنهادی واحد را می‌فرستد؛ هویت، عضویت، اعطای نقش و مالک واقعی از دیتابیسِ اعتبارسنجی‌شدهٔ سرور خوانده می‌شوند."),
                new("PDP", "Fanasa.ContractPolicy.dll", "native existing module", "Evaluate روی snapshot و context بومی فراخوانی شده؛ موتور کامل XML XACML اضافه نشده است."),
                new("PEP", "ContractPolicyModule.Pep + local adapters", "isolated native guard", "Permit بدون اجرای پذیرش و تطبیق مالک واقعی رکورد اثر محصول ندارد."),
                new("scenario", "کاتالوگ ثابت میزکار", "synthetic variation", scenario.Changes.Length == 0 ? "سناریوی پایه" : string.Join("؛ ", scenario.Changes))
            ], Inspect(projection, context, decision), Time(now), selection,
                new(storedResource.ResourceId, storedResource.TenantId, storedResource.ProductId, storedResource.Environment), Time(now), Fingerprint(_state.ProjectionState.Projection), proposedName, Fingerprint(source));
            var next = Copy(_state); next.Decisions.Add(run);
            Audit(next, "decision-evaluated", "آزمایشگاه", scenario.Title, new { run.DecisionId, scenario.Id, decision.Decision, decision.ReasonCode, policySelection = selection });
            Commit(next); return Copy(run);
        }
    }

    private static Inspection[] Inspect(ContractAccessProjection p, AccessEvaluationContext c, PolicyDecision d)
    {
        var now = Instant(c.Environment.Now);
        bool Window(string from, string to) => Instant(from) <= now && now < Instant(to);
        Inspection Step(string id, string label, bool pass, object input, string explanation) => new(id, label, pass ? "pass" : "fail", input, explanation);
        return
        [
            Step("target", "هدف سیاست", c.Resource.ProductId == p.ProductId, new { c.Resource.ProductId, policyProduct = p.ProductId }, "تطبیق محصول هدف؛ هدف متفاوت نتیجهٔ NotApplicable دارد."),
            Step("explicit-deny", "منع صریح", !p.Policy.ExplicitDenyActions.Contains(c.Action), new { c.Action, p.Policy.ExplicitDenyActions }, "در پروفایل deny-overrides منع صریح اجازه را متوقف می‌کند."),
            Step("identity", "هویت و کلاینت", c.Subject.AuthenticationValid && c.Subject.ClientAuthorized && c.Subject.Audience == "fanasa-organization-fabric-api" && c.Subject.Scopes.Contains(c.Action), c.Subject, "هویت، audience و scope از منبع معتبر نمونه در دیتابیس سرور می‌آیند."),
            Step("tenant", "سازمان فعال", c.Tenant.Active && c.Tenant.TenantId == p.ConsumerTenantId && c.Resource.ConsumerTenantId == p.ConsumerTenantId, new { c.Tenant, c.Resource.ConsumerTenantId, contractTenant = p.ConsumerTenantId }, "سازمان درخواست باید سازمان مصرف‌کنندهٔ قرارداد باشد."),
            Step("membership", "عضویت معتبر", c.Membership.Active && c.Membership.SubjectId == c.Subject.SubjectId && c.Membership.TenantId == p.ConsumerTenantId && Window(c.Membership.ValidFrom, c.Membership.ValidTo), c.Membership, "عضویت مستقل از حق استفادهٔ قراردادی خوانده می‌شود."),
            Step("contract", "قرارداد فعال و در بازه", p.Status == "Active" && Window(p.ValidFrom, p.ValidTo) && Instant(p.EffectiveAt) <= now, new { p.Status, p.ValidFrom, p.ValidTo, p.EffectiveAt }, "انقضای بازه status ذخیره‌شده را خودکار Ended نمی‌کند."),
            Step("action", "حق استفادهٔ عمل", c.Action == "organization.unit.save" && p.FeatureEntitlements.Contains(c.Action), new { c.Action, p.FeatureEntitlements }, "قرارداد فقط eligibility این عمل را فراهم می‌کند."),
            Step("grant", "اعطای واقعی نقش", c.Grant.Active && c.Grant.SubjectId == c.Subject.SubjectId && c.Grant.ConsumerTenantId == p.ConsumerTenantId && c.Grant.ProductId == p.ProductId && c.Grant.Actions.Contains(c.Action) && p.EligibleRoles.Contains(c.Grant.Role) && Window(c.Grant.ValidFrom, c.Grant.ValidTo), c.Grant, "نقش از قرارداد ایجاد نمی‌شود؛ grant معتبر شرط جداگانه است."),
            Step("scope", "محیط و گروه عملیات", p.Limits.Scope.Environments.Contains(c.Resource.Environment) && p.Limits.Scope.OperationGroups.Contains(c.Resource.OperationGroup), new { c.Resource.Environment, c.Resource.OperationGroup, p.Limits.Scope }, "allocation محدودهٔ محیط و گروه عملیات را مشخص می‌کند."),
            Step("zone", "ناحیهٔ اجرا", p.ZoneConstraints.Contains(c.Environment.Zone), new { c.Environment.Zone, p.ZoneConstraints }, "zone از محیط سرور می‌آید، نه فرم مرورگر."),
            Step("evidence", "تازگی و همبستگی نسخه‌ها", Instant(c.Evidence.ValidUntil) > now && c.Evidence.ContractVersion == p.ContractVersion && c.Evidence.PolicyVersion == p.Policy.PolicyVersion, c.Evidence, "شواهد منقضی یا نسخهٔ متفاوت نتیجهٔ Indeterminate دارند."),
            new("native-result", "نتیجهٔ بومی", d.Decision == "Permit" ? "pass" : "fail", new { d.Decision, d.ReasonCode, d.ValidUntil }, "ردیف‌های بالا inspection آزمایشگاه‌اند؛ پاسخ بومی trace هر gate صادر نمی‌کند."),
            new("obligations", "الزامات لازم برای اجرا", d.Decision == "Permit" ? "pending" : "skipped", d.Obligations, "تیک تکمیل تنها بعد از اجرای adapterهای محلی و guard بومی ثبت می‌شود.")
        ];
    }

    public ExecutionResult Execute(ExecuteCommand command)
    {
        lock (_gate)
        {
            RefreshValidatedStorage();
            var run = _state.Decisions.SingleOrDefault(x => x.DecisionId == command.DecisionId)
                ?? throw new WorkbenchError(404, "unknown-decision", "تصمیم باید قبلاً در همین سرور صادر شده باشد.");
            if (run.ScenarioId != command.ScenarioId) throw new WorkbenchError(409, "scenario-mismatch", "تصمیم متعلق به این سناریو نیست.", _state.Revision);
            if (command.FailObligation is not null && command.FailObligation is not ("admit-allocation" or "resource-tenant"))
                throw new WorkbenchError(400, "unknown-failure-mode", "فقط شکست دو adapter شناخته‌شده قابل انتخاب است.");
            var d = run.RawDecision; var now = DateTimeOffset.UtcNow; var nowText = Time(now);
            var usage = UsageFor(_state, run.Projection.Limits);
            ExecutionResult Refuse(string reason, ObligationResult[]? obligations = null)
            {
                var result = new ExecutionResult(false, false, reason, obligations ?? d.Obligations.Select(x => new ObligationResult(x.Id, "blocked", "اجرا مجاز نشد")).ToArray(), null, usage, d);
                var next = Copy(_state); Audit(next, "execution-refused", "PEP محلی", reason, new { run.DecisionId, run.ScenarioId, consumption = 0 }); Commit(next);
                return Copy(result);
            }
            if (run.PolicySelection != "accepted") return Refuse("candidate-not-bound");
            if (Fingerprint(_state.ProjectionState.Projection) != run.BindingFingerprint) return Refuse("binding-changed");
            if (run.TrustedSourcesFingerprint is null || Fingerprint(_state.TrustedContext!) != run.TrustedSourcesFingerprint)
                return Refuse("sources-changed");
            if (_state.Policies.SingleOrDefault(x => x.PolicyId == _state.ProjectionState.Projection.Policy.PolicyId)?.Status != "Published") return Refuse("retired-policy");
            if (_state.Executions.TryGetValue(run.DecisionId, out var receipt)) return Copy(receipt with { Idempotent = true, ReasonCode = "idempotent-replay", Usage = UsageFor(_state, run.Projection.Limits) });
            if (d.Decision != "Permit") return Refuse("decision-denied");
            if (run.ScenarioId == "unknown-obligation")
            {
                var labTamperedDecision = d with { Obligations = d.Obligations.Append(new DecisionObligation("urn:fanasa:obligation:unknown:1", "Permit", new JsonObject())).ToArray() };
                var guarded = _module.Pep.CanExecute(labTamperedDecision, labTamperedDecision.Obligations.Select(x => x.Id).ToArray(), nowText);
                if (!guarded) return Refuse("unknown-obligation", [new("urn:fanasa:obligation:unknown:1", "refused", "کپی دست‌کاری‌شدهٔ آزمایشگاه توسط guard بومی رد شد؛ rawDecision اصلی حفظ شد")]);
                return Refuse("unexpected-native-guard-result");
            }
            if (d.Obligations.Any(x => x.Id != Admission && x.Id != Resource))
            {
                _ = _module.Pep.CanExecute(d, d.Obligations.Select(x => x.Id).ToArray(), nowText);
                return Refuse("unknown-obligation");
            }
            if (!_module.Pep.CanExecute(d, [Admission, Resource], nowText))
                return Refuse(d.ValidUntil is not null && now >= Instant(d.ValidUntil) ? "expired-decision" : "native-pep-rejected");
            var admission = d.Obligations.Single(x => x.Id == Admission).Parameters.Deserialize<AllocationLimits>(ContractJson.Options)!;
            var requirement = d.Obligations.Single(x => x.Id == Resource).Parameters.Deserialize<ResourceTenantRequirement>(ContractJson.Options)!;
            var realResource = _state.Resources.SingleOrDefault(x => x.ResourceId == run.Resource.ResourceId);
            if (command.FailObligation == "resource-tenant") return Refuse("resource-adapter-unavailable", [new(Admission, "blocked", "هیچ سهمیه‌ای مصرف نشد"), new(Resource, "refused", "شکست کنترل‌شدهٔ adapter محلی")]);
            if (realResource is null || realResource.TenantId != requirement.ConsumerTenantId || realResource.ProductId != requirement.ProductId || realResource.Environment != requirement.Environment || run.Context.Action != requirement.Action)
                return Refuse("resource-tenant-mismatch", [new(Admission, "blocked", "هیچ سهمیه‌ای مصرف نشد"), new(Resource, "refused", "مالک واقعی رکورد با سازمان مجاز متفاوت است")]);
            if (command.FailObligation == "admit-allocation") return Refuse("admission-adapter-unavailable", [new(Admission, "refused", "شکست کنترل‌شدهٔ adapter محلی"), new(Resource, "blocked", "هیچ عملیات محصولی اجرا نشد")]);
            usage = UsageFor(_state, admission);
            if (usage.Remaining <= 0) return Refuse("quota-exhausted", [new(Admission, "refused", "سقف ماهانهٔ allocation پر شده است"), new(Resource, "blocked", "هیچ عملیات محصولی اجرا نشد")]);
            if (usage.Tokens < 1) return Refuse("rate-exhausted", [new(Admission, "refused", "توکن پذیرش نرخ کافی نیست"), new(Resource, "blocked", "هیچ عملیات محصولی اجرا نشد")]);
            var fulfilled = new[] { Admission, Resource };
            if (!_module.Pep.CanExecute(d, fulfilled, Time(DateTimeOffset.UtcNow))) return Refuse("native-pep-rejected");
            var committed = Copy(_state); committed.PeriodId = usage.PeriodId;
            committed.Admitted = usage.Admitted + 1; committed.Tokens = usage.Tokens - 1;
            committed.LastRefillAt = nowText; committed.LastAdmittedAt = nowText;
            var savedName = run.ProposedUnitName ?? realResource.Name;
            var operation = new Operation(Id("operation-"), run.DecisionId, run.ScenarioId, realResource.ResourceId, realResource.TenantId, run.Context.Action, nowText, 1, savedName);
            committed.Operations.Add(operation);
            committed.Resources[committed.Resources.FindIndex(x => x.ResourceId == realResource.ResourceId)] = realResource with { Name = savedName, SaveCount = realResource.SaveCount + 1, LastSavedAt = nowText };
            var success = new ExecutionResult(true, false, "executed", [new(Admission, "fulfilled", "یک پذیرش محلی در سهمیه و token bucket ثبت شد"), new(Resource, "fulfilled", "مالک رکورد از مخزن محلی تطبیق داده شد")], operation, UsageFor(committed, admission), d);
            committed.Executions.Add(run.DecisionId, success);
            Audit(committed, "operation-executed", "PEP محلی", "guard بومی و دو adapter موفق؛ اثر محصول و مصرف در یک commit", new { operation.OperationId, run.DecisionId, resourceSaveCount = realResource.SaveCount + 1, admitted = committed.Admitted });
            try
            {
                Commit(committed, () =>
                {
                    if (!_module.Pep.CanExecute(d, fulfilled, Time(DateTimeOffset.UtcNow)))
                        throw new WorkbenchError(409, "expired-decision", "تصمیم پیش از ثبت تراکنش منقضی شد؛ دوباره ارزیابی کنید.");
                });
            }
            catch (WorkbenchError error) when (error.Code == "expired-decision")
            { return Refuse("expired-decision"); }
            return Copy(success);
        }
    }
}
