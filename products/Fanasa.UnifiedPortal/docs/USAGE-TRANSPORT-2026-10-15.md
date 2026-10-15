# خواندن مستقل گزارش مصرف — ۱۵ اکتبر ۲۰۲۶

## تغییر پنجره واحد

`ConsumerUsageReportClient` در سمت سرور اضافه شد. این خواننده از همان
`ConsumerUsageReportProjection` نسخهٔ ۱ استفاده می‌کند؛ parser مالی دوم یا
وابستگی به اسمبلی Web مرکز راهبری ایجاد نشد. یک GET به مسیر مصوب
`/api/accounting/v1/consumers/{consumer}/usage-report?from=...&to=...` می‌فرستد.
subject از فرم یا query گرفته نمی‌شود و در URL درخواست نیست.

میزبان باید origin دقیق HTTPS روی پورت ۴۴۳، بدون path، userinfo، query یا fragment
باشد. handler عملیاتی redirect، cookie، credentials محیطی و decompression را
خاموش می‌کند. retry احراز هویت، fallback به service token و refresh token ندارد.
بودجهٔ پیش‌فرض ۳۰ ثانیه و حداکثر ۶۰ ثانیه، lookup اعتبارنامه، HTTP و بدنه را
پوشش می‌دهد؛ لغو درخواست‌کننده به خطای مالی تبدیل نمی‌شود.

پاسخ باید HTTP 200، `application/json` و `Cache-Control: no-store` داشته باشد؛
بدنهٔ اعلام‌شده یا stream بیشتر از ۸ MiB رد می‌شود. projection موجود subject،
consumer، دورهٔ دقیق UTC نیمه‌باز، basis و جمع مبلغ را کنترل می‌کند. در failure
اطلاعات مالی خالی است؛ notRecorded و incomplete همچنان «نامعلوم» هستند، نه صفر.
توکن، subject، پاسخ خام و exception داخلی در result مرورگر قرار نمی‌گیرند.
اعتبارنامه کلاس عادی با propertyهای internal است تا ToString و serialization
پیش‌فرض، bearer را افشا نکنند.

## مرز هویت و اتصال — هنوز عملیاتی نیست

`IConsumerUsageHumanCredentialProvider` یک قرارداد server-only است، نه پیاده‌سازی
ورود یا delegation. مسئول پیاده‌سازی باید credential **انسانیِ اعتبارسنجی‌شده**
را از نشست جاری، client واقعی Portal و audience/scope مصوب مرکزی تأمین کند.
خود خواننده signature، issuer، scope یا ماهیت انسانی JWT را اثبات نمی‌کند؛
این‌ها تعهد provider و بررسی مجدد API مرکزی‌اند. خواننده فقط شکل subject و
header و انقضا را پیش/پس از خواندن کنترل می‌کند. subject باید Guid canonical
غیرخالی باشد و اعتبارنامه/توکن نمونه یا client_credentials در production ممنوع است.

تا تعیین provider مصوب، کلاس در DI ثبت نشده و هیچ تغییر در OIDC، SaveTokens،
realm، grant، Workspace، تب مصرف یا environment انجام نشده است. service token
کاتالوگ جایگزین credential مالی نیست. provider نباید در browser/localStorage،
کوکی بزرگ حاوی token یا یک user store جدید پیاده شود.

برای اتصال نهایی: تحویل credential انسانی و endpoint زنده، هماهنگی مالک
Workspace، محدودسازی DTO خروجی، no-store روی پاسخ HTTP خود Portal، و کنترل
نسل درخواست/تغییر سازمان/دوره/خروج لازم‌اند. شکست خواندن مصرف نباید خدمات مجاز
را پاک کند. این تحویل ادعای پذیرش واقعی SSO، billing.read یا حسابداری ندارد.

## CI و بسته

دو harness نگاشت و انتقال مصرف به solution مستقل محصول و job تأیید CI اضافه
شدند. مسیر مبدأ scp در هر دو job استقرار با نام واقعی فایل دانلودشده،
`bundle/fanasa-unified-portal.zip`، هماهنگ شد؛ نام مقصد همچنان شامل SHA است.
workflow در این نوبت روی GitHub اجرا یا به production ارسال نشد.

## شاهد زندهٔ فقط‌خواندنی

probe در `2026-10-15T13:53:12Z` با Host/SNI رسمی، TLS/CA موجود و اتصال TCP
loopback روی VPS اجرا شد؛ redirect دنبال نشد و تنظیمی تغییر نکرد:

| مسیر مرکزی، بدون توکن | HTTP |
| --- | --- |
| `/health/live` | 200 |
| `/api/platform/published-services` | 404 |
| `/api/platform/application-tenants` | 404 |
| `/api/platform/products` | 401 |
| مسیر گزارش مصرف با tenant مصنوعی و دورهٔ UTC معتبر | 404 |

این شاهد همچنان مانع فعال‌سازی خواننده‌های کاتالوگ و مصرف است؛ پاسخ 401 محصولات
گواه consumer-aware بودن آن نیست. چت مرکزی در snapshot مشاهده‌شده فعال و منتظر
approval خودش بود؛ هیچ approval یا deployment آن از این چت انجام نشد.

## اجرای محلی

```powershell
dotnet run --project products/Fanasa.UnifiedPortal/tests/UsageProjection.Tests -c Release
dotnet run --project products/Fanasa.UnifiedPortal/tests/UsageTransport.Tests -c Release
```

آزمون‌های انتقال از handler مصنوعی استفاده می‌کنند؛ propertyهای handler عملیاتی
نیز بررسی می‌شوند، اما آزمون HTTP/TLS زندهٔ گزارش مالی نیستند. هویت‌ها، توکن و
مبلغ‌های fixture مصنوعی‌اند.

## نتیجهٔ اجرای نهایی

solution مستقل محصول از snapshot
`bdb61cdaa1e41bc8f35d5cdbbdfd0b0043ce0d15` با صفر هشدار و صفر خطا در Release
ساخته شد. snapshot شامل فقط تغییرات این تحویل روی HEAD
`d54e03b796095d4b708c714fb236183136260dd8` بود؛ فایل‌های dirty و stageهای چت‌های
دیگر، از جمله Workspace، وارد آن نشدند. چهار harness از همان snapshot اجرا شدند:

| مجموعه | بررسی موفق |
| --- | --- |
| عضویت و فضای کار | ۴۹ |
| کشف سرویس و HTTP | ۷۸ |
| نگاشت گزارش نسخهٔ ۱ | ۹۰ |
| انتقال مستقل گزارش | ۱۴۵ |
| جمع | ۳۶۲ |

بررسی static مسیر فایل ZIP هر دو job و حضور هر دو suite در workflow نیز موفق
بود؛ این بررسی parse کامل YAML یا اجرای GitHub Actions نیست. restore از cache
و feed خالی محلی انجام شد. `NuGetAudit=false` فقط برای restore ایزوله بود؛ audit
آسیب‌پذیری یا تغییر سیاست CI ادعا نمی‌شود.

بخش‌های جدید وارد نسخهٔ زنده نشدند، چون provider انسانی و API عملیاتی تحویل
نشده‌اند. هیچ تنظیم VPS، داده، احراز هویت، دسترسی یا فایل فعال چت دیگر تغییر
نکرد. قرارداد Skill/MCP تغییر نکرد و تنها راهنمای اتصال/آزمون محصول به‌روز شد.
