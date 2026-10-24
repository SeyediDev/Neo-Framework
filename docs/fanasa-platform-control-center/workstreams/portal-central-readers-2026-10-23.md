# تحویل API مرکزی — ۲۳ اکتبر ۲۰۲۶

پیگیری عملیاتی: [تشخیص قطعی 503 و پیش‌نیازهای آماده‌سازی](portal-reader-503-diagnosis-2026-10-23.md) در ۲۳ اکتبر ثبت شد. readerهای release زنده به‌علت نبود تنظیمات آماده نیستند؛ نتیجهٔ آزمون‌های محلی زیر پذیرش عملیاتی نیست.

خواندن سازمان و محصولات مجاز مصرف‌کننده تکمیل شد: `application-tenants?subject=...` و `products?subject=...&tenant=...` زیر `/api/platform`. هر دو مسیر client یکتا و فعال از نوع service با `platform.catalog` و عضویت فعال می‌خواهند؛ offer/grant تاریخی فقط برای فهرست محصولات لازم است. `Platform:ConsumerCatalog:Enabled` پیش‌فرض false است. SaaS با تولیدکنندهٔ متفاوت پشتیبانی می‌شود و سیاست عملیات توسعه‌دهنده حفظ شده است. رجیستری فقط خوانده می‌شود؛ schema یا مجوز تازه ساخته نمی‌شود.

چرخهٔ لغو انتشار پس از غیرفعال‌شدن مالک یا حذف دامنهٔ مجاز اصلاح شد. نبود publication در GET پاسخ204 بدون محتوا دارد. [OpenAPI پنج مسیر](../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/wwwroot/contracts/portal-readers-v1.openapi.json) همگام و ارجاع‌های JSON بررسی شد.

| آزمون | بررسی موفق |
| --- | ---: |
| consumer catalog | ۲۲۳ |
| catalog/publication | ۶۵ |
| API گزارش مصرف | ۱۰۶ |
| گزارش canonical | ۱۴۵ |
| mapper Portal | ۱۱۶ |
| انتقال HTTP مستقل | ۳۴۲ |
| جمع | **۹۹۷** |

[نتیجه و لاگ‌های اجرا](portal-readers-evidence-2026-10-23/verification.json) و [اثر انگشت منابع پیش از اجرا](portal-readers-evidence-2026-10-23/source-before.json) حفظ شده‌اند. امضای JWT در fixtureهای consumer و usage واقعی و هویت‌ها و داده‌ها مصنوعی‌اند. startup کامل عملیاتی، FabricClaimsTransformation، SSO و HR واقعی آزموده نشده‌اند. snapshot دو بانک نیز تراکنش توزیع‌شده نیست.

ساخت اولیهٔ میزبان و شش پروژهٔ آزمون موفق بود. تغییر موازی PIP در FabricServices.cs پس از آن، ساخت یکپارچه را با CS1061 در PipContracts.cs:8 مواجه کرد؛ readerهای تحت پایش ثابت ماندند. [لاگ ساخت یکپارچه](portal-readers-evidence-2026-10-23/Web-final.build.log) مستقل است و خطا به چت مالک گزارش شد. **این مانع بعداً رفع و در [ساخت یکپارچهٔ تازه پس از PIP](portal-post-pip-integration-2026-10-23.md) تأیید شد:** ۲۱۹۸ بررسی دات‌نت، ۱۲۳ آزمون JavaScript و smoke میزبان واقعی موفق بودند؛ لاگ شکست قبلی شاهد تاریخی است.

[استقرار۱۷اکتبر](../../../products/Fanasa.AccessManagement/docs/ORGANIZATION-DEPLOYMENT-2026-10-17.md) release958e را accepted ثبت کرده است؛ پاسخ‌های404 در [گزارش قدیمی](portal-central-readers-2026-10-15.md) فقط شاهد۱۴اکتبر هستند. در مرحلهٔ تحویل محلی استقرار تازه یا فعال‌سازی HR/مالی انجام نشد؛ probe خواندنی بعدی همان روز در گزارش تشخیص 503 بالا ثبت شده است. پذیرش اتصال Portal، credential انسانی، لینک‌های رسمی انتشار و نام سامانه/client سرویس HR هنوز مستقل‌اند؛ ایمیل جای نام سامانه یا client نیست.


پاک‌سازی این نوبت انجام شد: ۳۷۶٬۲۱۷٬۰۹۷ بایت خروجی ساخت و helper تکراری متعلق به همین کار حذف و نبودشان تأیید شد. [رسید پاک‌سازی](portal-readers-evidence-2026-10-23/cleanup.json) حفظ شده است؛ فایل صوتی در این نوبت حذف نشده است.
