# Fanasa Usage & Accounting — بستهٔ مستقل ۱٫۰

تاریخ: ۱۰ اکتبر ۲۰۲۶. مالک قرارداد مصرف: این workstream. وضعیت: **fixture و قرارداد ۱٫۰، به‌همراه مسیر runtime اختیاری برای unit.save؛ پذیرش مالی در محیط عملیاتی انجام نشده است.**

پس از مرحله prototype، مسیر `Metering.UsageEventV1`، `MeasuredUsageStore` و append
داخلی Accounting در محصول پیاده‌سازی شد. [راهنمای runtime](../../../../products/Fanasa.AccessManagement/docs/USAGE-ACCOUNTING.md)
منبع وضعیت جاری، تنظیمات و محدودیت‌های اتصال است. sample و شواهد اولیه زیر،
آزمایش تاریخی prototype مستقل‌اند و با مسیر محصول یکی فرض نمی‌شوند.

مرحلهٔ دوم runtime، منبع اختیاری ContractPolicy، قیمت‌های تغییرناپذیر نسخه‌دار
و dispatcher پایدار با lease، retry و ack را در همان Fabric اضافه می‌کند؛ همهٔ
featureها پیش‌فرض خاموش‌اند. [شواهد مرحلهٔ دوم](RUNTIME-EVIDENCE.v2.md) از
manifest و نتیجهٔ تاریخی مرحلهٔ اول مستقل است.

مرحلهٔ سوم runtime، تطبیق خواندنی شواهد مصرف با دفتر و outbox و retry صریح حسابداری
برای شاهد معتبر pending را اضافه می‌کند. این API مدیریتی، رویداد مصرف تازه دریافت
نمی‌کند؛ [راهنمای محصول](../../../../products/Fanasa.AccessManagement/docs/USAGE-ACCOUNTING.md)
و [شواهد مرحلهٔ سوم](RUNTIME-EVIDENCE.v3.md) مرز و نتیجهٔ محلی را توضیح می‌دهند.
پنل `/UsageAccounting` همین گزارش و retry را با فیلتر، جست‌وجو، جزئیات مبلغ دقیق،
دلیل بازبینی و حفظ درخواست با نتیجهٔ نامشخص ارائه می‌کند. پیش‌نمایش موقت صرفاً از
هویت و مصرف مصنوعی استفاده می‌کند؛ نتیجهٔ بررسی دسکتاپ/موبایل در شواهد سوم آمده است.

این بسته برای FPC-013، FPC-017، FPC-019 و FPC-020، schema مصرف، مرز billable یک API، نمونهٔ idempotent و reconciliation و پیشنهاد wiring را تحویل می‌دهد. مرحله prototype داخل همین directory انجام شد؛ توسعه runtime بعدی در محصول است. [معماری مرجع](../../ARCHITECTURE.md)، [پلن توسعه](../../GROWTH-AND-DEVELOPMENT-PLAN.md) و [هماهنگی](../../EXECUTION-COORDINATION.md) تغییر نکرده‌اند.

در تحویل تاریخی ۱۳ اکتبر، [ماژول گزارش دوره‌ای مصرف سازمان](consumer-report/README.md) و
آزمون مستقل آن اضافه شد. منبع، دفتر ثبت‌شده و snapshot همان Fabric است؛ تاریخ
وقوع، قیمت تاریخی و دقت پول حفظ می‌شوند. در آن تحویل، این ماژول در API مرکزی ثبت نشده بود
و اتصال مالی Portal از آن نتیجه نمی‌شود؛ تحویل wiring نزد مالک مرکز راهبری است.
ساخت Release و ۱۴۵ assertion محلی موفق‌اند؛ [شواهد گزارش مصرف](consumer-report/EVIDENCE-2026-10-13.md)
از نتیجه‌های تاریخی مراحل قبلی مستقل است.
[آداپتور و نمایش مصرف](consumer-report/portal-adapter/README.md) نیز در بستهٔ
مستقل تحویل شده: ۱۱۶ assertion، ۲۴ گروه Node و ۱۹ گروه مرورگر موفق، بدون تغییر
فایل‌های فعال مرکز راهبری/Portal یا ثبت endpoint فرضی.

## خروجی قابل ادغام

| فایل | کاربرد |
| --- | --- |
| [UsageEvent 1.0](schemas/usage-event-v1.schema.json) | wire schema محدود پایلوت؛ owner/consumer مستقل، قرارداد، allocation، operation، meter و snapshot نرخ |
| [قرارداد و billable boundary](CONTRACT.md) | معنا، اعتبارسنجی trusted context، هویت تکرار و lifecycle نتیجهٔ نامعلوم |
| [مصرف نمونه](fixtures/usage-event-v1.json) | یک مصرف مصنوعی از owner A برای consumer B |
| [تطبیق دفتر](fixtures/reconciliation-v1.json) | دو operation با نرخ‌های ۱۲ و ۱۸؛ مبلغ مورد انتظار ۳۰ IRR، delivery تکراری و خارج از ترتیب |
| [پیشنهاد اتصال](WIRING.md) | reuse از Accounting/Fabric/outbox؛ مرز تغییرات لازم نزد مالک فایل‌های مشترک |
| [بازیابی و پذیرش](RECOVERY-AND-ACCEPTANCE.md) | ماتریس crash، unknown، restore و تفاوت دوام commit با RPO backup |
| [sample](sample/) | harness مستقل با Fabric و DTOهای واقعی ledger؛ adapter قیمت snapshot پیشنهادی |
| [schema-check](schema-check/) | اعتبارسنجی schema/fixture و نمونه‌های منفی؛ محدودیت validator در همان ابزار اعلام می‌شود |
| [شواهد اجرا](EVIDENCE.md) | فرمان‌ها، نتیجهٔ واقعی و حدود پوشش |
| [سازگاری بین سه بسته](COMPATIBILITY.md) | Decision تولیدشدهٔ سیاست، admission واقعی fixture Gateway و یک debit پس از شاهد commit |

از root مخزن اجرا کنید؛ sample مقصد تازه می‌سازد و نتیجهٔ قبلی را overwrite نمی‌کند:

```powershell
python -B docs/fanasa-platform-control-center/workstreams/usage-accounting/schema-check/validate.py
python -B -m unittest discover -s docs/fanasa-platform-control-center/workstreams/usage-accounting/schema-check -p 'test_*.py' -v
& ./docs/fanasa-platform-control-center/workstreams/usage-accounting/sample/run.ps1
```

Python استاندارد و .NET 10 همراه packageهای موجود محصول لازم‌اند؛ validator ساختاری dependency تازه نمی‌خواهد. outputهای build و اجرای sample زیر همین پوشه و ignored هستند. wrapper فقط برای build محلی fixture، NuGet audit شبکه‌ای را غیرفعال می‌کند؛ پذیرش امنیت packageها یا تغییر تنظیم runtime محصول از این اجرا نتیجه نمی‌شود.

## برش پایلوت

محصول مرجع `organization-fabric` و API موجود `POST /api/organization/tenants/{tenantId}` با `OrgCommand.Operation=unit.save` است. consumer از owner متفاوت است. این انتخاب به endpoint واقعی [OrganizationApi](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/OrganizationApi.cs:36) و meter موجود [OrganizationFabric](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/OrganizationFabric.cs:157) متکی است؛ ثبت این fixture شاهد عرضه یا قرارداد امضاشدهٔ همین محصول در محیط مستقر نیست.

مرز billable، **commit یک تغییر معتبر واحد سازمانی و ثبت شاهد پایدار آن** است. quantity همیشه یک `change` است. درخواست نامجاز، validation/revision failure، rollback، پاسخ status، retry و دریافت دوبارهٔ رویداد charge تازه نمی‌سازند. HTTP 200 یا log Gateway به‌تنهایی شاهد این مرز نیست. تعریف no-op در نسخهٔ نخست مطابق رفتار فعلی store است: اگر فرمان معتبر پذیرفته و revision تازه commit شود یک change است؛ «فقط اختلاف واقعی داده» به definition و نسخهٔ دیگری نیاز دارد.

نمونه از postpaid با قیمت ثابت هر operation استفاده می‌کند. در مسیر همان Fabric، کنترل credit و business commit و charge می‌توانند یک تراکنش محلی داشته باشند؛ reservation مالی مستقل برای این برش لازم نیست. PAYG در این fixture پوشش ندارد، ولی کنترل wallet برای عملیات کوتاه محلی نیز می‌تواند در همان تراکنش باشد. reservation فقط وقتی topology اجرا یا مدل پرداخت واقعاً نیاز دارد، مثلاً provider پرهزینه با نتیجهٔ دیررس، با reserve/finalize/release معتبر افزوده می‌شود.

## مبنای تاریخی نمونهٔ ۱۰ اکتبر و شکاف ادغام آن

- `PersistentAccessManagement.RecordUsage` receipt و مصرف خام را اتمیک ثبت می‌کند، اما به Accounting debit یا UsageEvent outbox متصل نیست.
- `AccountingStore.Execute` receipt و ledger و `AccountingChanged` را در Fabric اتمیک ثبت می‌کند، اما usage را با **tariff جاری** قیمت می‌زند. `BillingCommand.UnitPrice` برای usage استفاده نمی‌شود. تنها مقدار عددی قیمت در ledger حفظ می‌شود؛ نسخهٔ نرخ/قرارداد در مدل فعلی نیست.
- `FabricDatabase.Outbox` خواندن batch دارد؛ dispatcher/ack/DLQ برای این store موجود فرض نمی‌شود. پیاده‌سازی EF outbox نئو، مرجع طراحی است و adapter Fabric آماده محسوب نمی‌شود.
- نمونهٔ جدید، append مبتنی بر snapshot را برای بازبینی داخل همان `BillingAccount` نشان می‌دهد. نوشتن مستقیم sample به ledger، API مجاز تولید نیست؛ باید داخل AccountingStore با invariantهای حساب ادغام شود.
- legacy charge و ۷۰/۳۰ فعال یا مصوب فرض نشده‌اند. `ownerRevenueShare` و `platformRevenueShare` در fixture برابر null هستند. جمع ۳۰ فقط gross مصرف consumer است و payable/payout مالک از آن نتیجه نمی‌شود.

[شواهد سورس و seamها](WIRING.md) این تفاوت‌ها را با lineهای فعلی مشخص می‌کند. هیچ microservice، database عملیاتی، broker، PDP، role، route یا limiter تازه‌ای اضافه نشده است. پایگاه‌های موقت harness صرفاً دادهٔ آزمون‌اند.

قرارداد و Decision متعلق به workstream سیاست‌اند. این بسته `contractVersion`، `allocationId`، `decisionId`، `policyVersion` و `admissionId` را reference می‌گیرد و projection یا تصمیم تازه‌ای تعریف نمی‌کند. بدون پاسخ چت دیگر و بدون برد/قرارداد امضاشده نیز fixture مستقل قابل اجرا است؛ اتصال زنده همچنان به منابع معتبر خودش نیاز دارد.


## اجرای جاری ۲۴ اکتبر ۲۰۲۶

شرح مأموریت جاری در [ثبت اجرا](EXECUTION-2026-10-24.md) و نتایج نهایی در [تحویل پذیرش](ACCEPTANCE-2026-10-24.md) است. بخش‌های تاریخ‌دار بالا، وضعیت همان تحویل تاریخی را شرح می‌دهند. schema نسخهٔ ۱، هر دو billingMode برابر payg و postpaid را برای snapshot معتبر می‌پذیرد. نمونهٔ مستقل و شاهد ۱۰ اکتبر همچنان پایلوت postpaid تاریخی‌اند؛ آزمون PAYG محصول و صورتحساب در بستهٔ جاری مستقل ثبت می‌شوند.
