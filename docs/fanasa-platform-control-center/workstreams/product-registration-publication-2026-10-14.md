# پذیرش انتشار فرم ثبت محصول — ۱۴ اکتبر ۲۰۲۶

فرم `/ProductRegistration` در نسخهٔ فعال development تأیید شد. هر هفت فایل ویژگی با بستهٔ بازبینی‌شده یکسان است؛ انتشار جدیدتر از مسیر موجود GitLab CI و CD مورد اعتماد فعال شده است. هیچ حساب، client، grant یا محصولی توسط این کار ایجاد نشد.

## نسخه و شواهد واقعی

- نسخه: `hr-development-597dab599177-b841fabc1c3c`
- commit فعال و HEAD مشاهده‌شده: `597dab599177f98f7d260d00c72adb736614ad42`
- [Pipeline 10](https://gitlab.fanasa.net.local/javadsayedi/organization-hr-api/-/pipelines/10)، [job 22](https://gitlab.fanasa.net.local/javadsayedi/organization-hr-api/-/jobs/22)، runner 1؛ موفق.
- زمان پذیرش مستقل: `2026-10-14T12:26:35.221519+00:00`.
- fingerprint منبع: `b841fabc1c3c1b884763cd2e008c72b7e3c62f84c1ddbec21e38a0dc86ec3a37`.
- SHA256 آرشیو فعال: `84ed0d08dde9e1e80ba474f622106eea10ff1224fa35f4347137efdb235714be`.
- manifest: `2b78bc18b4414d2fcd92633bbe61b61b231c501420a82dc47010d67a27d0fd97`.
- CI evidence: `0c57c030a7a3bc318857b0ba327e9bc87f2574d5580b3245061582f1e2e034d5`.

شاهد کامل و قابل بازبینی در `.build/product-registration-20261013/runtime-verification.json` ذخیره شده است. fingerprint منبع مجدداً محاسبه شد؛ evidence API job با evidence فعال برابر بود. manifest، آرشیو، receipt بسته، receipt انتشار محدود و گزارش CD با همین commit/job تطبیق داشتند. هش همهٔ لاگ‌های گیت‌ها تأیید شد؛ لاگ ثبت محصول دقیقاً ۳۶ خط PASS دارد.

| گیت | تعداد موفق |
|---|---:|
| Organization.Hr.Tests | 109 |
| Organization.Bearer.Tests | 77 |
| Organization.Tests | 447 |
| Workbench.Boundary.Tests | 23 |
| ProductRegistration.Tests | 36 |
| native workbench | 18 |

وب، Workbench و CD timer فعال‌اند. `/health/live` و health موتور Workbench پاسخ ۲۰۰ داشتند. درخواست ناشناس به فرم و PolicyWorkbench به `/login` و درخواست `/Products` به SSO هدایت شد؛ redirect دنبال نشد و هیچ نشست انسانی ساخته نشد. اتصال بررسی‌ها از loopback با hostname، SNI و CA معتبر انجام شد؛ این شاهد، پذیرش DNS روی رایانهٔ کاربر نیست.

## سابقهٔ تحویل و رفع مانع

commit اولیهٔ این ویژگی `1e4e86cb29ffca2a964f9a76b7128ae21b5b550b` بود. Pipeline 8/job 19 پیش از اجرای آزمون‌ها با `runner_system_failure` شکست خورد: Docker پیام `all predefined address pools have been fully subnetted` داد. فضای دیسک و حافظه کافی بود.

یک شبکهٔ بازماندهٔ راه‌اندازی ناموفق fixture، به نام `fanasa-gateway-dual-20261013-7f8243e2_dependencies`، با ID `260eee866b7e5ba5908e85879f6d2712e26ca1982698f7d91ada68433d27230c` و subnet `192.168.240.0/20` بازنشسته شد. guard تازه، نبود reference در هر ۲۰۱ کانتینر روشن/خاموش را بررسی کرد. فهرست کانتینرهای روشن ثابت ماند؛ هیچ کانتینر یا volume حذف نشد. metadata شبکه پیش از حذف ذخیره شد؛ SHA256 آن `d228bc8ab83da0f5774178a1fdf50f3e3efb97d6c95387a969db5d35689bba94` بود. شواهد fixture تاریخی نیز حفظ شد.

خروجی معتبر retry برای job 19 دریافت نشد. دو helper محلیِ معطلِ همین کار متوقف شدند. بررسی مستقل بعدی نشان داد HEAD و نسخهٔ فعال جلو رفته‌اند؛ retry نسخهٔ قدیمی ادامه نیافت و پذیرش بر نسخهٔ واقعاً فعال انجام شد. هیچ موفقیتی برای pipeline 8 ادعا نمی‌شود.

## حدود پذیرش و ادامهٔ واقعی

ثبت محصول create-only، preview ده‌دقیقه‌ای وابسته به actor/فیلدها، CSRF، نقش `platform.admin` و ذخیرهٔ اتمیک محصول/audit با آزمون‌ها پوشش داده شده‌اند. تست بازگشایی Registry، restart کامل فرآیند با محصول تازه نیست.

ورود انسانی واقعی، ثبت «گیت‌لب فن آسا» و کشف آن در Portal در این پذیرش انجام نشده‌اند. ثبت metadata به‌تنهایی انتشار عمومی، نمایش در Portal یا مجوز دسترسی ایجاد نمی‌کند. دادهٔ موردنظر برای ثبت انسانی: key/audience برابر `fanasa-gitlab`، مرکز `fanasa.rayan`، owner برابر `platform`، URL برابر `https://gitlab.fanasa.net.local/` و attribute برابر `entrypoint.kanban.url` با مقدار `https://gitlab.fanasa.net.local/fanasa/neo-framework/-/boards/1`. tenant باید از انتخاب‌گر واقعی موجود انتخاب شود.

۳۶ بررسی محلی و ۱۰ آزمون helper نیز قبلاً موفق بودند. build محلی نخست با NU1900 متوقف شد؛ retry محلی از cache و `NuGetAudit=false` استفاده کرد. سیاست audit سورس/CI تغییر نکرد و پذیرش محلی، موفقیت audit امنیتی را اثبات نمی‌کند.

بستهٔ اصلی در `E:/SJVS/Projects/Hyper/.work/gitlab-registry-20261013/` بدون تغییر نگه داشته شد؛ ZIP SHA256 برابر `1afb850ec78f49e65b2bcdf8eb1e50fb02962363c82984842edff8f8b5b62419` و diff SHA256 برابر `5b9d9eaaf637f6e4b6ea30b639d96a6ba8e7e6db16e1cf9f481b5808e4c28fdf` است. تغییرات مشترک سایر مالکان commit/push نشدند.

پذیرش تکمیلی در `2026-10-14T17:45:35.999150+00:00` برای همان نسخه موفق بود و در `.build/product-registration-20261013/final-runtime-verification.json` ذخیره شد. هش پنج فایل اجرایی اصلی با manifest برابر بود؛ شامل DLL وب، موتور مشترک در وب/Workbench، DLL Workbench و SQLite native. خواندن HR با هویت سرویس موجود و scope خواندنی از گیت‌وی پاسخ ۲۰۰ داشت؛ فراخوانی مستقیم upstream بدون گواهی گیت‌وی با ۴۰۳ رد شد. TLS و hostname/SNI بررسی شدند. رسید انتشار، حفظ export، سلامت SQLite، عبور protection و برابری هش state پیش/پس انتشار را تأیید می‌کند.

خواندن فقط‌خواندنی Registry در همان زمان نشان داد کلید `fanasa-gitlab` هنوز ثبت نشده است. ورود/ثبت انسانی و کشف Portal همچنان انجام‌نشده‌اند. payloadهای موقت actions/commit/retry همین کار در میزبان و کانتینر GitLab پاک شدند؛ خروجی‌های محلی bin/obj این clone جداگانه پاک و در `cleanup.json` ثبت شدند. سورس، شواهد، لاگ‌ها، هش‌ها، بستهٔ اصلی و فایل‌های مشترک دیگر کارها حفظ شدند.

پاک‌سازی محلی ابتدا با Access denied داخل sandbox متوقف شد؛ اجرای همان فرمان محدود و دارای کنترل مسیر خارج sandbox موفق بود. در `2026-10-14T17:53:51.2388929+00:00` هر شش پوشهٔ bin/obj هدف حذف شد و ۱۴۲٬۱۵۲٬۶۰۳ بایت در تلاش موفق آزاد شد؛ مقدار حذف‌شدهٔ احتمالی تلاش نخست در این عدد محاسبه نشده است.
