> وضعیت نهایی‌تر و نتیجهٔ997 بررسی در [تحویل۲۳اکتبر](portal-central-readers-2026-10-23.md) آمده است. مطالب زمان‌دار این فایل شواهد تاریخی‌اند.

# اتصال API مرکزی به پنجره واحد

تاریخ محلی: ۱۵ اکتبر ۲۰۲۶. این تحویل ادامهٔ کار مستقل مرکز راهبری است؛ وضعیت سورس، آزمون محلی و نسخهٔ مستقر جدا ثبت می‌شوند.

## شاهد نسخهٔ مستقر

بررسی فقط‌خواندنی در `2026-10-14T20:57:56.943480Z` با hostname/SNI دقیق، CA موجود و اتصال loopback روی VPS انجام شد. این بررسی، دسترسی DNS از رایانهٔ مصرف‌کننده را اثبات نمی‌کند.

نسخهٔ مشاهده‌شده: `hr-development-597dab599177-b841fabc1c3c`.

| مسیر روی `https://platform.fanasa.net.local` | نتیجهٔ درخواست بدون توکن |
| --- | --- |
| `/health/live` | `200` |
| `/api/platform/products` | `401` |
| `/api/platform/published-services` | `404` |
| `/api/platform/application-tenants` | `404` |
| `/api/accounting/v1/consumers/{consumerTenantId}/usage-report?from=...&to=...` | `404` |

مرزهای دوره در probe گزارش مصرف UTC و tenant نمونهٔ موجود بودند؛ هیچ دادهٔ مالی، محصول، مجوز یا تنظیم عملیاتی تغییر نکرد. در نسخهٔ مشاهده‌شدهٔ۱۴اکتبر این endpointها وجود نداشتند؛ این مشاهده وضعیت امروز سرور را اثبات نمی‌کند.

سند تاریخی اتصال Portal در ۱۰ اکتبر موفقیت `application-tenants` روی نسخهٔ آن زمان را ثبت کرده بود. پاسخ `404` روی نسخهٔ مشاهده‌شدهٔ۱۴اکتبر، آن نتیجه را به وضعیت فعلی تبدیل نمی‌کند. سازگاری فهرست سازمان و محصولات مجاز consumer باید جدا با artifact فعلی اصلاح و پذیرفته شود؛ endpoint آن نسخهٔ `products` سیاست توسعه‌دهنده دارد و پارامتر `tenant` را برای مصرف‌کننده اعمال نمی‌کند. کاتالوگ معرفی سرویس جایگزین مجوز استفاده از محصول نیست.

## UI و UX

نمایش مستقل گزارش مصرف ۲۳ آزمون موفق و آزمون‌های Workspace، کارت کاتالوگ و معماری ۲۵ آزمون موفق داشتند. پس از اصلاح متن نقشهٔ معماری مرکز، دو آزمون همان صفحه دوباره موفق شدند. متن تازه، شاهد محدود HR در محیط توسعه را از پذیرش کامل قرارداد، PDP و سهمیهٔ مشترک جدا می‌کند. لینک درگاه در نقشه به راهنمای موجود `/OrganizationHr` می‌رود.

Portal مالک `WorkspaceJourney.cs`، صفحه و مدل Workspace، `workspace.js` و wiring آن است. مدل قدیمی مصرف باید با DTO گزارش نسخه‌دار تطبیق داده شود: مبلغ و quantity رشته، unit/version قدیمی null، تعداد سرویس بر اساس productKey متمایز، بدون سهمیهٔ فرضی، دورهٔ واقعی و پاک‌شدن دادهٔ مالی در خطا یا تغییر انتخاب. بستهٔ آمادهٔ mapper/renderer در `usage-accounting/consumer-report/portal-adapter/` قرار دارد. خطای خواندن مصرف نباید فهرست خدمات مجاز را پاک کند.

## قرارداد و مرزهای اتصال

کاتالوگ منتشرشده از همان رجیستری مرکزی استفاده می‌کند. ثبت محصول یا مقدار دلخواه در `Attributes` مجوز انتشار نیست. هیچ URL رسمی معرفی، درخواست دسترسی یا خرید از URL ورود و لینک Kanban استخراج نمی‌شود؛ لینک ثبت‌نشده null می‌ماند. انتشار صریح باید قابل حسابرسی و لغو باشد.

گزارش مصرف فقط از همان Fabric و دفتر ثبت‌شده خوانده می‌شود؛ بانک یا schema تازه، تغییر نرخ، debit، settlement یا grant ایجاد نمی‌کند. `platform.catalog` مجوز مالی نیست. API باید توکن انسانی با audience مرکزی، client معتبر Portal، عضویت فعال و `billing.read` جاری را بررسی کند؛ subject ورودی فرم یا header اثبات هویت نیست.

پیاده‌سازی محلی تکمیل و نتایج نهایی در ادامه ثبت شد. استقرار و اتصال عملیاتی همچنان جدا هستند.

## تحویل محلی نهایی

منبع اصلی `ConsumerUsageReport.cs` به اسمبلی Web مرکز منتقل شد؛ دو harness گزارش و mapper اکنون به همان اسمبلی ارجاع می‌دهند. سه بخش تازه در سورس ثبت و ساخته شدند. تغییر schema یا بانک مستقل لازم نشد. هیچ فایل Portal توسط این تحویل تغییر نکرد.

| سطح | قرارداد |
| --- | --- |
| معرفی سرویس | `GET /api/platform/published-services` با client سرویس مشخص و scope موجود `platform.catalog` |
| ادارهٔ انتشار | `GET/PUT /api/platform/service-publications/{productId}` با cookie مدیر؛ PUT نیازمند antiforgery و expectedRevision |
| مصرف سازمان | `GET /api/accounting/v1/consumers/{consumerTenantId}/usage-report?from=...&to=...` با توکن انسانی Portal و مجوز `billing.read` |
| خوانندهٔ Portal | `ConsumerUsageHttpReader` مستقل و فقط سمت سرور؛ provider اعتبارنامه هنوز باید توسط مالک Portal وصل و پذیرفته شود |

[قرارداد OpenAPI](../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/wwwroot/contracts/portal-readers-v1.openapi.json) هر سه مسیر را توصیف می‌کند؛ این قرارداد، گواه استقرار نیست.

### تنظیمات صریح

| کلید | مقدار/شرط |
| --- | --- |
| `Platform:ServiceCatalog:ClientId` | client ثبت‌شدهٔ سرویس Portal؛ client شناخته‌شدهٔ قبلی `fanasa-unified-portal-service` است |
| `Platform:ServiceCatalog:ClientProductKey` | کلید واقعی محصول مالک client؛ پیش‌فرض ندارد |
| `Platform:ServiceCatalog:OfficialLinkHosts` | hostهای دقیق جریان معرفی/درخواست مصوب؛ خالی یعنی URLها null |
| `ConsumerUsageReport:Enabled` | پیش‌فرض false |
| `ConsumerUsageReport:PortalProductKey` | محصول واقعی ثبت‌شدهٔ Portal با tenant مالک فعال |
| `ConsumerUsageReport:PortalClientId` | client دقیق انسانی Portal؛ یکتا، فعال و دارای scope ثبت‌شده |
| `ConsumerUsageReport:Scope` | scope انسانی موجود و مصوب؛ این تحویل scope یا grant نمی‌سازد |

هر دو reader توکن امضاشدهٔ audience مرکزی را از scheme موجود `PlatformBearer` می‌گیرند؛ expiry و nbf بدون ارفاق زمانی نیز در action بررسی می‌شوند. گزارش، subject را از کاربر احرازشده می‌گیرد و پارامتر subject، header جعل هویت، query ناشناخته/تکراری و maxRows ورودی را نمی‌پذیرد. tenant مصرف‌کننده، عضویت، tenant مالک Portal، client ثبت‌شده و grant زنده بررسی می‌شوند. مجوز داخل snapshot و پس از پایان آن دوباره بررسی می‌شود؛ بررسی پس از snapshot برای مشاهدهٔ لغو ثبت‌شده هنگام projection است و تضمین تراکنش توزیع‌شده ایجاد نمی‌کند.

publication همان رجیستری مرکزی را مرجع محصول نگه می‌دارد و در Fabric موجود ذخیره می‌شود. تغییر و audit/outbox در یک تراکنش‌اند؛ خطای audit کل تغییر را rollback می‌کند. رجیستری محصول بانک مجزای قبلی است؛ برای تغییر هم‌زمان رجیستری و publication ادعای atomicity توزیع‌شده نداریم. ثبت محصول، Attributes دلخواه، لینک ورود یا Kanban به انتشار یا لینک خرید تبدیل نمی‌شوند.

### پاسخ و محدودیت خواندن

بازهٔ HTTP و overload محافظت‌شده UTC نیمه‌باز و کوتاه‌تر از ۳۶۶ روز است. overload قبلی store، برای سازگاری، همچنان دقیقاً ۳۶۶ روز را می‌پذیرد. گزارش حداکثر ۱۰٬۰۰۰ ردیف و ۸ MiB سند حساب را می‌خواند؛ مبلغ و quantity رشتهٔ دقیق‌اند. quantity/unit/version نامعلوم legacy برابر null می‌مانند. فقط ledger ثبت‌شده مبنای هزینه است؛ ledger یا raw evidence، actor و ReasonCode داخلی در DTO HTTP نیستند.

نمونهٔ زیر صرفاً fixture مصنوعی حساب ثبت‌شدهٔ خالی است؛ مبلغ واقعی یا سازمان عملیاتی را نشان نمی‌دهد:

```json
{"subject":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","report":{"consumerTenantId":"11111111-1111-1111-1111-111111111111","from":"2026-10-01T00:00:00Z","to":"2026-11-01T00:00:00Z","asOf":"2026-10-15T00:00:00Z","status":"ready","basis":"posted-ledger","currency":"USD","totalAmount":"0","items":[]}}
```

حساب ثبت‌نشده با `notRecorded` و مبلغ/currency برابر null و items خالی پاسخ می‌دهد؛ این وضعیت صفر موفق نیست. خوانندهٔ Portal در خطا یا حالت غیرآماده، دادهٔ مالی و asOf را پاک می‌کند. خواننده فقط یک GET به origin مشخص HTTPS443 می‌فرستد؛ redirect، cookie، fallback سرویس و retry احراز هویت ندارد. حجم پاسخ، ساختار JSON، subject، consumer، دوره، basis، تعداد و جمع دقیق مبلغ کنترل می‌شوند. سقف timeout شصت ثانیه است؛ لغو caller حفظ می‌شود.

پاسخ‌هایی که به controller احرازشده می‌رسند `Cache-Control: no-store` دارند. challenge توکن گم‌شده/نامعتبر پیش از action رخ می‌دهد؛ no-store صریح روی آن 401ها در این تحویل تضمین نشده است.

### نتیجهٔ آزمون ۱۵ اکتبر

| مجموعه | نتیجه |
| --- | --- |
| کاتالوگ و publication روی Kestrel/Fabric | ۵۲ بررسی موفق |
| API گزارش روی Kestrel، JWT امضاشدهٔ RSA و Fabric | ۱۰۶ بررسی موفق |
| گزارش canonical و شاهد measured/ledger | ۱۴۵ بررسی موفق |
| mapper نمایش Portal | ۱۱۶ بررسی موفق |
| انتقال HTTP مستقل Portal | ۵۷ سناریو، ۳۴۲ بررسی موفق |
| جمع پنج harness | ۷۶۱ بررسی موفق |

ساخت Release میزبان مرکزی و پنج پروژهٔ آزمون با صفر هشدار/خطا موفق شد. contract JSON parse شد و diffهای این تحویل خطای whitespace نداشتند. restore به cache محلی و source آفلاین محدود بود؛ `NuGetAudit=false` فقط برای این اجرای محلی بود و معادل audit آسیب‌پذیری یا تغییر سیاست CI نیست.

harness کاتالوگ از authentication مصنوعی استفاده می‌کند و آزمون امضای واقعی JWT نیست. harness API امضا/issuer/audience و زمان JWT را آزمایش می‌کند، ولی کل میزبانی عملیاتی و `FabricClaimsTransformation` را اجرا نمی‌کند. بنابراین پاسخ 503 آزمون‌شده، همهٔ خطاهای storage پیش از controller یا SSO واقعی را پوشش نمی‌دهد. `AccessBoundaryFilter` فقط AccessApi قدیمی را هدف می‌گیرد و به controllerهای جدید اعمال نمی‌شود.

### کارهای لازم برای اتصال عملیاتی

- پاسخ404 مربوط به probe۱۴اکتبر است؛ یادداشت استقرار۱۷اکتبر release جدیدی را ثبت کرده است.
- مالک Portal باید provider توکن انسانی را با نشست واقعی، audience مرکزی و رفتار معتبر `sid/auth_time` در realm تأیید کند. وضعیت فعلی SaveTokens=false و نبود credential downstream با reader جدید به‌تنهایی رفع نمی‌شود.
- wiring Workspace، DTO نسخه‌دار و mapper/renderer بر عهدهٔ چت مالک Portal است. خطای مصرف باید فهرست خدمات مجاز را حفظ کند.
- سازگاری مستقل `application-tenants` و محصولات مجاز consumer همچنان نیازمند اصلاح/پذیرش است؛ published-services جایگزین entitlement نیست.
- URL رسمی معرفی/درخواست/خرید هنوز در registration موجود تثبیت نشده است؛ URL ساختگی وارد نشد.
- ایمیل اعلام‌شده قبلاً به tenant `fanasa-internal` مرتبط شده است؛ برای HR عملیاتی نام سامانه و client سرویس لازم است. هویت HR توسعه فقط در `hr-development` محدود است.
- پذیرش کامل قرارداد، PDP، سهمیهٔ مشترک و مسیر مالی تجاری همچنان مستقل است.

شاهد probe فقط‌خواندنی در [فایل runtime](portal-central-readers-runtime-2026-10-15.json) حفظ شده است. خروجی‌های ساخت و helperهای موقت متعلق به همین تحویل، پس از ثبت شواهد پاک می‌شوند؛ منابع، آزمون‌ها و شواهد تاریخی چت‌های دیگر حفظ می‌شوند.


## ادامهٔ بازبینی ۱۶ اکتبر

بازبینی مستقل، اشکال چرخهٔ عمر لغو انتشار را یافت: اعتبارسنجی tenant مالک یا host رسمی فعلی می‌توانست لغو انتشار قدیمی را مسدود کند. سورس اصلاح شد تا withdrawal از هویت/revision قبلی استفاده کند، لینک‌ها را پاک کند و همان audit اتمیک را حفظ کند. خواندن مدیر، لینک قدیمی با ساختار HTTPS معتبر را برای بررسی می‌پذیرد؛ کاتالوگ عمومی برای publication فعال همچنان host مصوب جاری را الزام می‌کند. ۱۱ بررسی تازه برای مالک غیرفعال و host حذف‌شده اضافه شده‌اند؛ نتیجهٔ ساخت/اجرای پس از این اصلاح جدا ثبت خواهد شد.

سازگاری خواندن application-tenants و محصولات consumer نیز در حال تکمیل است. تاریخچهٔ Git نشان داد ApplicationTenancy.cs در ef86d98 اضافه و در snapshot e29a3be حذف شده بود. مجوز PlatformService فعلی فقط client توسعه‌دهنده با platform.registry را می‌پذیرد. اصلاح باید platform.catalog را در مسیر خواندن consumer با client دقیق حفظ کند و سیاست توسعه‌دهنده روی عملیات ثبت و authorize باقی بماند. هیچ schema، offer، grant یا اشتراک عملیاتی در این ادامه ساخته نمی‌شود.

دو خروجی موقت تأییدشدهٔ همین کار، catalog-reader-20261015 و portal-transport-20261015، با مجموع ۸۶٬۴۴۸٬۶۲۲ بایت حذف و نبودشان تأیید شد. خروجی ساخت مشترک آزمون‌های مرکزی تا پایان بررسی سازگاری حفظ می‌شود.
