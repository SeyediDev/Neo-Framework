# پیاده‌سازی نامزد خواندن HR

این بسته، کد اجرایی محلی پروفایل خواندن و کنترل پاسخ است. طراحی آن با درخواست صریح کاربر «طراحی و پیاده‌سازی کن» در ۱۵ اکتبر ۲۰۲۶ انجام شد. پروفایل زیر طراحی جدید است؛ وجود آن به معنی ثبت قرارداد تجاری، اعطای دسترسی، provision شدن allocation یا فعال بودن APISIX نیست.

## هدف و مسیر تحقق

کاربر مجاز باید بتواند چارت یک سازمان و نسخهٔ مشخصی از تاریخچهٔ آن را بخواند. توکن سرویس، قرارداد فعال، عضویت، grant، محدودهٔ allocation و تأیید Gateway باید همگی برای همان مصرف‌کننده و همان محصول برقرار باشند. یک امضای درست به‌تنهایی مجوز دسترسی نیست.

| درخواست | action مستقل | scope لازم | مجوز بومی | گروه |
|---|---|---|---|---|
| `GET /api/organization-hr/v1/tenants/{tenant}/chart` | `organization.hr.chart.read` | `organization.hr.read` | `organization.read` | `organization.hr.read` |
| `GET /api/organization-hr/v1/tenants/{tenant}/history/{revision}` | `organization.hr.history.read` | `organization.hr.read` | `organization.read` | `organization.hr.read` |

نسخهٔ schema برابر `3.0.0`، profile برابر `fanasa.organization-hr-read/1`، template برابر `organization-hr-read/1` و نقش قرارداد برابر `HrReader` است. audience همان مقدار موجود `fanasa-organization-fabric` می‌ماند. action با scope یکسان فرض نمی‌شود. مسیر commands و عملیات مالی از این پروفایل مجوز نمی‌گیرند.

ترتیب اجرای `HrReadCoordinator`:

1. فقط GET با RawTarget دقیق، tenant UUID کوچک و revision مثبت Int64 پذیرفته می‌شود. query، encoding متفاوت و slash اضافه رد می‌شوند. یک byte از stream واقعی بررسی می‌شود تا بدنهٔ پنهان یا chunked غیرخالی رد شود.
2. adapter احراز باید `OrganizationHrBearer`، همان peer واقعی TLS، اعتبار/EKU/pin، امضای wire، introspection یکتای مصرف‌کننده و binding جاری را تأیید کند. provider پیش‌فرض `UnavailableHrReadAuthority` است.
3. adapter Fabric در transaction مستقل و همگام، با guard جاری nonce را در همان bucket `contract-policy.gateway-context.replay.v1` و کلید `tier:requestId` مصرف و commit می‌کند. رد بعدی یا قطع پاسخ این مصرف را برنمی‌گرداند.
4. snapshot فقط‌خواندنی تازه، کنترل پیش/پس از خواندن و تطبیق tenant/revision انجام می‌دهد. نتیجه به JSON مستقل تبدیل می‌شود. هیچ query یا entity تنبلی از snapshot بیرون نمی‌آید.
5. serialization کامل با سقف صریح پاسخ انجام می‌شود؛ سپس snapshot **جدید** و registry جاری دوباره کنترل می‌شوند. هم پاسخ200 و هم404 از این gate می‌گذرند. پس از کنترل deadline و cancellation، bytes آماده با `no-store` فرستاده می‌شوند.

سقف طراحی پاسخ1MiB و عمر witness/decision و budget اجرای درخواست حداکثر۵ثانیه است؛ deadline واقعی کمینهٔ انقضای تمام شواهد است. این سقف‌ها سهمیه یا نرخ یا قیمت نمی‌سازند. حد پاسخ، حد تمام حافظهٔ process نیست: Fabric موجود string دیتابیس را پیش از کنترل اندازه می‌سازد و Utf8JsonWriter می‌تواند buffer موقت داشته باشد. materialization نیز باید جداگانه محدود شود.

## نمونهٔ تنظیم درست

برای خواندن تاریخچهٔ revision1 در tenant توسعه، URL backend چنین است:

```text
/api/organization-hr/v1/tenants/7398590f-2d87-41ce-beda-19fb8e2a45fd/history/1
```

context باید action تاریخچه داشته باشد؛ داشتن scope `organization.hr.read` همراه با action چارت برای این URL کافی نیست. tenant URL، نگاشت introspection، عضویت، grant، projection و allocation باید همگی همین tenant باشند. تست‌ها از دادهٔ مصنوعی مجزا استفاده می‌کنند و مجوزی برای این شناسه ایجاد نمی‌کنند.

برای ساخت تنظیم واقعی از [قالب خاموش](receiver.template.json) شروع کنید:

1. product UUID، key، audience و مالکیت را از **ثبت مرکزی جاری** بخوانید. مشاهدهٔ زندهٔ این بسته محصول `b735c486-e711-49cb-bd62-0ce900ad557e` را با `owner=platform` و `tenantId=null` تأیید کرد. UUID tenant مالک نسازید؛ HR3 مالکیت platform را صریح مدل می‌کند. این metadata به‌تنهایی اختیار مصرف قرارداد نیست.
2. consumer/client/subject را از هویت معتبر و نگاشت یکتای introspection پر کنید. grant فعال `organization.read` و نقش قرارداد `HrReader` باید توسط منبع مرجع تأیید شوند. scope نوشتن جانشین خواندن نیست.
3. شناسه/نسخهٔ contract، policy و allocation و `configRevision` مستقل را از رویداد مرجع قرارداد و provisioner واقعی بگیرید. template قیمت و سهمیهٔ ساختگی ندارد. selector باید tuple کامل consumer/profile/product/contract را انتخاب کند و تطابق چندگانه را رد کند.
4. `canonicalScope` باید دقیقاً `tenant={consumer};products=organization-fabric;environments={environment};groups=organization.hr.read` باشد؛ zone نیز از محیط واقعی تأیید شود.
5. keyId/tier و pin گواهی واقعی peer را با sender یکسان کنید. secret در این JSON قرار نمی‌گیرد. Idempotency-Key برای خواندن اختیاری، تک‌مقداری، حداکثر۱۲۸کاراکتر ASCII از `[A-Za-z0-9._:-]` و بدون تغییر bytes است. نبود آن hash صفرbyte دارد؛ nonce همان requestId مستقل Gateway است.

`HrReadReceiverSettings.TryParse` property ناشناخته/تکراری، فیلد اجباری غایب، tuple نسخهٔ ناسازگار، شناسه یا canonical scope غلط و سقف بیش از طراحی را رد می‌کند. `MatchesCurrentRegistration` با snapshot تازهٔ ثبت مرکزی تطبیق می‌دهد. حتی JSON کامل و `enabled=true` یک Permit تولید نمی‌کند و هیچ endpointی را ثبت نمی‌کند.

## اجرا و حدود پذیرش

```powershell
dotnet run --project tests/Fanasa.HrReadCandidate.Tests.csproj -c Release
dotnet run --project http-tests/Fanasa.HrReadCandidate.Http.Tests.csproj -c Release
dotnet run --project contract-policy/tests/OriginalRegression/OriginalRegression.csproj -c Release
```

منابع PCC و Fabric در این بسته مستقل کپی و hash مبنای آن‌ها ثبت شده‌اند؛ سورس اصلی، CI10، release597 و مسیر زنده تغییر نمی‌کنند. schemaهای v1/v2 حفظ شده‌اند. schema/profile ناشناخته به نسخهٔ قدیمی fallback نمی‌کند.

تست‌های protocol/coordinator و profile fixture محلی‌اند. تست Fabric از SQLite واقعی موقت و schema موجود استفاده می‌کند؛ آزمون nonce پس از بازکردن مجدد DB و revocation در connection دوم را پوشش می‌دهد. این‌ها پذیرش native TLS، introspection واقعی، provisioning/observation، Collector یا APISIX عملیاتی نیستند.

پروژهٔ اختیاری `http` برای verifier پیام `fanasa.hr-read.context/1` و اتصال احراز HTTP اضافه شده است. نسخهٔ پیام جدید فقط در این نامزد تعریف می‌شود؛ sender جاری Gateway آن را ارسال نمی‌کند. قاب HMAC قبلی حفظ می‌شود و receiver فرمان قبلی تغییر نمی‌کند. کلید و authority مرجع پیش‌فرض unavailable هستند؛ ثبت endpoint فقط با فراخوانی صریح میزبان انجام می‌شود.

[قرارداد پیام و راهنمای اتصال میزبان](http/WIRE-CONTRACT.fa.md) فیلدها، headerها، محدودیت‌ها و مسئولیت منابع مرجع را دقیق مشخص می‌کند. آزمون‌های HTTP شامل fixtureهای احراز/گواهی مجزا و آزمون مسیر روی Kestrel واقعیِ موقت در loopback هستند؛ این آزمون loopback پذیرش mTLS یا SSO عملیاتی نیست.

برای اتصال عملیاتی هنوز guardهای مرجع میزبان، selector و persistence میزبان با tuple جدید، sender متناظر، منابع contract/allocation و مشاهدهٔ Gateway باید یکپارچه و روی topology واقعی آزموده شوند. پذیرش قدیمی Nginx/Kubernetes جای این آزمون را نمی‌گیرد. هیچ عملیات مالی یا cutover خودکار از این بسته اجرا نمی‌شود.

اطلاعات فعلی سرویس در [live-inspection.json](live-inspection.json) ثبت شده است. این مشاهده با سلامت اجرای Pod تفاوت وضعیت sync کنترلر GitOps را تفکیک می‌کند.

اصلاح زندهٔ liveness در Argo بررسی شده است، اما هنوز در مقادیر Helm ثبت نشده است. [راهنمای عملیات](ARGOCD-REPO-SERVER-HEALTH.md) اختلاف دو مسیر فایل منبع با release و گام دقیق ماندگار کردن اصلاح را ثبت می‌کند؛ ارتقای بعدی با مقادیر قدیمی می‌تواند timeout را به۱برگرداند.
