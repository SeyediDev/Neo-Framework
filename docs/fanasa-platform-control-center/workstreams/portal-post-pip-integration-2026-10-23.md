# ساخت یکپارچه پس از PIP — ۲۳ اکتبر ۲۰۲۶

رفع مانع PIP در سورس مشترک تأیید شد. `PipSourceDefinition` اکنون `Definitions` اختیاری دارد و قراردادهای قدیمی از مقدار پیش‌فرض استفاده می‌کنند؛ تغییر لازم از قبل توسط مالک PIP در سورس حاضر بود و patch دیگری به فایل‌های مشترک افزوده نشد. [شواهد مالک PIP](../../../products/Fanasa.AccessManagement/docs/evidence/pip-2026-10-23/README.md) با [اجرای مستقل نهایی این چت](portal-readers-evidence-2026-10-23/post-pip/integration-final-verification.json) تکمیل شد.

ساخت Release مرکز و Portal موفق بود؛ همهٔ buildهای اجراشده صفر خطا و هشدار داشتند. برای وابستگی ContractPolicy که بیرون solution مرکزی است، ساخت نهایی با `-p:ShouldUnsetParentConfigurationAndPlatform=false` اجرا شد تا Configuration=Release در ProjectReference حفظ شود. این اصلاح فرمان اعتبارسنجی است؛ فایل solution و CI مشترک تغییر نکردند. [لاگ ساخت نهایی](portal-readers-evidence-2026-10-23/post-pip/Final.Build.AccessManagement.log) فرمان و مسیرهای واقعی خروجی را پشتیبانی می‌کند.

| مجموعهٔ اجرای تازه | بررسی موفق |
| --- | ---: |
| شش suite مرکزی و harness گزارش/adapter/transport تحویل قبلی | ۹۹۷ |
| Organization و PIP، Temporal، Calendar | ۷۸۸ |
| چهار suite محصول Portal: Workspace، Discovery HTTP، usage projection و usage transport | ۴۱۳ |
| جمع ۱۳ suite دات‌نت | **۲۱۹۸** |
| JavaScript مرکز: governance، calendar، PIP، navigation، architecture و usage | ۵۰ |
| JavaScript Portal و Workspace | ۴۹ |
| renderer گزارش مصرف با هشت fixture تازهٔ C# | ۲۴ |
| جمع JavaScript | **۱۲۳** |

شمارش ۳۴۲ در harness انتقال گزارش، assertion در ۵۷ گروه است؛ در جمع دات‌نت به‌عنوان «بررسی» آمده است. مجموعه‌های گزارش canonical و Portal projection مستقل‌اند و هر کدام در manifest با نام پروژهٔ خود مشخص شده‌اند. اجرای مجدد Organization.Tests دوباره در جمع شمرده نشده است.

تمام کپی‌های runtime از Web مرکزی، ContractPolicy و Web مربوط به Portal در خروجی نهایی، برای هر اسمبلی SHA-256 یکسان دارند. هنگام همسان‌سازی تنها وابستگی‌های اجرایی Organization.Tests تغییر کرد و همان suite مجدداً اجرا شد. DLLهای استفاده‌شده در سایر suiteها ثابت ماندند. SHA-256 نهایی Web مرکزی:

```text
d78b162f3260e955075c0b5cfa24b8cd187c560d42d369b301b81443fd10b695
```

[فهرست پیش از اجرا](portal-readers-evidence-2026-10-23/post-pip/source-before.json)، [فهرست پس از اجرای اولیه](portal-readers-evidence-2026-10-23/post-pip/source-after.json) و مقایسهٔ نهایی manifest نشان می‌دهند ۲۷۸ فایل منبع و تنظیم تحت پایش تغییر نکردند. این inventory منتخب است و قفل سراسری repository نیست.

[راه‌اندازی واقعی میزبان](portal-readers-evidence-2026-10-23/post-pip/startup-verification-retry.json) نیز با همان DLL نهایی، محیط Production مصنوعی، content root تازه و بانک‌ها و کلیدهای کاملاً جدا انجام شد: `/health/live` پاسخ 200 و Healthy داشت؛ دو reader با درخواست ناشناس پاسخ 401 دادند. پردازش آزمون پس از بررسی متوقف شد. این smoke راه‌اندازی DI و hosted serviceهای واقعی را پوشش می‌دهد؛ reader احراز هویت‌شده، منبع خارجی PIP، SSO، Gateway و HR/مالی واقعی را نمی‌آزماید.

دو مشکل محیط اجرای آزمون در شواهد تاریخی حفظ شده‌اند: Discovery HTTP مسیر معمول `bin/Release/net10.0` را فرض می‌کرد؛ DLLهای ساخته‌شده و assetهای فعلی بدون تغییر کد در چیدمان جدا و سازگار قرار گرفتند و ۷۸ بررسی موفق شد. درخواست health در نخستین smoke از مهلت پنج‌ثانیه‌ای عبور کرد؛ اجرای نهایی با state تازه و مهلت ۴۵ثانیه موفق شد. لاگ‌های تلاش نخست حذف یا به موفقیت بازنویسی نشده‌اند.

[تشخیص خواندنی 503 زنده](portal-reader-503-diagnosis-2026-10-23.md) مستقل باقی است: release عملیاتی، تنظیمات reader و ثبت client در این کار تغییر نکردند. رفع مانع build به‌معنای آماده‌شدن اتصال زنده نیست. هیچ استقرار، grant، client یا credential تازه‌ای ایجاد نشد.

منابع و فرمان‌های تکرارپذیر، لاگ‌ها، SHA-256 اسمبلی‌ها و fixtureهای مصنوعی در پوشهٔ شواهد نگه داشته شده‌اند. خروجی حجیم ساخت و state موقتِ متعلق به همین اجرا پس از ممیزی حذف و نبود پوشه تأیید شد؛ [رسید پاک‌سازی](portal-readers-evidence-2026-10-23/post-pip/cleanup.json) حجم دقیق را ثبت می‌کند. فایل‌های چت‌های دیگر و صوت‌ها در این نوبت حذف نشدند.
