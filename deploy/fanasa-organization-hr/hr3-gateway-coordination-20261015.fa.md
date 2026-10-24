# تحویل HR/PCC به Gateway

این تحویل، منابع موجود و شواهد خوانده‌شده را مشخص می‌کند. آزمونی دوباره اجرا، تماس remote برقرار یا پیکربندی عملیاتی تغییر داده نشده است. دو بستهٔ ثابت HR3 و PDP بازنویسی نشده‌اند.

| قرارداد اجرایی موجود | مقدار |
|---|---|
| profile / schema | `fanasa.organization-hr-read/1` / `3.0.0` |
| PDP | `POST /internal/contract-policy/hr-read/v3/evaluate`؛ opt-in و در میزبان زنده ثبت نشده |
| نسخه‌های transport | `fanasa.hr-read.pdp.request/1` و `fanasa.hr-read.pdp.response/1` |
| header سرویس / موضوع | `Authorization` / `X-Fanasa-Subject-Authorization`؛ دو bearer مستقل |
| audience محصول | `fanasa-organization-fabric` |
| scope و group خواندن | `organization.hr.read` |
| actionهای GET | `organization.hr.chart.read` و `organization.hr.history.read` |
| نقش / permission بومی | `HrReader` / `organization.read` |
| سقف PDP | request برابر ۳۲۷۶۸ و response برابر ۶۵۵۳۶ بایت |
| سقف دادهٔ HR | ۱۰۴۸۵۷۶ بایت؛ جدا از سقف پاسخ تصمیم PDP |
| freshness | حداکثر پنج ثانیه؛ evidence/token/گواهی/دوره ممکن است کوتاه‌تر کنند؛ عملیات همگام به‌اجبار preempt نمی‌شود |
| admission | پاسخ PDP همیشه `admissionConfirmed=false` دارد؛ Permit شامل دو obligation انجام‌نشده است |

این مقادیر قرارداد کد آزموده‌شده‌اند؛ وجودشان به معنی grant، delegation یا قرارداد تجاری مصوب نیست. [قرارداد کامل HTTP](hr-read-pdp-v3/WIRE-CONTRACT.fa.md)، [schemaها](hr-read-pdp-v3/schemas/README.md) و [منابع و شکاف‌های اتصال میزبان](hr-read-pdp-v3/LIVE-INTEGRATION-NEXT.fa.md) در بستهٔ موجود آمده‌اند.

ثبت مرکزی محصول در مشاهدهٔ خواندنی `2026-10-14T21:13:32.439333Z` از PVC موجود چنین بود: `id=b735c486-e711-49cb-bd62-0ce900ad557e`، `key=organization-fabric`، `audience=fanasa-organization-fabric`، `owner=platform`، `tenantId=null` و `center=fanasa.ayin`. [رسید همان مشاهده](hr-read-profile-v3/live-inspection.json) منبع این اطلاعات است؛ این تحویل آن را مشاهدهٔ تازه یا مالکیت tenant فرض نمی‌کند.

رسید Gateway با SHA-256 برابر `66c1a97ae7583893507528cba81be75724728f2160ee7799f4a85487ccbce4b7` خوانده و hash آن تطبیق داده شد. رسید، ۳۵ بررسی ورودی/lifecycle/schema، هفت پلاگین، دو service/route، هشت بردار crypto نصب‌شده، ۱۱ denial probe و metric مشاهده‌ای loopback را گزارش می‌کند. `syntheticOnly=true` و `operationalAcceptance=false` صریح‌اند. launch مربوط نیز همان receipt، ورودی‌ها و manifest PDP را pin می‌کند. این سند اجرای native را دوباره انجام نداده است.

bridge با رسید `4d18834c9a0c9ebde9c412e2a7c33c6298f85307f8ed99326793f1ec35c16c82`، ۱۰۵ بررسی و هشت زنجیرهٔ مثبت را ثبت کرده؛ audit آن با رسید `dedaf1355573f2a32809b63008b1fb7b2eff4ada90d26f74a1378e9ef4b4cc2b`، ۹۴ بررسی خواندنی و بدون rerun دارد. parser، service، evaluator و verifier واقعی مصرف شده‌اند؛ ticket/TLS feature/authority/admission مصنوعی‌اند. این نتیجه پذیرش HTTP/JWT/Redis عملیاتی نیست.

بازبینی مستقل خواندنی در این تحویل مغایرت hash یا provenance پیدا نکرد: هر ۱۱۱ فایل دو بستهٔ مالک، ۱۹ ورودی launch و گروه‌های before/after bridge با فایل‌های retained تطبیق داشتند. سه نام archive زیر `native-smoke-hr3/vectors/` عمداً از build ثابت bridge آمده‌اند؛ مسیر source آن‌ها در launcher مشخص و hash هر سه برابر بود. این بازبینی هیچ آزمون یا اتصال remote تازه‌ای اجرا نکرد.

ورودی‌های عملیاتی زیر از منابع تحویلی حاضر قابل ارائه نیستند و از fixture پر نمی‌شوند:

- mapping احرازشده و یکتای token به `consumerTenantId`؛ آخرین receipt introspection در `2026-10-14T21:45:04.712063Z`، `consumerTenantClaimPresent=false` و `gatewayConsumerMappingReady=false` دارد.
- delegation جاری Gateway و grant، contract/policy/allocation/projection و assignment مخصوص HR3؛ publisher فعال فعلی همچنان قرارداد فرمان v2 را مصرف می‌کند.
- registration زندهٔ endpointهای PDP و HR با adapterهای مرجع `IHrReadPdpHostRecords` و receiver authority، هویت سرویس/موضوع، mTLS/key/pin و current guards؛ پیش‌فرض بسته‌ها unavailable است.
- پذیرش Redis admission و مسیر مثبت HTTP تا PDP و HR واقعی؛ Collector عملیاتی با endpoint/auth/CA و پذیرش privacy/retention نیز از این سمت تحویل نشده است.

[رسید هماهنگی](hr3-gateway-coordination-20261015.json) hash منابع، محدودهٔ بررسی و وضعیت انتشار را جدا از بسته‌های ثابت نگه می‌دارد. انتقال کامل سورس و ساخت branch در GitLab از این چت انجام نشده است؛ رد بررسی خودکار و درخواست تأیید قبلی همچنان برقرارند. شواهد آزمون Gateway آن تصمیم را تغییر نمی‌دهند.
