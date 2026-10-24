# شواهد runtime مصرف — تطبیق، بازیابی و پنل مدیریتی

تاریخ: ۱۱ اکتبر ۲۰۲۶. گزارش خواندنی تطبیق و retry مدیریتی حسابداری در همان Fabric
محصول پیاده‌سازی شد. صفحهٔ `/UsageAccounting` گزارش واقعی را با انتخاب سازمان،
جست‌وجو، فیلتر، جزئیات مبلغ دقیق و اقدام دارای دلیل نمایش می‌دهد.

## نتیجهٔ نهایی

- ساخت محصول و runner: موفق، **صفر warning و error**.
- runner کامل: **۴۴۷ check با exit 0**؛ شامل ۹۱ بررسی تازهٔ تطبیق/بازیابی.
- Node: **۱۴ گروه آزمون، ۱۴ pass و صفر fail**.
- مرورگر Chrome موجود، نسخهٔ `155.0.8059.40`: **۱۲ سناریو، همگی موفق**.
- ۱۳۸ ورودی منتخب runtime پیش و پس از build/test همسان بودند؛ baseline فقط پس
  از ساخت، آزمون‌های موفق و بررسی همسانی نوشته شد. سه اسمبلی خروجی ثبت شدند.

[log ساخت](fixtures/runtime-build.v3.txt)، [log .NET](fixtures/runtime-checks.v3.txt)،
[log Node](fixtures/usage-accounting-ui-checks.v3.txt)،
[نتایج مرورگر](fixtures/usage-accounting-browser-checks.v3.json) و
[baseline سورس/باینری](fixtures/runtime-source-baseline.v3.json) شواهد این اجرا هستند.
نسخه‌های [۱](RUNTIME-EVIDENCE.md) و [۲](RUNTIME-EVIDENCE.v2.md) تاریخی‌اند و log و
manifest آن‌ها بازنویسی نشد. اجراهای مقدماتیِ ناموفق baseline تولید نکردند؛
یک اجرای دارای تغییر هم‌زمان سورس نیز پس از گذر آزمون‌ها، ثبت baseline را رد کرد.

فرمان بررسی از root مخزن:

```powershell
& ./docs/fanasa-platform-control-center/workstreams/usage-accounting/verify-runtime.ps1
```

ساخت بدون restore، ترتیبی و بدون build server انجام شد. دسترسی محلی لازم برای
cacheهای SDK، ایجاد logها و اجرای browser موقت از مسیر بازبینی خودکار تأیید شد.
baseline یک manifest کامل dependency/SDK، deployment یا محیط عملیاتی نیست.

## رفتار backend بررسی‌شده

گزارش در snapshot خواندنی و pinned SQLite، سندهای مصرف، رسیدهای اصلی و alias،
مبلغ و نشان دفتر، fingerprint، زمان و envelopeهای outbox را بررسی می‌کند. paging
بیش از ۱۰۰ ردیف، حد scan، شواهد گم‌شده/متعارض/خراب، زمان نامعتبر outbox، رکورد
غیرقابل انتساب و scope سازمان بررسی شدند. خواندن، داده را اصلاح نمی‌کند؛ نوشتن
در snapshot رد شد و پس از خطا context تراکنش آزاد شد. تغییر هم‌زمان اتصال دوم
در همان snapshot دیده نشد و خواندن بعدی تغییر جدید را دید.

بازیابی فقط همان EventJson معتبر نگه‌داری‌شده را با مبلغ و نرخ اصلی append می‌کند؛
backend و validator پذیرش تازه اجرا نمی‌شوند. اعتبار ناکافی، نبود یا غیرفعال‌بودن
حساب و دورهٔ invoice صادرشده override نشدند. پس از رفع محدودیت، درخواست تازه یک
ثبت مالی و یک رویداد committed ساخت. درخواست تاریخی همسان همان receipt را بدون
audit تازه برگرداند؛ تغییر actor/دلیل/هدف تعارض بود. دو مدیر هم‌زمان یک charge و
دو receipt ممیزی ایجاد کردند. شواهد خراب یا scan ناقص پیش از هر اثر تازه رد شدند.

metadata API و نگاشت پاسخ‌های مستقیم بررسی شد: Cookie، `PlatformAdmin`،
antiforgery، سقف body و actor برگرفته از نشست. درخواست مصرف تازه در این API نیست.

## رفتار رابط و مرورگر

آزمون‌های Node، حفظ lexeme مبلغ بزرگ/اعشاری، exponent و متن JSON escape‌شده،
انتخاب operation دقیق در گزارش سالم و تازه، دلیل محدود، فرمان immutable، رد
double submit، حفظ UUID/هدف/دلیل در نتیجهٔ نامشخص، receipt ناسازگار، نتیجهٔ pending
تاریخی و بازیابی درخواست محفوظ پس از reload را بررسی کردند.

مرورگر با profile موقت و درخواست‌های محدود به `http://127.0.0.1:5198` اجرا شد؛
هویت Cookie و داده‌های آن مصنوعی‌اند. مرورگر، Playwright و SDK موجود استفاده شدند؛
browser یا dependency تازه نصب نشد. بررسی‌ها شامل این رفتارها بودند:

- صفحهٔ واقعی با ۲۷ مصرف؛ ۲۴ committed و ۳ pending در آغاز fixture، فیلتر و جست‌وجوی
  رقم فارسی، و حالت جست‌وجوی بدون نتیجه.
- رد HTTP واقعی درخواست بدون توکن CSRF، و بازیابی واقعی یک مصرف نمونه با همان مبلغ.
- گم‌کردن پاسخ پس از commit واقعی سرور؛ reload و retry صریح با همان فرمان/UUID،
  بدون charge دوم. انتخاب سازمان و ویرایش دلیل در این حالت قفل بود.
- receipt مصنوعی pending با اقدام صریح برای درخواست بعدی و UUID تازه.
- گزارش ناقص یا خراب، GET ناموفق با حفظ گزارش قدیمی و غیرفعال‌شدن اقدام تازه،
  گزارش خالی، پاسخ نامعتبر/سازمان بیگانه و نبود خطای JavaScript بدون رسیدگی.
- نمایش مبلغ `999999999999999999.123456789` در پاسخ عددی JSON، دقیق در جزئیات و
  بدون اسکرول افقی کل صفحه یا بیرون‌زدگی مبلغ در عرض‌های ۳۹۰ و ۳۲۰ پیکسل.

سناریوهای حالت UI از response interception استفاده کردند؛ این پاسخ‌های مصنوعی
ادعای وجود چنین مبلغی در دفتر یا صدور receipt واقعی نمی‌کنند. سناریوی پاسخ گم‌شده
و بازیابی موفق به API و SQLite واقعی همین fixture متصل بودند.

تصاویر بررسی‌شده:

- [نمای دسکتاپ](fixtures/usage-accounting-desktop.v3.png)
- [جزئیات و دلیل اقدام](fixtures/usage-accounting-detail.v3.png)
- [نمای موبایل](fixtures/usage-accounting-mobile.v3.png)
- [جزئیات مبلغ بزرگ در موبایل](fixtures/usage-accounting-mobile-detail.v3.png)
- [جزئیات در عرض ۳۲۰](fixtures/usage-accounting-mobile-detail-320.v3.png)
- [وضعیت خالی](fixtures/usage-accounting-empty.v3.png) و
  [دریافت ناموفق](fixtures/usage-accounting-error.v3.png)

خطای آغازین رابط هنگام نبود receipt اصلاح شد. برچسب دسترس‌پذیری جدول نیز در
scroller خودش محصور شد؛ جدول همچنان روی موبایل مستقل اسکرول می‌شود. logo، فونت
و theme موجود حفظ شدند؛ دریافت تازهٔ brand book در این اجرا موفق نبود.

## حدود شواهد

احراز هویت مرورگر fixture مصنوعی است؛ SSO عملیاتی، نقش‌های واقعی و نشست production
از ابتدا تا انتها بررسی نشدند. کنترل CSRF واقعی همین pipeline بررسی شد. هیچ داده،
اعتبار، تسویه، grant، مقصد بیرونی یا deployment واقعی تغییر نکرد.

گزارش فقط سازگاری شواهد محلی را اثبات می‌کند؛ شمارش صف، دریافت در مقصد را تأیید
نمی‌کند. حد مستقل scan هر جریان ۱..۱۰۰۰۰ است و ردیف‌های مشترکِ دیگر سازمان‌ها نیز
سهمی از آن دارند. scan ناقص اجازهٔ recovery نمی‌دهد. نگه‌داری فرمان در
sessionStorage، حفظ آن پس از بسته‌شدن tab یا پاک‌شدن storage را تضمین نمی‌کند.

ورودی UsageEvent از producer بیرونی همچنان نیازمند binding workload و شاهد معتبر
پذیرش تاریخی است. این مرحله امضای قرارداد تجاری، admission عملیاتی، RPO صفر، قطع
برق، exactly-once شبکه، اصلاح خودکار دفتر یا settlement را ثابت نمی‌کند. تنظیمات
metering/worker واقعی فعال نشدند. پیش‌نیازها و قراردادها در
[راهنمای محصول](../../../../products/Fanasa.AccessManagement/docs/USAGE-ACCOUNTING.md) آمده‌اند.
Skill/MCPهای موجود مصرف‌کنندهٔ این API نیستند؛ قراردادهای آن‌ها تغییر نکرد.
