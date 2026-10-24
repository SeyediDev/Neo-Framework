# مصرف نسخه‌دار سازمان و حسابداری

مسیر داخلی `Metering.UsageEventV1` و `MeasuredUsageStore` برای operation موجود
`organization-fabric / unit.save` پیاده‌سازی شده است. تنظیم
`UsageMetering:Organization:Enabled` پیش‌فرض false است. مسیر قدیمی
`Organization:MeterChanges` مستقل است؛ فعال‌کردن هم‌زمان این دو گزینه رد می‌شود.
عملیات دیگر سازمان در مسیر جدید charge ندارند.

## پیش‌نیاز و منبع اعتبار

فعال‌سازی به tenant مصرف‌کننده و عضو فعال، grant پایدار `organization.write`،
principal احراز‌شده با همان subject، یک اشتراک و entitlement فعال و پلن payg یا postpaid
سازگار نیاز دارد. محصول مرکزی باید `TenantId` مالک فعال داشته باشد؛ رشته `Owner`
یا tenant مصرف‌کننده جای آن قرار نمی‌گیرد. حساب مصرف‌کننده باید با مدل پرداخت همان پلن و snapshot، ارز
سازگار و موجودی یا اعتبار کافی باشد؛ حساب مالک بدهکار نمی‌شود.

مدل legacy قرارداد، نسخه و تاریخچه نرخ ندارد. حالت پیش‌فرض `configuration`
نسخه قرارداد، allocation و نرخ را از تنظیمات معتبر سرور می‌گیرد. حالت صریح
`contract-policy` از projection احراز‌شدهٔ موجود و قیمت نسخه‌دار Fabric استفاده
می‌کند؛ جزئیات اتصال در بخش بعد آمده است. هیچ‌کدام اعتبارسنجی امضای قرارداد
تجاری یا مصرف سهمیه را انجام نمی‌دهند. `authorization.decisionId` شاهد کنترل واقعی
مجوز محلی است و `admissionId` مرجع کنترل اعتبار در تراکنش ledger است. هیچ endpoint
عمومی برای ساخت این شاهد از JSON ورودی وجود ندارد. رویداد جدید wire بدون شاهد
داخلی معتبر در adapter محلی رد می‌شود.

## تنظیمات

نام‌ها در JSON با `:` و در متغیر محیطی با `__` جدا می‌شوند. مثال زیر ساختار لازم
را با placeholder نشان می‌دهد؛ فعال‌سازی و اطلاعات تجاری باید جداگانه فراهم شوند.

```json
{
  "UsageMetering": {
    "Organization": {
      "Enabled": false,
      "Environment": "pilot",
      "ProducerId": "<registered-backend-id>",
      "DeploymentId": "<deployment-id>",
      "Bindings": [
        {
          "ConsumerTenantId": "<consumer-uuid>",
          "ContractId": "<central-contract-id>",
          "ContractVersion": 1,
          "AllocationId": "<stable-allocation-id>",
          "AllocationPeriodId": "<allocation-period-id>",
          "PlanKey": "<active-postpaid-plan-key>",
          "PricingReference": "<rate-id>",
          "RateSnapshot": {
            "RateId": "<rate-id>",
            "RateVersion": 1,
            "EffectiveFrom": "<UTC-Z-start>",
            "EffectiveTo": "<UTC-Z-end>",
            "UnitPrice": "<exact-decimal-string>",
            "Currency": "IRR",
            "MinorUnitScale": 0,
            "Rounding": "away-from-zero",
            "BillingMode": "postpaid"
          }
        }
      ]
    }
  }
}
```

برای هر مصرف‌کننده، بازه‌های نرخ نباید هم‌پوشانی داشته باشند. هر rate ID/version
فقط یک snapshot دارد. USD/EUR scale=2 و IRR scale=0 دارند. نرخ با
`effectiveFrom <= acceptedAt < effectiveTo` انتخاب می‌شود. quantity فقط رشته `"1"`
و واحد `change` است؛ ضرب decimal دقیق و گردکردن AwayFromZero انجام می‌شود.

## قرارداد مرکزی و قیمت پایدار

با `UsageMetering:Organization:BindingSource=contract-policy`، `Bindings` خوانده
نمی‌شود و fallback به تعرفهٔ امروز وجود ندارد. `ContractPolicyStore.Find` باید
یک projection معتبر v2 با مالک مرکزی، وضعیت `Active`، entitlement
`organization.unit.save` و بازهٔ معتبر فعلی بدهد. allocation کامل باید با
`ReadAllocation` برابر باشد و محیط سرور و گروه `organization` را پوشش دهد.
قراردادِ دارای explicit deny برای این action رد می‌شود. این بررسی، جای ارزیابی
نقش/zone در PDP یا اجرای obligationهای Gateway را نمی‌گیرد.

شناسه‌های تجاری `SubscriptionId` و `OfferId` opaque هستند. تبدیل آن‌ها به GUID
یا کلید پلن انجام نمی‌شود. نگاشت مورد تأیید تجاری باید صریحاً روی سرور فراهم شود:

```json
{
  "UsageMetering": {
    "Organization": {
      "Enabled": false,
      "BindingSource": "contract-policy",
      "Environment": "pilot",
      "ProducerId": "<registered-backend-id>",
      "DeploymentId": "<deployment-id>",
      "ContractLinks": [{
        "ConsumerTenantId": "<consumer-uuid>",
        "ContractId": "<exact-commercial-contract-id>",
        "SubscriptionId": "<exact-commercial-subscription-id>",
        "OfferId": "<exact-commercial-offer-id>",
        "CentralSubscriptionId": "<existing-central-subscription-uuid>",
        "CentralPricingPlanId": "<existing-central-plan-uuid>"
      }]
    }
  }
}
```

اشتراکِ متصل باید متعلق به همان consumer/product و همان پلن فعال با مدل پرداخت برابر snapshot باشد.
در این حالت، entitlement از projection می‌آید؛ entitlement مستقل legacy منبع
دوم تصمیم نیست. عضویت، grant پایدار و principal فعلی همچنان بررسی می‌شوند.

`OrganizationUsagePriceStore` مبلغ دقیق و snapshot کامل را در
`organization-usage.price` ذخیره می‌کند. API مدیریتی
`POST /api/usage-accounting/organization/prices` فقط با Cookie، policy موجود
`PlatformAdmin` و antiforgery پذیرفته می‌شود. ورودی `PriceId`، `PriceVersion`،
`RateSnapshot` و `Reason` دارد؛ مالک و meter را سرور از رجیستری و پروفایل ثابت
می‌گیرد. ID/version snapshot باید با price reference برابر باشد. انتشار دوبارهٔ
همان محتوا idempotent است؛ تغییر همان نسخه تعارض است. برای تغییر مبلغ نسخهٔ
تازه منتشر و reference قرارداد به‌روزرسانی می‌شود. هیچ قیمت پیش‌فرضی ساخته
نمی‌شود. نسخه‌های مستقل می‌توانند بازهٔ مشترک داشته باشند چون انتخاب فقط با
reference دقیق انجام می‌شود. `GET` همان مسیر با queryهای `priceId` و
`priceVersion` نسخهٔ منتشرشده را برای مدیر می‌خواند.

نسخه‌های long قرارداد/قیمت که از `int.MaxValue` بیشتر باشند، در UsageEvent 1.0
رد می‌شوند. `allocationPeriodId` از ماه UTC زمان پذیرش (`yyyy-MM`) گرفته می‌شود؛
meter سهمیهٔ `api.request.admitted / request` با meter مالی
`organization.change / change` یکی فرض نمی‌شود. خواندن allocation سهمیه را مصرف
نمی‌کند. replay به دسترسی فعلی قرارداد/اشتراک نیاز دارد ولی قیمت تاریخی را
دوباره انتخاب نمی‌کند؛ قیمت منقضی‌شده پاسخ قبلی را reprice نمی‌کند.

## تراکنش، retry و دوره مالی

`RequestId` برای unit.save فعال باید UUID پایدار باشد. receipt کسب‌وکار قبل از
انتخاب دوباره نرخ بررسی می‌شود، ولی دسترسی فعلی caller برای دریافت پاسخ کنترل
می‌شود. همان درخواست پاسخ تاریخی می‌گیرد؛ تغییر actor یا payload تعارض است.
در مسیر فعال، خواندن پاسخ تاریخی نیز به grant، عضویت، اشتراک، پلن و entitlement
فعالِ امروز نیاز دارد؛ نرخ و allocation تاریخی برای پاسخ دوباره انتخاب نمی‌شوند.
کلید `measured:v1:` از هویت ثابت operation ساخته و در API و AccountingStore برای
typed intake رزرو شده است. نسخه قرارداد و نرخ، هویت مصرف را عوض نمی‌کنند.

`measured.operation` رویداد اولیه و snapshot کامل را نگه می‌دارد؛
`measured.event` receipt هر delivery را با fingerprint نگه می‌دارد. delivery تازه
برای همان operation همسان، alias می‌شود. تغییر evidence یا snapshot در همان
هویت رد می‌شود. receipt مالی، TTL ترافیکی ندارد.

در مسیر محلی، هر شکست حسابداری کل transaction سازمان/history/receipt/ledger/outbox
را برمی‌گرداند. `Accept(event, retainPending: true)` فقط seam داخلی آینده برای
رویداد completed از منبع بیرونی معتبر است. شکست مالی آن، شاهد اصلی را با وضعیت
`accounting-pending` حفظ می‌کند؛ retry فقط append حسابداری را اجرا می‌کند و backend
را اجرا نمی‌کند. validator واقعی remote و endpoint دریافت آن هنوز فراهم نشده‌اند.
حساب مصرف‌کننده غیرفعال/ناموجود نیز evidence را در مسیر trusted remote به pending
می‌برد؛ در این وضعیت receipt بدون AccountView بازمی‌گردد.

`LedgerEntry.At` زمان ثبت حسابداری است و `UsageOccurredAt` زمان نتیجه کسب‌وکار.
صورتحساب مصرف جدید را با UsageOccurredAt گروه‌بندی می‌کند؛ legacy با At
محاسبه می‌شود. مصرف مربوط به ماه صورتحساب صادرشده pending/adjustment می‌شود و
صورتحساب را خودکار تغییر نمی‌دهد. دوره allocation مستقل از ماه invoice است.

outbox از همان Fabric استفاده می‌کند. `UsageCommitted` شامل `TenantId`، `LedgerKey`،
`Status`، `Amount` و شیء `Usage` کامل با wire camelCase است. pending اولیه
`UsageAccountingPending` می‌سازد؛ alias و replay مالی رویداد committed تازه ندارند.
dispatcher و acknowledgement در بخش بعد پیاده‌سازی شده‌اند. انتقال خودکار به
DLQ، تسویه مالک، مالیات و اعتبارسنجی امضای قرارداد تجاری فراهم نشده‌اند.

## تطبیق و retry حسابداری

`GET /api/usage-accounting/organization/{consumer}/reconciliation?maxRows=10000`
با Cookie و policy موجود `PlatformAdmin`، شواهد همان مصرف‌کننده را در snapshot
خواندنی SQLite تطبیق می‌دهد. کلید فیزیکی سند، fingerprint رویداد و operation،
receipt اصلی و aliasها، charge و marker دفتر، مبلغ/نرخ/actor/زمان وقوع و envelope
خروجی بررسی می‌شوند. گزارش به قیمت یا قرارداد امروز مراجعه نمی‌کند و هیچ حساب،
charge، receipt، cursor یا رویدادی نمی‌سازد. snapshot در transaction ناتمام قابل
اجرا نیست و خود اتصال SQLite نوشتن را رد می‌کند.

`Coverage` برابر `complete` یا `incomplete` و `Status` برابر `clean`، `issues` یا
`incomplete` است. pending سالم نیز finding با کد `accounting-pending` دارد تا نیاز
به اقدام حسابداری دیده شود. Findings کد ثابت و هویت رکوردهای همین مصرف‌کننده را
دارند؛ payload، token و متن خطای ذخیره‌شده بازگردانده نمی‌شود. سند خرابِ غیرقابل
انتساب، شمارش و پوشش ناقص ایجاد می‌کند. scan در صفحه‌های ۱۰۰تایی انجام می‌شود؛
`maxRows` بین ۱ و ۱۰۰۰۰، حد مستقل هر جریان سند/outbox است.
این حد روی جریان مشترک پایگاه اعمال می‌شود؛ رکوردهای دیگر مصرف‌کننده‌ها و رویدادهای
غیرمصرف نیز سهمی از scan دارند، هرچند اطلاعاتشان در گزارش tenant برگردانده نمی‌شود.
عبور از حد، نتیجهٔ پاک نمی‌دهد و نبود رکورد خارج از scan را اثبات نمی‌کند.
این گزارش سازگاری شواهد
محلی را می‌سنجد؛ صحت امضای تجاری یا تکمیل اثر در مقصد بیرونی را ثابت نمی‌کند.
ردیف‌های دفتر حساب نیز مشمول همین حد هستند. `Operations` خلاصهٔ قابل انتخاب مصرف
با LedgerKey، OperationFingerprint، وضعیت، مبلغ، ارز و زمان وقوع را می‌دهد؛ مدیر
fingerprint دستور retry را از همین گزارش کامل و سازگار برمی‌دارد.

`POST /api/usage-accounting/organization/{consumer}/recover` با همان Cookie/policy،
antiforgery و سقف body برابر ۱۶ KiB، فقط operation قبلاً معتبر و پایدارشده را retry
می‌کند. ورودی:

```json
{
  "businessKey": "measured:v1:<64-lowercase-hex-digest>",
  "expectedOperationFingerprint": "<64-lowercase-hex-digest>",
  "requestId": "<recovery-request-uuid>",
  "reason": "<accounting-review-reason>"
}
```

سرور actor را از subject نشست می‌گیرد. JSON رویداد، نرخ تازه، Permit یا شاهد
invocation در این API ورودی نیست. پیش از retry، همان transaction شواهد مصرف‌کننده
را تطبیق می‌دهد؛ پوشش ناقص یا اختلاف، هیچ اثر تازه‌ای ثبت نمی‌کند. دفتر و outbox
ناسازگار باید ابتدا بررسی شوند؛ این مسیر skip، حذف یا اصلاح خودکار مبلغ ندارد.
retry از EventJson اصلی استفاده می‌کند، validator پذیرش جدید یا backend را دوباره
اجرا نمی‌کند و تعرفهٔ فعلی را جایگزین نمی‌کند. شکست عادی حسابداری همچنان
`accounting-pending` باقی می‌ماند؛ نبود حساب، اعتبار ناکافی یا دورهٔ invoice صادرشده
از این API override نمی‌شود. operation از قبل committed، charge تازه نمی‌سازد.

هر اقدام با actor، دلیل، requestId، fingerprint، وضعیت پیش/پس و مبلغ در
`usage.accounting.recovery` و `UsageAccountingRecoveryAttempted` همان Fabric ثبت
می‌شود. receipt اقدام تاریخی است: retry دقیق همان requestId و محتوا، همان نتیجهٔ
قبلی را بدون audit تازه برمی‌گرداند. پس از رفع مشکل حساب، برای تلاش تازه requestId
تازه لازم است؛ تغییر هدف/actor/دلیل در requestId قبلی تعارض است. این عملیات،
صورتحساب صادرشده را تغییر نمی‌دهد و adjustment یا تسویه ایجاد نمی‌کند.

### پنل مدیریتی

صفحهٔ `/UsageAccounting?tenant=<uuid>` از منوی مدیر پلتفرم و صفحهٔ حسابداری قابل
دسترسی است و همان Cookie و `PlatformAdmin` را لازم دارد. سازمان‌های فعال از منبع
مرکزی انتخاب می‌شوند؛ گزارش، شمارش ثبت‌شده/درانتظار، سلامت شواهد، صف محلی، جست‌وجو
و فیلتر وضعیت دارد. جزئیات مصرف مبلغ و ارز محفوظ، زمان، شناسه و fingerprint را
نمایش می‌دهد. جزئیات نرخ واحد در این API نیست و در رابط ساخته نمی‌شود.

مبلغ JSON پیش از تبدیل به JavaScript Number به رشتهٔ دقیق تبدیل می‌شود؛ نمایش و
کپی مبلغ‌های بزرگ/اعشاری رقم از دست نمی‌دهند. درخواست تازه فقط با گزارش کامل،
سلامت تأییدشده، انتخاب همان operation و دلیل صریح فعال است. پس از پنج دقیقه یا
شکست دریافت گزارش، اقدام تازه تا به‌روزرسانی غیرفعال می‌شود. گزارش موجودِ قدیمی
با برچسب وضعیت نمایش می‌ماند.

پاسخ نامشخص، timeout، redirect، receipt ناسازگار یا خطای سرور، شناسه و متن همان
درخواست را حفظ می‌کند. تغییر سازمان/هدف/دلیل هنگام ارسال یا نتیجهٔ نامشخص در
کنترل‌های صفحه مسدود است؛ درخواست در `sessionStorage` همان tab و سازمان حفظ
می‌شود و پس از reload، فقط با انتخاب صریح مدیر دوباره ارسال می‌شود. این storage
جایگزین receipt سرور یا تضمین حفظ داده پس از بسته‌شدن tab/پاک‌شدن storage نیست.
نتیجهٔ معتبر pending تاریخی است؛ پس از رفع محدودیت و گزارش تازه، دکمهٔ
«آماده‌سازی درخواست جدید» برای UUID تازه استفاده می‌شود.

پیش‌نمایش موقت `Organization.Tests.dll --preview` در
`http://127.0.0.1:5198/UsageAccounting`، هویت مدیریتی مصنوعی و ۲۷ مصرف نمونه دارد:
۲۴ ثبت مالی کامل و ۳ در انتظار، هر کدام ۱۲۰۰۰۰ IRR. داده‌ها در مسیر موقت نگهداری
می‌شوند و اسمبلی محصول به این fixture وابسته نیست. این پیش‌نمایش SSO واقعی نیست.
آزمون‌های Node در `tests/usage-accounting-ui.test.cjs` و بررسی مرورگر موجود در
`tests/usage-accounting-browser-check.cjs` رفتار دقت مبلغ و retry را بررسی می‌کنند؛
دومی فقط به همین preview loopback متصل می‌شود و Playwright/browser موجود می‌خواهد.

دریافت تازهٔ UsageEvent از producer بیرونی هنوز فراهم نیست. publication تاریخی
Gateway برخی snapshotهای قرارداد را حفظ می‌کند، ولی UsageEvent به event انتشار
متصل نیست و تصمیم/admission کامل Gateway آرشیو پایدار قابل تطبیق ندارد. ثبت یک
ProductClient یا وجود decisionId در JSON، مجوز producer مالی نمی‌سازد. گیرندهٔ
واقعی به binding مورد تأیید workload و شاهد معتبر پذیرش تاریخی نیاز دارد.

## تحویل outbox

`UsageOutboxDispatcher` فقط `UsageCommitted` را برای یک destination/consumer
پیکربندی‌شده می‌فرستد. journal، receipt و charge پیش از ارسال و پیش از ثبت ack
بررسی می‌شوند. cursor در `usage.outbox.cursor` به endpoint، consumer و هویت
پایدار DB متصل است. تلاش ارسال پیش از شبکه ذخیره می‌شود؛ lease و fence مانع
پیش‌روی worker قدیمی می‌شوند. هیچ await در تراکنش Fabric وجود ندارد؛ اجرای
dispatcher داخل تراکنش محیطی صریحاً رد می‌شود.

worker پیش‌فرض خاموش است. نمونهٔ ساختار تنظیمات:

```json
{
  "UsageMetering": {
    "Outbox": {
      "Enabled": false,
      "DestinationId": "<trusted-destination-id>",
      "Endpoint": "https://<trusted-host>/<usage-intake-path>",
      "ConsumerTenantId": "<consumer-uuid>",
      "LeaseSeconds": 60,
      "TimeoutSeconds": 20,
      "RetrySeconds": 30,
      "PollSeconds": 10,
      "BatchSize": 100
    }
  }
}
```

credential سرور با `UsageMetering__Outbox__BearerToken` از secret configuration
تأمین می‌شود؛ در Fabric یا پیام مصرف ذخیره نمی‌شود. endpoint فقط HTTPS و بدون
userinfo/query/fragment است؛ redirect غیرفعال است. timeout کل headers/body را
پوشش می‌دهد. lease از timeout حداقل پنج ثانیه بیشتر است؛ lease و retry حداکثر
۳۰۰ ثانیه، batch حداکثر ۱۰۰ و ack حداکثر ۱۶ KiB است.

HTTP POST شامل wire اصلی UsageEvent و header ثابت `Idempotency-Key` است. پاسخ
دقیقاً این پنج رشتهٔ camelCase را دارد: `destinationId`، `ledgerKey`، `eventId`،
`eventFingerprint` و `disposition` (`accepted` یا `duplicate`). دریافت‌کننده باید
پیش از ack اثر خود و receipt تکرار را پایدار کند و تمام هویت‌ها را برگرداند.
هویت delivery از destination/event/business/fingerprint ساخته می‌شود و به ID
محلی outbox وابسته نیست. دریافت‌کنندهٔ واقعی هنوز متصل یا آزمایش نشده است.

خطا یا ack ناسازگار cursor را جلو نمی‌برد؛ همان event با backoff تا ۳۰۰ ثانیه
دوباره ارسال می‌شود. ردیف خراب پیش‌روی را متوقف و کد ثابت تشخیصی ذخیره می‌کند؛
skip مالی، حذف outbox و poison bypass خودکار وجود ندارد. ردیف‌های معتبر tenant
دیگر و رویدادهای غیرمصرف بدون ارسال اسکن می‌شوند. برای هر consumer/destination
cursor مستقل لازم است؛ worker فعلی یک subscription پیکربندی‌شده دارد.

restore منسجم DB قدیمی، رویدادهای قبلاً دریافت‌شده را دوباره می‌فرستد؛ مقصد باید
receiptها را حفظ کند. cursor با key/hash آخرین ردیف، restore نامنسجم را رد می‌کند.
ردیف anchor و تلاش جاری نباید حذف شوند. backup قدیمی رویدادهای پس از backup را
بازیابی نمی‌کند؛ آن‌ها به replay معتبر یا reconciliation نیاز دارند. این مسیر
تضمین RPO صفر، exactly-once شبکه یا آزمون قطع برق نیست.

## بررسی

[شواهد اجرای محلی](../../../docs/fanasa-platform-control-center/workstreams/usage-accounting/RUNTIME-EVIDENCE.md)
از شواهد تاریخی prototype مستقل است.

```powershell
dotnet run --project products/Fanasa.AccessManagement/tests/Organization.Tests -c Release -p:NuGetAudit=false
```

آزمون‌ها Fabric واقعی، نرخ تاریخی، replay و alias، credit race، rollback، pending،
دوره صادرشده، parser سخت‌گیر، نگاشت سروری و backup/restore را بررسی می‌کنند.
`NuGetAudit=false` فقط محدودیت اتصال شبکه محیط آزمون را دور می‌زند و تنظیم محصول
را تغییر نمی‌دهد. بازیابی offline همان [راهنمای بازیابی](RECOVERY.md) است؛ تضمین
commit محلی به معنی بازیابی تراکنش‌های پس از آخرین backup نیست.

برای build ترتیبی، ثبت log نسخهٔ ۳ و pin کردن سورس/باینری پس از موفقیت، از root
مخزن اجرا کنید (بسته‌ها باید از قبل restore شده باشند):

```powershell
& ./docs/fanasa-platform-control-center/workstreams/usage-accounting/verify-runtime.ps1
```

اسکریپت اگر ورودی‌های runtime هنگام اجرا تغییر کنند، baseline را به‌روز نمی‌کند.
manifest نسخه‌های ۱ و ۲ و شواهد prototype تاریخی حفظ می‌شوند. Skill/MCPهای Neo موجود
این API محصول را مصرف نمی‌کنند و قراردادشان در این مرحله تغییر نکرده است.


## تکمیل محلی PAYG و Billing — ۲۴ اکتبر ۲۰۲۶

شرح رفتار، APIهای نسخه‌دار، صورتحساب ثابت، اصلاح اعتباری و گام‌های پذیرش در [راهنمای مالی](BILLING-LIFECYCLE.md) آمده است. انتشار، درگاه واقعی و فعال‌سازی feature از موفقیت آزمون محلی نتیجه نمی‌شود.
