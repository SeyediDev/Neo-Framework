# بازبینی مستقل تحویل پنجرهٔ واحد — ۲۴ اکتبر ۲۰۲۶

## محدوده

تحویل بررسی‌شده `206e41c3e804b7df3052dbb11f9274417d707c19` در شاخهٔ مستقل
`codex/fanasa-billing-integration-20261024` است. مبنای این چت، `b367728`، جد
همین تحویل است؛ پس اصلاحات `e90d749` حذف نشده‌اند. diff فایل‌های `portal.js`،
`workspace-transport.js` و fixture قدیمی مرورگر با مبنای این چت خالی بود.

بازبینی اولیه فقط‌خواندنی بود. هش سه گزارش backend/static/portal و manifestهای
سورس و باینری با `INTEGRATION-RECEIPT.json` مالک تطبیق داشت. نسخهٔ ادغام‌شده
در checkout مشترک ادغام یا نصب نشد؛ اجرای مستقل از `git archive` همان commit
در `.artifacts/portal-integration-review-20261024/source` انجام شد.

## رفتار ترکیبی

پس از دریافت کاتالوگ مجاز، خواندن مالی فقط از بودجهٔ باقی‌مانده تا ثانیهٔ ۴۸
استفاده می‌کند. ساعت monotonic، لغو downstream، پایان انتظار reader نامتعاون
و کنارگذاشتن پاسخ دیررس در harness مخصوص PageModel پوشش دارند. این محدودیت
برای انتظار افزودهٔ مالی است؛ provider نامتعاون کاتالوگ، history یا discovery
را به یک deadline سراسری جدید تبدیل نمی‌کند.

وضعیت سابقه و مالی مستقل‌اند. اطلاعات مالی مصرف‌کنندهٔ دیگر، loading و شکست
دریافت، مبلغ قبلی را به نمایش برنمی‌گردانند. تنظیم `ConsumerBilling.Enabled`
همچنان false و GatewayOrigin/Audience/Scope خالی است.

## نتیجهٔ اجرای مستقل

پنج مجموعهٔ JavaScript هم روی checkout تحویل و هم روی snapshot جدا، هر بار
**۸۵ تست موفق** داشتند؛ این دو اجرای همان تست‌ها هستند و با هم جمع نمی‌شوند.
شکست، skip و لغو وجود نداشت. چهار build Release وب و پروژه‌های آزمون با صفر
هشدار و خطا موفق شدند. restore فقط از cache، با feed خالی محلی و
`NuGetAudit=false` بود؛ audit آنلاین اجرا نشد.

| مجموعهٔ مستقل | بررسی موفق |
|---|---:|
| Portal.Tests | ۱۲۵ |
| BillingIntegration.Tests | ۶۷؛ linked-source، هویت امضاشدهٔ مصنوعی |
| BillingWorkspace.Tests | ۲۴؛ شامل ۱۰ بررسی بودجهٔ ترکیبی |
| JavaScript پورتال/کاتالوگ/فضای کار/transport/حساب | ۸۵ |

محتوای هر ۴۹ فایل سورس runtime پس از build با blob همان commit، با لحاظ تفاوت
CRLF/LF، تطبیق داده شد. اسمبلی وب در خروجی میزبان، Portal.Tests و
BillingWorkspace.Tests هم‌هش بود:
`4cae4c066b5c6b1eafa843ff99800bb9dd4c63bf75b5fdec6fe81bc5895bee67`.
آزمون linked-source مالی، Web DLL را بارگذاری نمی‌کند.

فرمان بازتولید، لاگ‌ها، manifest SHA256 و نتیجهٔ اجرا در
`.artifacts/portal-integration-review-20261024/` محفوظ‌اند. این خروجی بستهٔ نصب
نیست و هیچ باینری آن روی VPS کپی نشد.

این اجراها بدون credential و سرویس زنده‌اند. پذیرش دیداری جدید، اتصال SSO،
reader مجاز زنده، پرداخت، فعال‌سازی مالی یا انتشار از آن‌ها نتیجه نمی‌شود.

## مورد لازم برای CI تحویل

در commit بررسی‌شده، `BillingIntegration.Tests` و `BillingWorkspace.Tests` در
solution محصول نیستند و workflow پورتال نیز آن‌ها را اجرا نمی‌کند. مجموعهٔ
`consumer-billing.test.cjs` هم در فهرست Node همان workflow نیامده است. اجرای
محلی موفق این شکاف را پوشش نمی‌دهد؛ پیش از پذیرش CI نسخهٔ ادغام‌شده باید هر
سه مجموعه به مسیر خودکار بررسی اضافه شوند. فایل‌های مشترک مالک ادغام در این
بازبینی تغییر نکرده‌اند.
