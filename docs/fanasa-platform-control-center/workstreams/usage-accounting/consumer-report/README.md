# گزارش دوره‌ای مصرف سازمان

منبع اصلی گزارش در [پروژهٔ مرکزی Access Management](../../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/ConsumerUsageReport.cs) قرار دارد و از همان Fabric و دفتر موجود استفاده می‌کند. API نسخه‌دار در سورس مرکزی اضافه شده و به‌صورت پیش‌فرض غیرفعال است؛ اتصال و استقرار عملیاتی از آن نتیجه نمی‌شود. [تحویل ۱۵ اکتبر](../../portal-central-readers-2026-10-15.md) وضعیت آزمون، تنظیمات و موانع اتصال را ثبت می‌کند.

ساخت Release با صفر هشدار/خطا و اجرای ۱۴۵ assertion روی Fabric و SQLite واقعی
موفق شد؛ [نتیجه و حدود پوشش](EVIDENCE-2026-10-13.md) ثبت شده‌اند.

[آداپتور نمایش مصرف](portal-adapter/README.md) نیز مستقل از میزبانی مرکزی و
Portal آماده شده است؛ دوره، مبلغ دقیق و quantity نامعلوم را برای نمایش حفظ می‌کند.

`ConsumerUsageReportStore.Read(consumer, from, to, maxRows)` همان Fabric را با
`ReadSnapshot` و `query_only` می‌خواند. دورهٔ UTC نیمه‌باز `[from,to)` و حداکثر
۳۶۶ روز است. تنها ردیف‌های `usage` وارد هزینه می‌شوند؛ receipt، پرداخت، شارژ،
رویداد pending و alias تحویل به هزینهٔ ثبت‌شده اضافه نمی‌شوند. تاریخ وقوع
`UsageOccurredAt ?? At` مبنای دوره است، حتی اگر ثبت حسابداری دیرتر انجام شود.

مبلغ از خود دفتر می‌آید و با تعرفهٔ فعلی محاسبه نمی‌شود. جمع با `decimal`
کنترل‌شده و تطبیق ضریب صحیح انجام می‌شود؛ از دست رفتن دقت در جمع مبلغ یا quantity
گزارش نامعتبر تولید می‌کند. مقدار JSON گزارش رشتهٔ دقیق است. قبل از deserialization،
مقادیر خام بررسی می‌شوند تا گردشدن بی‌صدای عدد نامعتبر ممکن نباشد. اعداد دفتر
مطابق serializer موجود، token عددی ساده بدون exponent هستند.

برای مصرف measured، شاهد `measured.operation` باید committed، بدون خطا و با
همان consumer، هویت، fingerprint، مقدار، مبلغ، نرخ و زمان وقوع باشد. واحد و
نسخه از رویداد اصلی گرفته می‌شوند. ردیف‌های قدیمی این اطلاعات را ندارند؛ در
گروه جدا، مبلغ و تعداد ثبت را دارند ولی `Quantity/Unit/MeterVersion` آن‌ها null
است. نسخه و واحد اندازه‌گیری از تعرفه یا اشتراک امروز حدس زده نمی‌شود.

محدودهٔ گزارش، هزینهٔ ثبت‌شده است؛ وضعیت پرداخت، صورتحساب، سهمیه، سهم عرضه‌کننده
یا کامل‌بودن همهٔ شواهد مصرف از آن نتیجه نمی‌شود. actor، مرجع تجاری، کلید دفتر
و متن خطای داخلی در خروجی گزارش وجود ندارند.

| وضعیت | معنی |
| --- | --- |
| `ready` | دفتر موجود و داده‌های لازم معتبرند؛ جمع دوره می‌تواند واقعاً صفر باشد |
| `notRecorded` | هنوز سند حساب وجود ندارد؛ مبلغ نامعلوم است |
| `incomplete` | حد خواندن رد شده؛ مبلغ و ردیف‌های جزئی نمایش داده نمی‌شوند |
| `unavailable` | ساختار، scope یا شاهد ناسازگار است؛ هیچ مبلغ جزئی برنمی‌گردد |

سقف خواندن ۱۰٬۰۰۰ ردیف و ۸ MiB سند حساب است. همهٔ ردیف‌ها، از جمله ردیف‌های
خارج دوره و رسیدها، در حد خواندن حساب می‌شوند. خطای مقدار، تکرار شناسه/charge،
نوع ردیف ناشناخته، نام JSON تکراری یا overflow گزارش موفق صفر تولید نمی‌کند.
consumer حساب با کلید ذخیره تطبیق دارد. ردیف `organization:{consumer}:{revision}`
و رویداد measured نیز consumer را مستقل تطبیق می‌دهند. ردیف legacy عمومی
consumer جداگانه ندارد؛ مالکیت آن تنها از سند حساب می‌آید و این گزارش پذیرش
کامل reconciliation را ادعا نمی‌کند.

## اتصال قابل بررسی برای مالک مرکز راهبری

API محلی `GET /api/accounting/v1/consumers/{consumerTenantId}/usage-report?from=...&to=...` فقط توکن انسانی معتبر client مشخص Portal، عضویت فعال، tenant فعال و مجوز زندهٔ `billing.read` را می‌پذیرد. `platform.catalog` مجوز مالی نیست. بازهٔ API و overload محافظت‌شده UTC و نیمه‌باز و کوتاه‌تر از ۳۶۶ روز است؛ overload قبلی store برای سازگاری همچنان حداکثر ۳۶۶ روز را می‌پذیرد.

خوانندهٔ مستقل [Portal](portal-transport/ConsumerUsageHttpReader.cs) فقط در سرور استفاده می‌شود و به provider اعتبارنامهٔ انسانی معتبر نیاز دارد. این provider، میزبانی Portal و realm واقعی هنوز پذیرفته نشده‌اند. mapper/renderer مستقل نمایش در `portal-adapter/` قرار دارد. مقدار، واحد و نسخهٔ legacy باید null بمانند؛ مبلغ رشتهٔ دقیق است و گزارش ناقص نباید صفر موفق نمایش داده شود.

## اجرای آزمون محلی

از root مخزن، با .NET 10 و packageهای موجود در cache اجرا شود:

```powershell
$consumerReportArtifacts = Join-Path (Get-Location) 'docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/tests/.artifacts'
dotnet restore docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/tests/ConsumerUsageReport.Tests.csproj --configfile docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/tests/offline-nuget.config -p:UseArtifactsOutput=true "-p:ArtifactsPath=$consumerReportArtifacts"
dotnet build docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/tests/ConsumerUsageReport.Tests.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:BuildInParallel=false -p:UseArtifactsOutput=true "-p:ArtifactsPath=$consumerReportArtifacts"
dotnet docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/tests/.artifacts/bin/ConsumerUsageReport.Tests/release/ConsumerUsageReport.Tests.dll
```

restore بالا منبع شبکه ندارد. خروجی build مستقل است و DLL میزبان نمونهٔ در حال
اجرا را بازنویسی نمی‌کند. harness پایگاه مصنوعی تازه‌ای برای آزمون می‌سازد؛
این داده‌ها هرگز به حساب واقعی، قرارداد یا مجوز تبدیل نمی‌شوند.
