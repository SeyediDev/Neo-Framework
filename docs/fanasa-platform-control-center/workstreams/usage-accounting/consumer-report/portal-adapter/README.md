# آداپتور نمایش مصرف سازمان

این بسته، گزارش خواندنی `ConsumerUsageReportStore` را به دادهٔ نمایش محدود تبدیل
می‌کند و نمایش مستقل آن را فراهم می‌کند. در Portal یا API مرکزی ثبت نشده است؛
فایل‌های فعال محصول، DI، registry و دسترسی‌ها تغییر نکرده‌اند.

ساخت Release با صفر خطا/هشدار، ۱۱۶ assertion در .NET، ۲۴ گروه Node و ۱۹ گروه
مرورگر موفق‌اند؛ [شواهد و حدود اتصال](EVIDENCE-2026-10-13.md) ثبت شده‌اند.

[تحویل اتصال در ۱۴ اکتبر](INTEGRATION-HANDOFF-2026-10-14.md)، دریافت پیام دو مالک،
وضعیت source و محل تغییرات لازم را ثبت می‌کند؛ هنوز پذیرش API زنده اعلام نشده است.
[بازبینی Portal](../../../../../../products/Fanasa.UnifiedPortal/docs/USAGE-ADAPTER-REVIEW-2026-10-14.md)
نمایش محدود هزینهٔ ثبت‌شده را قابل پذیرش دانسته و ۲۳ آزمون مستقل Node را گذرانده
است؛ این بازبینی اتصال عملیاتی نیست و آزمون‌های کامل ۱۳ اکتبر را تکرار نکرده است.

`WorkspaceUsageReportAdapter.Map`، subject مورد انتظار، consumer و هر دو مرز دقیق
دورهٔ UTC را با envelope تطبیق می‌دهد. اختلاف، پاسخ نامعتبر یا مبلغ ناسازگار،
تمام مبلغ‌ها، ردیف‌ها، currency و timestamp پاسخ را حذف می‌کند. `notRecorded`،
`incomplete` و `unavailable` با معنای مستقل حفظ می‌شوند. حساب موجود با مصرف صفر
به‌صورت `ready` و صفر واقعی نمایش داده می‌شود.

جمع کنترل مبلغ با `BigInteger` در واحد خرد ارز انجام می‌شود؛ رشتهٔ مبلغ باید
بدون گردشدن در `decimal` قابل نمایش باشد. مجموع ردیف‌ها با مبلغ کل تطبیق دارد.
رشته‌ها به Number تبدیل نمی‌شوند. quantity و واحد/نسخهٔ تاریخی ردیف قدیمی null
می‌مانند. گزارش، سهمیه، موجودی، صورتحساب یا وضعیت پرداخت حدس نمی‌زند.

نام محصول فقط از lookup تأییدشدهٔ caller گرفته می‌شود؛ lookup نمی‌تواند ردیف یا
کاتالوگ بسازد. نبود نام، کلید معتبر همان محصول را حفظ می‌کند. خروجی JSON
subject، actor، کلید دفتر، reference و متن خطای داخلی ندارد.

## نمایش قابل اتصال

`usage-report-view.js` و `usage-report-view.css` اجزای مستقل‌اند. renderer، container
را پیش از اعتبارسنجی پاسخ تازه پاک می‌کند و سازمان پاسخ را با انتخاب فعال
تطبیق می‌دهد. متن‌ها با `textContent` ساخته می‌شوند. تعداد خدمات از محصول‌های
متمایز به دست می‌آید؛ مترها و نسخه‌های مختلف در ردیف‌های جدا باقی می‌مانند.
quantity نامعلوم «نامعلوم» نمایش داده می‌شود و عدد صفر جای آن قرار نمی‌گیرد.

```csharp
var usage = WorkspaceUsageReportAdapter.Map(
    authenticatedSubject, selectedConsumer, requestedFrom, requestedTo,
    centrallyAuthorizedEnvelope, approvedProductNames);
```

پس از serialization با `JsonSerializerDefaults.Web`:

```javascript
FanasaConsumerUsageReport.renderUsageReport(container, usage, selectedOrganizationId);
```

این mapper و envelope مرز احراز هویت نیستند. subject ارسال‌شده از فرم یا service
token، اثبات delegation محسوب نمی‌شود. پیش از اجرا، reader مرکزی باید subject
معتبر، consumer فعال، عضویت و مجوز زندهٔ `billing.read` را اثبات کند. درخواست
HTTP یا scope مالی فرضی در این بسته ساخته نشده است.

## تحویل به مالکان

مالک مرکز راهبری باید route/DTO نسخه‌دار و روش delegation محدود را اعلام و
store را به همان Fabric موجود وصل کند. `platform.catalog` مجوز مالی نیست.
reader سازمان و subject و دورهٔ درخواست را به پاسخ معتبر متصل می‌کند.

مالک Portal، بعد از تطبیق قرارداد مرکزی، همین mapping و renderer را در مسیر
خواندن مصرف شامل می‌کند. پاسخ دیرهنگام فقط باید برای subject، سازمان و دورهٔ
همچنان فعال اعمال شود. دادهٔ preview و sessionStorage منبع گزارش واقعی نیست.
mapper حاضر به نوع گزارش مرکزی وابسته است؛ این وابستگی در harness آزمون است.
برای اتصال محصول، DTO انتقال مستقل و نسخه‌دار یا DTO محلی مطابق API مصوب لازم
است؛ استفاده از بسته نباید Portal را به اسمبلی Web مرکز راهبری وابسته کند.

مدل فعلی `WorkspaceUsageItem` فیلدهای نسخهٔ متر و تعداد ثبت ندارد. renderer
فعلی، تعداد ردیف را تعداد سرویس می‌نامد و همواره «سقف طرح» اضافه می‌کند؛
بنابراین DTO تازه نباید بدون تغییر نمایش، به مدل قدیمی cast شود. هنگام اتصال،
اعلان ثابت «گزارش در دسترس نیست» باید تابع status شود و عنوان ثابت «این ماه»
با دورهٔ واقعی هماهنگ شود. این بسته تغییر آن فایل‌های مشترک را انجام نداده است.

## اجرای آزمون

از root مخزن، با .NET 10، Node موجود و packageهای موجود در cache:

```powershell
$consumerAdapterArtifacts = Join-Path (Get-Location) 'docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/tests/.artifacts'
dotnet restore docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/portal-adapter/tests/PortalAdapter.Tests.csproj --configfile docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/portal-adapter/tests/offline-nuget.config -p:UseArtifactsOutput=true "-p:ArtifactsPath=$consumerAdapterArtifacts"
dotnet build docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/portal-adapter/tests/PortalAdapter.Tests.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:BuildInParallel=false -p:UseArtifactsOutput=true "-p:ArtifactsPath=$consumerAdapterArtifacts"
$consumerAdapterFixtures = Join-Path (Get-Location) ('docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/portal-adapter/tests/results/fixtures-' + [guid]::NewGuid().ToString('N'))
dotnet docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/tests/.artifacts/bin/PortalAdapter.Tests/release/PortalAdapter.Tests.dll --export-fixtures $consumerAdapterFixtures
$env:CONSUMER_USAGE_REPORT_FIXTURES = $consumerAdapterFixtures
node docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/portal-adapter/tests/usage-report-view.test.cjs
node docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/portal-adapter/tests/browser-check.cjs --fixtures $consumerAdapterFixtures
```

restore منبع شبکه ندارد. اگر اسمبلی‌های مرکزی قبلاً با همان تنظیم و مسیر
ساخته و ثابت شده‌اند، `-p:BuildProjectReferences=false` به build افزوده می‌شود؛
این حالت جای build نسخهٔ تغییرکردهٔ محصول مرکزی را نمی‌گیرد.

فرمان مستقیم Node، همان `node:test` را بدون ساخت subprocess اجرا می‌کند.
آزمون مرورگر از Playwright و Chrome موجود استفاده می‌کند و چیزی نصب نمی‌کند؛
مسیر نصب متفاوت را با `FANASA_UI_PLAYWRIGHT_MODULE` و
`FANASA_UI_BROWSER_EXECUTABLE` تعیین کنید. صفحهٔ آزمون و فونت Vazir موجود به‌صورت
محلی بارگذاری می‌شوند؛ هیچ server یا دسترسی شبکه لازم نیست.

آزمون .NET، Fabric و SQLite واقعی را در پایگاه‌های موقت به کار می‌گیرد.
`--export-fixtures <fresh-directory>` پس از موفقیت بررسی‌ها، هشت JSON حاصل از
Store→Map را برای آزمون مرورگر صادر می‌کند؛ پوشهٔ موجود بازنویسی نمی‌شود.
این پایگاه‌ها و خروجی‌ها دادهٔ عملیاتی نیستند.
