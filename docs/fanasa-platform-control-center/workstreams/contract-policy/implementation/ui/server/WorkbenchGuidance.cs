using Fanasa.ContractPolicy;

namespace Fanasa.ContractPolicy.Workbench;

public sealed record ConfigurationIssue(string Field, string Code, string Message);
public sealed record ReadinessCheck(string Id, string Label, string Status, string Detail);
public sealed record WorkbenchReadiness(bool Ready, string CheckedAt, ReadinessCheck[] Checks,
    WorkbenchStorageHealth Storage, object Example, string RecommendedNext)
{
    public bool CanEvaluate => Storage.Healthy;
}

public sealed partial class WorkbenchStore
{
    private static List<ConfigurationIssue> PolicyIssues(PolicyInput? input)
    {
        var errors = new List<ConfigurationIssue>();
        if (input is null) { errors.Add(new("policy", "required", "مشخصات سیاست را وارد کنید.")); return errors; }
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 120)
            errors.Add(new("name", "invalid-name", "نام روشن و حداکثر ۱۲۰ نویسه برای سیاست وارد کنید."));
        if (input.ZoneConstraints is null || input.ZoneConstraints.Length != 1
            || input.ZoneConstraints[0] is not ("ir-private-1" or "ir-private-2"))
            errors.Add(new("zoneConstraints", "unsupported-zone", "دقیقاً یک منطقه انتخاب کنید: ir-private-1 یا ir-private-2. منطقه باید با محیط اجرای درخواست یکسان باشد."));
        if (!double.IsFinite(input.RequestsPerSecond) || input.RequestsPerSecond <= 0 || input.RequestsPerSecond > 1000000)
            errors.Add(new("requestsPerSecond", "invalid-rate", "نرخ باید عددی بزرگ‌تر از صفر و حداکثر ۱٬۰۰۰٬۰۰۰ درخواست در ثانیه باشد."));
        if (input.BurstCapacity < 1 || input.BurstCapacity > 1000000)
            errors.Add(new("burstCapacity", "invalid-burst", "ظرفیت لحظه‌ای باید بین ۱ و ۱٬۰۰۰٬۰۰۰ درخواست باشد؛ یک درخواست یک توکن مصرف می‌کند."));
        if (input.MonthlyQuota < 0 || input.MonthlyQuota > 1000000000)
            errors.Add(new("monthlyQuota", "invalid-quota", "سقف ماهانه باید بین صفر و ۱٬۰۰۰٬۰۰۰٬۰۰۰ باشد. صفر معتبر است و تمام اجراها را در پذیرش متوقف می‌کند."));
        return errors;
    }

    public object ValidateConfiguration(ValidateConfigurationCommand command)
    {
        lock (_gate)
        {
            RefreshValidatedStorage();
            var errors = PolicyIssues(command.Policy);
            var warnings = new List<ConfigurationIssue>();
            if (string.IsNullOrWhiteSpace(command.Author) || command.Author.Length > 500)
                errors.Add(new("author", "invalid-author", "برچسب نویسندهٔ پیش‌نویس را وارد کنید؛ حداکثر ۵۰۰ نویسه."));
            if (string.IsNullOrWhiteSpace(command.Reviewer) || command.Reviewer.Length > 500)
                errors.Add(new("reviewer", "invalid-reviewer", "برای انتشار، برچسب بازبین مستقل را وارد کنید."));
            else if (string.Equals(command.Author?.Trim(), command.Reviewer.Trim(), StringComparison.OrdinalIgnoreCase))
                errors.Add(new("reviewer", "independent-review-required", "نویسنده و بازبین باید متفاوت باشند. در کارگاه این‌ها برچسب نمونه‌اند؛ هویت واقعی نشست SSO جداست."));
            var policyValid = !errors.Any(x => x.Field != "reviewer");
            PolicyInput? normalized = null;
            if (policyValid)
            {
                var input = command.Policy;
                normalized = input with { Name = input.Name.Trim(), ZoneConstraints = input.ZoneConstraints.ToArray() };
                var proposed = new PolicyDocument("configuration-preview", _state.Policies.Concat(_state.Drafts).Max(p => p.PolicyVersion) + 1,
                    normalized.Name, normalized.ExplicitDeny, normalized.ZoneConstraints, normalized.RequestsPerSecond,
                    normalized.BurstCapacity, normalized.MonthlyQuota, command.Author!.Trim(), null, "Draft",
                    Time(DateTimeOffset.UtcNow), null, "بررسی تنظیمات بدون ثبت");
                foreach (var issue in _module.Projections.Validate(WithPolicy(_state.ProjectionState.Projection, proposed, false)))
                    errors.Add(new("policy", "native-validation-failed", issue));
                policyValid = !errors.Any(x => x.Field != "reviewer");
                if (!policyValid) normalized = null;
                if (input.ExplicitDeny) warnings.Add(new("explicitDeny", "explicit-deny", "منع صریح فعال است؛ نتیجهٔ عملیات ذخیره Deny خواهد بود، حتی با هویت و قرارداد معتبر."));
                if (input.MonthlyQuota == 0) warnings.Add(new("monthlyQuota", "zero-quota", "سهمیهٔ صفر تعریف معتبر است؛ PDP ممکن است Permit بدهد ولی PEP هیچ ذخیره‌ای انجام نمی‌دهد."));
                if (!input.ZoneConstraints.Contains(_state.TrustedContext!.Environment.Zone))
                    warnings.Add(new("zoneConstraints", "zone-mismatch", $"محیط معتبر دیتابیس در {_state.TrustedContext.Environment.Zone} است؛ این انتخاب در سناریوی پایه zone-denied می‌گیرد."));
                if (input.MonthlyQuota > 0 && input.MonthlyQuota < _state.Admitted && _state.PeriodId == DateTimeOffset.UtcNow.ToString("yyyy-MM"))
                    warnings.Add(new("monthlyQuota", "quota-below-current-usage", "سقف تازه از مصرف ثبت‌شده کمتر است؛ اتصال نسخه مصرف گذشته را پاک نمی‌کند و پذیرش تازه متوقف می‌شود."));
            }
            return new
            {
                valid = policyValid, canPublish = policyValid && errors.Count == 0, checkedAt = Time(DateTimeOffset.UtcNow),
                errors, warnings, normalizedPolicy = normalized, example = ConfigurationExample(_state),
                checks = new ReadinessCheck[]
                {
                    new("policy-definition", "قالب و حدود تنظیمات", policyValid ? "pass" : "fail", policyValid ? "تنظیمات با کنترل دامنه و اعتبارسنج بومی سازگارند." : "خطاهای مشخص‌شده کنار فیلدها را اصلاح کنید."),
                    new("independent-review", "بازبینی مستقل", errors.Any(x => x.Field == "reviewer") ? "fail" : "pass", "ذخیرهٔ پیش‌نویس مجوز انتشار یا اتصال به قرارداد نیست."),
                    new("execution-impact", "اثر تنظیمات بر اجرای نمونه", warnings.Count == 0 ? "pass" : "warning", warnings.Count == 0 ? "تنظیمات پیشنهادی مانع شناخته‌شده‌ای برای سناریوی پایه ندارند؛ تصمیم نهایی پس از اتصال توسط موتور صادر می‌شود." : "هشدارها معتبر بودن تعریف را از امکان اجرای موفق جدا می‌کنند."),
                }
            };
        }
    }

    public WorkbenchReadiness Readiness()
    {
        lock (_gate)
        {
            var storage = Integrity();
            if (!storage.Healthy)
                return new(false, Time(DateTimeOffset.UtcNow),
                    [new("database", "دیتابیس و یکپارچگی داده", "fail", "اعتبار دادهٔ پایدار تأیید نشد؛ ارزیابی و اجرای درخواست مسدود است.")],
                    storage, ConfigurationExample(_state), "خطاهای یکپارچگی دیتابیس را بررسی کنید؛ دادهٔ خراب خودکار بازنشانی نمی‌شود.");
            RefreshValidatedStorage();
            return ReadinessFor(_state, storage);
        }
    }

    private WorkbenchReadiness ReadinessFor(WorkbenchState state, WorkbenchStorageHealth storage)
    {
        var p = state.ProjectionState.Projection;
        var c = state.TrustedContext!;
        var now = DateTimeOffset.UtcNow;
        var checks = new List<ReadinessCheck>();
        void Check(string id, string label, bool pass, string success, string failure) => checks.Add(new(id, label, pass ? "pass" : "fail", pass ? success : failure));
        bool Window(string from, string to) => DateTimeOffset.TryParse(from, out var start) && DateTimeOffset.TryParse(to, out var end) && start <= now && now < end;
        Check("database", "دیتابیس تراکنشی و دادهٔ معتبر", storage.Healthy, "SQLite و مراجع، تعریف‌ها و اثرهای ثبت‌شده بررسی شدند.", "دادهٔ پایدار نامعتبر است؛ اجرا مسدود می‌شود.");
        Check("native-definition", "قالب بومی قرارداد", _module.Projections.Validate(p).Count == 0,
            "قالب و قواعد معنایی snapshot پذیرفته‌شده معتبرند.", "snapshot قرارداد را با قالب و قواعد بومی اصلاح کنید.");
        var bound = state.Policies.SingleOrDefault(x => x.PolicyId == p.Policy.PolicyId && x.PolicyVersion == p.Policy.PolicyVersion);
        Check("binding", "نسخهٔ منتشرشده و مرجع قرارداد", bound?.Status == "Published",
            $"قرارداد نسخهٔ {p.ContractVersion} به سیاست نسخهٔ {p.Policy.PolicyVersion} متصل است.", "نسخهٔ منتشرشده را صریحاً به قرارداد متصل کنید؛ سیاست بازنشسته قابل اجرا نیست.");
        Check("contract-window", "وضعیت و زمان قرارداد", p.Status == "Active" && Window(p.ValidFrom, p.ValidTo) && Instant(p.EffectiveAt) <= now,
            $"قرارداد فعال است؛ پایان اعتبار {p.ValidTo}.", "وضعیت، شروع اثر و پایان اعتبار قرارداد را بررسی کنید.");
        Check("identity", "هویت و مجوز کلاینت در دیتابیس", c.Subject.AuthenticationValid && c.Subject.ClientAuthorized
            && c.Subject.Audience == "fanasa-organization-fabric-api" && c.Subject.Scopes.Contains(c.Action),
            "هویت نمونه، audience و scope عملیات معتبرند.", "هویت، audience و scope مجاز منبع معتبر را بررسی کنید.");
        Check("tenant", "سازمان درخواست", c.Tenant.Active && c.Tenant.TenantId == p.ConsumerTenantId && c.Resource.ConsumerTenantId == p.ConsumerTenantId,
            "سازمان فعال و با مصرف‌کنندهٔ قرارداد منطبق است.", "سازمان معتبر درخواست باید با مصرف‌کنندهٔ قرارداد منطبق باشد.");
        Check("membership", "عضویت معتبر و در بازه", c.Membership.Active && c.Membership.SubjectId == c.Subject.SubjectId
            && c.Membership.TenantId == p.ConsumerTenantId && Window(c.Membership.ValidFrom, c.Membership.ValidTo),
            $"عضویت تا {c.Membership.ValidTo} معتبر است؛ این بازه از دیتابیس خوانده می‌شود.", "عضویت را در مرجع معتبر فعال و بازهٔ معتبر آن را بررسی کنید.");
        Check("grant", "نقش و مجوز مستقیم عملیات", c.Grant.Active && c.Grant.SubjectId == c.Subject.SubjectId
            && c.Grant.ConsumerTenantId == p.ConsumerTenantId && c.Grant.ProductId == p.ProductId && p.EligibleRoles.Contains(c.Grant.Role)
            && c.Grant.Actions.Contains(c.Action) && Window(c.Grant.ValidFrom, c.Grant.ValidTo),
            $"اعطای نقش {c.Grant.Role} برای {c.Action} معتبر است؛ قرارداد نقش ایجاد نمی‌کند.", "grant معتبرِ همین عضو، سازمان، محصول و عملیات را بررسی کنید.");
        Check("scope", "حق استفاده و محدودهٔ عملیات", c.Resource.ProductId == p.ProductId && p.FeatureEntitlements.Contains(c.Action)
            && p.Limits.Scope.Environments.Contains(c.Resource.Environment) && p.Limits.Scope.OperationGroups.Contains(c.Resource.OperationGroup),
            "محصول، عملیات، محیط و گروه عملیات با تخصیص منطبق‌اند.", "دامنهٔ قرارداد و تخصیص باید درخواست واقعی را پوشش دهند.");
        Check("zone", "منطقهٔ معتبر محیط", p.ZoneConstraints.Contains(c.Environment.Zone),
            $"منطقهٔ {c.Environment.Zone} مجاز است.", "منطقهٔ مجاز سیاست با منطقهٔ سرور یکسان نیست؛ نسخهٔ درست را منتشر و متصل کنید.");
        Check("explicit-deny", "اثر منع صریح", !p.Policy.ExplicitDenyActions.Contains(c.Action),
            "منع صریحی برای عملیات پایه وجود ندارد.", "منع صریح بر اجازه مقدم است؛ فقط با بازبینی نسخهٔ تازه می‌توان آن را تغییر داد.");
        var resource = state.Resources.SingleOrDefault(x => x.ResourceId == "unit-primary");
        Check("resource", "مالک واقعی رکورد", resource is not null && resource.TenantId == p.ConsumerTenantId
            && resource.ProductId == p.ProductId && resource.Environment == c.Resource.Environment,
            "مالک رکورد هدف از دیتابیس خوانده و با سازمان مجاز تطبیق داده می‌شود.", "مالک، محصول و محیط رکورد هدف با درخواست مجاز منطبق نیستند.");
        var usage = UsageFor(state, p.Limits);
        Check("quota", "ظرفیت پذیرش ماهانه", usage.Remaining > 0,
            $"{usage.Remaining} درخواست در ماه UTC {usage.PeriodId} باقی است؛ مصرف قبلی با انتشار پاک نمی‌شود.", "سهمیه صفر یا تمام شده است؛ اجازهٔ PDP به اجرای ذخیره تبدیل نمی‌شود.");
        checks.Add(new("rate", "ظرفیت لحظه‌ای", usage.Tokens >= 1 ? "pass" : "warning",
            $"{usage.Tokens:0.##} توکن از ظرفیت {p.Limits.Rate.BurstCapacity}؛ بازپرشدن با نرخ {p.Limits.Rate.RequestsPerSecond} در ثانیه. پذیرش نهایی هنگام اجرا دوباره بررسی می‌شود."));
        checks.Add(new("decision-lifetime", "تازگی تصمیم و کنترل هنگام اجرا", "pass", "هر تصمیم حداکثر ۳۰ ثانیه اعتبار دارد؛ تغییر binding، بازنشستگی، مالک رکورد و سهمیه پیش از اثر دوباره کنترل می‌شوند."));
        var firstFailure = checks.FirstOrDefault(x => x.Status == "fail");
        return new(firstFailure is null && storage.Healthy, Time(now), checks.ToArray(), storage, ConfigurationExample(state),
            firstFailure is null ? "سناریوی شرایط کامل را ارزیابی کنید، دو تعهد را اجرا کنید و افزایش یک ذخیره و یک مصرف را در شاهد اجرا ببینید." : firstFailure.Detail);
    }

    private static object ConfigurationExample(WorkbenchState state) => new
    {
        title = "ویرایش واحد فروش توسط کارشناس سازمان نمونه",
        goal = "کارشناس دارای عضویت و نقش معتبر فقط واحد متعلق به سازمان خودش را در محیط توسعه ذخیره کند؛ هر ذخیره دقیقاً یک واحد مصرف ثبت کند.",
        policy = new PolicyInput("ذخیرهٔ واحد فروش سازمان نمونه", false, [state.TrustedContext?.Environment.Zone ?? "ir-private-1"], 2, 4, 1000),
        author = "mina.policy", reviewer = "reza.review", reason = "دو درخواست در ثانیه، جهش چهار درخواست و سقف هزار ذخیره در ماه برای ویرایش ساختار سازمان نمونه",
        sizing = new[]
        {
            "فرض مثال: ۵۰ ذخیره در روز × ۲۰ روز کاری = ۱۰۰۰ ذخیره در ماه UTC. این عدد ظرفیت سازمان شما را تخمین نمی‌زند؛ با حجم واقعی خودتان تنظیم کنید.",
            "نرخ ۲ یعنی دو توکن در ثانیه اضافه می‌شود؛ ظرفیت ۴ اجازهٔ جهش کوتاه چهار درخواست را می‌دهد و ظرفیت هم‌زمانی یا تعداد کاربر نیست.",
            "منطقه از محیط معتبر دیتابیس می‌آید؛ داشتن سهمیه و قرارداد جای عضویت و grant را نمی‌گیرد.",
        },
        steps = new[]
        {
            new { title = "۱. هدف و دادهٔ معتبر", detail = "عضو، سازمان، محصول، عملیات و مالک رکورد را در منابع دیتابیس و آمادگی اجرا بررسی کنید." },
            new { title = "۲. بررسی و ذخیرهٔ پیش‌نویس", detail = "تنظیمات مثال را وارد کنید؛ اعتبارسنج سرور خطای هر فیلد و اثر سهمیه یا منع را نشان می‌دهد." },
            new { title = "۳. بازبینی و انتشار", detail = "بازبین مستقل محتوا و دلیل تغییر را بررسی و نسخهٔ ثابت را منتشر کند." },
            new { title = "۴. اتصال صریح", detail = "نسخهٔ منتشرشده را انتخاب و به قرارداد متصل کنید؛ فقط انتشار، مرجع اجرا را تغییر نمی‌دهد." },
            new { title = "۵. ارزیابی و اجرای تعهد", detail = "سناریوی شرایط کامل و مرجع پذیرفته‌شده را ارزیابی کنید؛ Permit را پیش از انقضای ۳۰ ثانیه اجرا کنید." },
            new { title = "۶. بررسی اثر", detail = "در شاهد اجرا، یک ذخیره و یک مصرف همراه با receipt ببینید؛ تکرار همان تصمیم مصرف دوم ایجاد نمی‌کند." },
        },
        expected = new { scenarioId = "permit", decision = "Permit", obligations = new[] { Admission, Resource },
            effect = "در صورت عبور از دو تعهد: saveCount +۱، مصرف +۱، یک operation و receipt در یک تراکنش SQLite." }
    };
}
