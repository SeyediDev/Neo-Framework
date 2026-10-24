# پیشنهاد اولیه wiring و وضعیت اتصال runtime

این سند seamهای مرحله prototype را ثبت می‌کند. توسعه بعدیِ مسیر محلی در فایل‌های
محصول انجام شده است: `UsageContracts.cs`، `OrganizationUsageMetering.cs`،
`MeasuredUsageStore.cs`، `Accounting.cs`، `AccountingApi.cs`، `OrganizationFabric.cs`،
`OrganizationApi.cs` و `FabricServices.cs`. وضعیت جاری و تنظیمات در
[راهنمای runtime](../../../../products/Fanasa.AccessManagement/docs/USAGE-ACCOUNTING.md) است.
نام‌های actual document برابر `measured.operation` و `measured.event` و متد append
داخلی برابر `AccountingStore.AppendMeasured` هستند. جدول زیر شواهد تاریخی سورس
پیش از توسعه است؛ شماره خط‌های آن locator جاری محسوب نمی‌شوند.

## مبنای خوانده‌شده

| سورس | واقعیت قابل reuse |
| --- | --- |
| [AccessDomain.cs:7](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/AccessDomain.cs:7) | UsageEvent/RecordUsageCommand قدیمی فاقد party/contract/allocation/operation/rate کامل‌اند |
| [PersistentAccessManagement.cs:173](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/PersistentAccessManagement.cs:173) | `usage.receipt` و `usage` در یک transaction؛ اختلاف serialized payload رد می‌شود؛ debit و outbox ندارد |
| [Accounting.cs:38](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Accounting.cs:38) | receipt قبل از expectedRevision؛ تغییر payload conflict؛ actor داخل fingerprint نیست |
| [Accounting.cs:66](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Accounting.cs:66) | usage با tariff جاری؛ UnitPrice/Amount command برای قیمت usage استفاده نمی‌شوند |
| [Accounting.cs:93](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Accounting.cs:93) | account و AccountingChanged در Fabric اتمیک؛ payload رویداد، UsageEvent کامل نیست |
| [FabricDatabase.cs:27](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/FabricDatabase.cs:27) | transaction فوری، synchronous ThreadLocal و savepoint تو در تو |
| [FabricDatabase.cs:60](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/FabricDatabase.cs:60) | document upsert و outbox unique key؛ immutability باید در adapter enforce شود |
| [FabricDatabase.cs:83](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/FabricDatabase.cs:83) | Outbox(after,take) حداکثر ۱۰۰؛ ack/claim/DLQ ندارد |
| [FabricServices.cs:17](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/FabricServices.cs:17) | همان singleton store موجود؛ Organization:MeterChanges پیش‌فرض false |
| [OrganizationFabric.cs:41](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/OrganizationFabric.cs:41) | metering فقط با همان شیء Fabric؛ transaction business/history/receipt/charge/outbox |
| [OrganizationFabric.cs:62](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/OrganizationFabric.cs:62) | request receipt actor-bound؛ RequestId فعلاً optional |
| [AccountingApi.cs:18](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/AccountingApi.cs:18) | API cookie/CSRF؛ endpoint ingest سرویس-to-سرویس آماده نیست |
| [PersistentAccessManagement.cs:192](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/PersistentAccessManagement.cs:192) | legacy charge غیرفعال؛ revenue share مصوب از gross حاصل نمی‌شود |

## اتصال پیشنهادی در همان PCC

`UsageIntakeV1`، `MeasuredUsageCharge` و dispatcher، مرزهای ماژولی داخل میزبان فعلی‌اند. interface مستقل process، DB یا broker جدید ایجاد نمی‌کند. نام نوع پیشنهادی از `UsageEvent` قدیمی جدا شود، مثلاً `Metering.UsageEventV1`.

مالک ادغام فایل‌های مشترک باید این seamها را بررسی کند:

1. اضافه‌کردن `AccountingStore.ExecuteMeasuredUsage` یا معادل typed داخلی. ورودی `ValidatedRateSnapshot` فقط توسط trusted resolver ساخته شود؛ Decimal amount، currency/mode، ظرفیت حساب، ledger identity و receipt در دامنهٔ Accounting enforce شوند. الگوی sample برای بررسی است و API نوشتن مستقیم ledger عمومی نمی‌شود.
2. receipt/event و alias logical operation در namespaceهای نسخه‌دار همین `fabric_documents` ذخیره شوند. مدل پیشنهادی `usage.event.v1`، `usage.receipt.v1` و `usage.operation.v1` است؛ نگاشت نام‌های دقیق sample در سورس آن دیده می‌شود. مصرف خام قدیمی overwrite نمی‌شود.
3. پس از احراز producer و context محدود، در یک `FabricDatabase.Transaction`: lookup receipt و operation/pending و بررسی fingerprint → برای logical operation جدید، اعتبارسنجی تاریخی → append یک LedgerEntry(kind=usage) با snapshot → ثبت immutable event و receipt → `Emit` publish intent نسخه‌دار. replay داخلی همسان با تعرفه/وضعیت متغیر امروز reprice نمی‌شود؛ این ترتیب جای احراز caller یا authorization مشاهدهٔ پاسخ را نمی‌گیرد. `receivedAt` از intake ثبت شود. expectedRevision و actor معتبر در mutation مالی حفظ شوند.
4. business operation محلی `unit.save` باید از همین transaction استفاده کند و rate هنگام قبول operation freeze شود. producer commit، receipt کسب‌وکار و event/outbox از هم جدا نشوند. مسیر قدیمی optional metering برای همین operation هم‌زمان debit نکند. این بسته تنظیم `Organization:MeterChanges` را فعال نکرده است.
5. ingest سرویس بیرونی در آینده با authenticated producer و permission محدود همین module؛ API cookie موجود به endpoint عمومی متر تبدیل نشود. prefix اختصاصی ledger مانند `measured:v1:` برای intake رزرو و از BillingCommand عمومی منع شود تا receipt قدیمی با receipt typed جدید برخورد نکند. party، price و scope از body مشتری اعتماد نمی‌گیرند. transport خطا: 400 schema، 409 immutable identity conflict، 503 durable acceptance نامعلوم/ناپذیرفته؛ پاسخ موفق فقط پس از commit receipt.
6. source resolver از رجیستری مرکزی و projection معتبر read می‌کند؛ در network await داخل Fabric transaction قرار نگیرد. پس از resolve، version/expiry معتبر هنگام commit دوباره check شود؛ رابطه با store مستقل با event/reconciliation تضمین می‌شود، transaction توزیع‌شده ادعا نمی‌شود.

`AccountingStore.Read` بر همین BillingAccount جدید، outstanding و available credit را گزارش می‌کند. owner، contract، allocation و rate version در immutable usage document linked به LedgerEntry.Key می‌مانند؛ DTO ledger موجود را با فیلدهای حدسی پر نمی‌کنیم. LedgerEntry.At زمان ثبت حساب است و occurredAt در مصرف باقی می‌ماند.

مصرف completed پیش از revoke باید با شاهد تاریخی خودش تطبیق شود؛ این کار به مجوز اجرای operation تازه تبدیل نمی‌شود. `AccountingStore.Read` فعلی tenant غیرفعال را نمی‌خواند؛ مسیر internal حسابداری تاریخی یا pending معتبر برای این وضعیت باید در ادغام مشخص شود. sample tenant-deactivation و قیمت‌گذاری تاریخچه پس از حذف محصول را آزموده یا کامل اعلام نمی‌کند.

برای مصرف completed از محصولی با DB مستقل، outbox محصول و transaction Accounting دو commit جدا هستند. backlog metering و reconciliation این فاصله را پوشش می‌دهند. اگر اعتبار مصرف completed قابل ثبت نباشد، event به accounting-pending می‌رود و برای تطبیق نگه داشته می‌شود؛ حذف آن یا اجرای دوبارهٔ backend بدهی را حل نمی‌کند. کنترل اعتبار پیش از اجرای مدل‌های پرریسک به reservation واقعی همان مدل نیاز دارد. این وضعیت در پایلوت یک‌تراکنشی محلی لازم نیست.

## outbox موجود و dispatcher پیشنهادی

تنها از `FabricDatabase.Emit/Outbox` و `fabric_documents` موجود استفاده شود. stream مالی و eventهای دامنه قابل مشاهده‌اند؛ sample sink مستقل، consumer idempotent را شبیه‌سازی می‌کند و دفتر دوم محصول نیست.

برای یک worker اولیه، durable cursor به destination و stream/epoch بازیابی bind شود. read batch → ارسال با event/business key ثابت → receipt durable مقصد → commit ack/cursor محلی. crash بعد از commit مقصد و قبل از cursor، همان payload را دوباره می‌فرستد. cursor نباید از row شکست‌خورده عبور کند مگر quarantine ثبت‌شده با مسئول، دلیل، payloadDigest و replay audit وجود داشته باشد. retention تا تضمین replay و backup ack پذیرفته نشده، rowهای مالی را حذف نمی‌کند.

در رشد چند worker، claim با lease، owner/fencing و compare-and-set لازم است. expiry تنها حق تحویل دوبارهٔ event idempotent را می‌دهد، نه حق تکرار side effect ناشناختهٔ backend. backoff محدود، retry count، poison quarantine، alert lag و replay با payload اصلی به adapter افزوده می‌شوند. مدل fixture dispatcher تک‌worker است؛ ظرفیت یا topology تولید را اثبات نمی‌کند.

[EfOutboxStore](../../../../src/Neo.Infrastructure/Features/Outbox/EfOutboxStore.cs:10) و [ProcessOutboxRecurringJob](../../../../src/Neo.Application/Features/Outbox/Implementation/ProcessOutboxRecurringJob.cs:12) مرجع lease/transaction هستند. آن‌ها بر EF/queue و DTO دیگر کار می‌کنند و drop-in برای Fabric نیستند. `OutboxMessageProcessor` پاسخ سابق را بدون مقایسهٔ payload برمی‌گرداند و `IIdempotencyStore` digest ندارد؛ Redis TTL جای receipt مالی پایدار را نمی‌گیرد. [SqlServerOutboxExtensions](../../../../src/Neo.Infrastructure/Features/Messaging/SqlServerOutboxExtensions.cs:13) نیز مقصد SQL Server را فرض می‌کند؛ برای این پایلوت اضافه نمی‌شود.

## سازگاری و نگهداری

schema مصرف متعلق به این workstream است؛ projection/Decision/limits متعلق به workstream Contract & XACML است. allocationId پایدار، operation و version referenceها نقاط اتصال‌اند. signer/schema قرارداد تجاری یا XACML rule اینجا تولید نمی‌شود.

هیچ Skill، MCP schema، recipe یا snapshot موجود به API جدید runtime اشاره نمی‌کند، زیرا endpoint تازه‌ای فعال نشده است. پس companionهای موجود نیازمند تغییر نیستند. پس از ادغام واقعی ExecuteMeasuredUsage/ingest، مالک تغییر باید README محصول، knowledge recipe و schema MCP متأثر را هم‌زمان به‌روزرسانی و smoke test کند. تغییر fingerprint سورس به‌تنهایی اثبات صحت recipe نیست.
