# اتصال خواننده‌های مرکزی — ۱۵ اکتبر ۲۰۲۶

## انجام‌شده در پنجره واحد

- مسیر آزمون‌های discovery و redirect با قرارداد واقعی
  `GET /api/platform/published-services` هماهنگ شد؛ scope همان `platform.catalog`
  است. `DiscoveryPath` عملیاتی عمداً بدون تأیید اتصال مرکزی فعال نشد.
- `ConsumerUsageReportProjection`، JSON قرارداد نسخهٔ ۱ مصرف را مستقل از اسمبلی
  Web مرکزی اعتبارسنجی و به DTO محدود مرورگر تبدیل می‌کند. ورودی از فرم یا
  `subject` دلخواه، اثبات نمایندگی انسان نیست؛ این کلاس هیچ مجوزی صادر نمی‌کند.
- subject، سازمان مصرف‌کننده، هر دو مرز دقیق UTC و basis باید تطبیق کنند.
  بازه نیمه‌باز `[from,to)` و کوتاه‌تر از ۳۶۶ روز است. پاسخ ناسازگار، ناقص،
  تکراری، بزرگ‌تر از ۸MiB یا بیش از ۱۰هزار ردیف به `unavailable` خالی تبدیل می‌شود.
- مبلغ و quantity رشتهٔ دقیق می‌مانند؛ مجموع ردیف‌ها و دقت ارز کنترل می‌شود.
  واحد/نسخه/مقدار قدیمی null می‌ماند. تعداد خدمات از productKey متمایز است، نه
  تعداد مترها. `notRecorded`، `incomplete` و `unavailable` صفر مالی نیستند.
- خروجی subject، raw evidence، reasonCode، سهمیه یا طرح فرضی ندارد. در ادامهٔ
  همین روز، reader شبکهٔ مستقل به projection متصل شد؛ تب مصرف و OIDC هنوز
  فعال نشده‌اند و هیچ دادهٔ مالی زنده خوانده نشد. فایل‌های در حال توسعهٔ
  Workspace و تنظیمات SSO دیگران تغییر نکردند.

قرارداد مرجع محلی:
`products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/wwwroot/contracts/portal-readers-v1.openapi.json`.
این مسیر مرجع تطبیق سورس است، نه وابستگی build یا ادعای انتشار قرارداد روی سرور.

## شاهد زندهٔ فقط‌خواندنی

سرویس `fanasa-unified-portal@development` فعال و release آن
`77f043fb2ba2f892022b00bc03ba1e6f696a6d96` است. `/health/live` برابر
200/Healthy و پاسخ ناشناس فضای کار `SignInRequired` با `Cache-Control: no-store`
بود؛ سازمان و محصول خصوصی صفر و discovery برابر `NotConfigured` بود.

مرکز راهبری همچنان release `hr-development-597dab599177-b841fabc1c3c` را داشت.
probe در `2026-10-15T06:40:08Z` با TLS معتبر، CA موجود، Host/SNI رسمی
`platform.fanasa.net.local` و اتصال loopback VPS انجام شد؛ redirect دنبال نشد:

| مسیر ناشناس | HTTP |
| --- | --- |
| `/health/live` | 200 |
| `/api/platform/published-services` | 404 |
| `/api/platform/application-tenants` | 404 |
| `/api/platform/products` | 401 |
| `/api/accounting/v1/consumers/{synthetic-id}/usage-report` با دورهٔ UTC معتبر | 404 |

probe اولیه با resolution معمول VPS به `URLError` خورد؛ این نتیجه خطای API
حساب نشد. probe بالا همان میزبان و بررسی TLS را حفظ و فقط اتصال TCP را به
loopback محدود کرد. DNS، proxy، trust store، Keycloak، داده یا تنظیمات سرور
تغییر نکردند. پاسخ 401 محصولات، مصرف‌کننده‌محور بودن یا صحت grant را اثبات نمی‌کند.

## کارهای مانده و مالکیت

| شناسهٔ محلی | کار | مالک / وابستگی |
| --- | --- | --- |
| UP-READERS-01 | بازگرداندن application-tenants و محصولات مجاز consumer؛ آزمون با SaaS مالک متفاوت | مرکز راهبری؛ source فعلی products پارامتر tenant مصرف‌کننده را اعمال نمی‌کند |
| UP-CATALOG-02 | انتشار reader، تنظیم کلاینت خواندنی و انتشار حسابرسی‌شدهٔ متادیتا/لینک‌های واقعی | مرکز راهبری؛ بدون جعل grant یا publication |
| UP-CATALOG-03 | فعال‌سازی DiscoveryPath پس از موفقیت واقعی و پذیرش کاربر مجاز/غیرمجاز | پنجره واحد، پس از دو مورد بالا |
| UP-USAGE-01 | روش مصوب credential انسانی با audience مرکزی، client/scope دقیق و billing.read زنده | مالک SSO/مرکز راهبری؛ service token کاتالوگ کافی نیست |
| UP-USAGE-02 | reader شبکه مستقل آماده؛ سیم‌کشی DTO به تب/متریک و کنترل نسل درخواست/دوره باقی است | پنجره واحد و هماهنگی مالک فایل‌های Workspace؛ فعال‌سازی وابسته به UP-USAGE-01 |
| UP-JOURNEY-01 | API درخواست/خرید، سابقهٔ شخصی و عرضه/پذیرش واقعی | قرارداد مرکزی سفارش/گردش هنوز تحویل نشده؛ آدرس یا فرآیند حدس زده نمی‌شود |

این جدول ثبت محلی وابستگی‌هاست، نه ادعای ساخت یا رزرو task در برد عملیاتی.
پیگیری همان روز: [خوانندهٔ انتقال، CI و شاهد تازهٔ VPS](USAGE-TRANSPORT-2026-10-15.md).
در اجرای اولیهٔ ثبت این جدول، ابزار هماهنگی چت‌ها/برد در دسترس نبود. در پیگیری
بعدی snapshot فقط‌خواندنی چت مرکزی بررسی شد؛ پیام تازه‌ای ارسال‌شده اعلام نمی‌شود.

## اجرای آزمون آداپتور مستقل

```powershell
dotnet run --project products/Fanasa.UnifiedPortal/tests/UsageProjection.Tests -c Release
dotnet run --project products/Fanasa.UnifiedPortal/tests/Discovery.Tests -c Release -- --http
node --test products/Fanasa.UnifiedPortal/tests/service-catalog.test.cjs products/Fanasa.UnifiedPortal/tests/portal-client.test.cjs
```

آزمون .NET فقط فایل واقعی projection را link می‌کند؛ به Web مرکزی، بانک، شبکه،
توکن واقعی یا فایل‌های dirty Workspace وابسته نیست. JSON و هویت‌ها مصنوعی‌اند.
این پوشش، پذیرش SSO، مجوز مالی، API زنده یا تجربهٔ واقعی خرید نیست.

نتیجهٔ این نوبت: ۹۰ بررسی مستقل projection، ۷۲ بررسی discovery/HTTP/redirect و
۱۹ آزمون JavaScript موفق شدند؛ مجموع ۱۸۱ بررسی. دو suite دات‌نت از snapshot
جداشدهٔ همین تغییر اجرا شدند؛ وب نیز به عنوان dependency suite کاتالوگ کامپایل
شد. تغییرات dirty سایر چت‌ها در این snapshot نبودند. اجرای اولیه در sandbox
به منع نوشتن obj برخورد کرد؛ اجرای مجاز بعدی موفق بود. NuGetAudit برای build
isolated خاموش بود و نتیجهٔ اسکن آسیب‌پذیری ادعا نمی‌شود.
