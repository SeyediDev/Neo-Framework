# انتشار مقاوم‌سازی discovery — ۱۴ اکتبر ۲۰۲۶

## نسخه و محدوده

کامیت `775605b613de72e89bf86fe4984523b086733320` روی سرویس
`fanasa-unified-portal@development` منتشر شد. مسیر current:

```text
/opt/fanasa-unified-portal/development/releases/775605b613de72e89bf86fe4984523b086733320
```

تغییر runtime فقط مدیریت IOException در discovery است. قطع جریان دریافت توکن
یا metadata، Unavailable و خروجی خالیِ ناموفق می‌دهد، نه خطای کل صفحه یا
metadata ناقص. workspace مستقلاً تأییدشده حفظ می‌شود و لغو درخواست caller
همچنان انتشار می‌یابد. UI، registry، grants، SSO، تنظیمات محیط و production تغییر نکردند.

## ساخت و آزمون

سورس release از git archive همان کامیت، همراه props و NuGet config آن، جدا شد؛
هیچ فایل dirty مربوط به Workspace یا کار مالکان دیگر وارد artifact نشد.
SourceLink/SCM queryهای اختیاری هنگام آزمون/ساخت محلی خاموش بودند؛ تنظیم process
پس از package به مقدار قبلی برگشت. اجرای اولیهٔ .NET بدون نتیجه لغو و موفقیت
فقط از اجرای بعدی با exit 0 گزارش می‌شود.

- ۵۰ بررسی .NET discovery شامل ۷ بررسی HTTP loopback موفق؛ روی سورس فعلی و
  سپس روی سورس isolated تکرار شد. تکرار را دو بار در آمار نمی‌شماریم.
- ۴۹ بررسی membership، consumer scoping و خطاهای Portal روی سورس isolated موفق.
- ۱۹ آزمون Node همان سورس isolated، موفق.
- publish واقعی و ابزار Package-Local موفق؛ DLL و manifest حاضرند.
- جداگانه، ۲۳ آزمون renderer نمونهٔ مصرف موفق؛ اتصال مالی عملیاتی نیست.

manifest، commit فوق و `containsCredentials: false` دارد. appsettings بسته فاقد
client secret است؛ credential مرجع VPS فقط در حافظه خوانده شد. checksum ZIP
پس از انتقال دوباره روی VPS تطبیق داده شد:

```text
3eef4bc8550a9eb7f12aa07627d5df75a75e08d7c180caeede25ba7d7dfcf0f3
```

## انتشار و پذیرش

SSH با known_hosts موجود اعتبارسنجی شد. guard قبل از staging و switch، release
قبلی و تنظیم DiscoveryPath خالی را کنترل کرد. مسیرهای ZIP و symlinkهای احتمالی
اعتبارسنجی شدند. release موجود بازنویسی نشد؛ current با تعویض اتمیک symlink
تغییر کرد و فقط سرویس development restart شد. پذیرش موفق بود؛ rollback لازم نشد.
نسخهٔ قبلی `4f99dc8cc9c866f769a3e79ff0baaadfd6d6e04e` و artifact تشخیصی محفوظ‌اند.

بررسی روی خود VPS پس از انتشار:

- health: HTTP 200 و Healthy.
- پاسخ workspace ناشناس: SignInRequired، no-store، بدون سازمان/محصول خصوصی.
- discovery: NotConfigured و بدون سرویس ساختگی؛ مسیر مرکزی فعال نشده است.
- فایل service-catalog.js سرو شد و HTML هر ۱۴ محل کاتالوگ مراکز را داشت.

HTTPS canonical نیز روی VPS پاسخ 200 داد. تلاش اولیه از رایانه در محیط محدود
شبکه رد اتصال داد؛ DNS با مرجع VPS تطبیق داشت. تکرار همان درخواست خارج از
sandbox و با اعتبارسنجی معمول TLS موفق شد: health 200 / Healthy و workspace
ناشناس 200 / no-store / SignInRequired، discovery برابر NotConfigured و شمار
سازمان/محصول خصوصی صفر. هیچ trust store، DNS، proxy یا سیاست شبکه تغییر نکرد.

این بررسی، اثبات انتشار API مرکزی، ورود واقعی SSO، purchase یا grant واقعی نیست.
نمایش ظاهری تغییر نکرده و در این نوبت پذیرش دیداری تازه ادعا نمی‌شود.

## تحویل و وابستگی‌ها

پیگیری API معرفی با نتیجهٔ موفق ابزار به مالک Fanasa Platform Control Center
تحویل شد؛ هیچ مسیر/DTO/scope قطعی تازه‌ای دریافت نشد. مقدار DiscoveryPath خالی
حفظ شد. endpoint فرضی یا API privilegeدار به جای کاتالوگ عمومی استفاده نشد.

قرارداد مستقل مصرف کامل خوانده و با Workspace تطبیق داده شد؛ پذیرش مشروط،
موارد قابل اقدام، محل اتصال و نیاز به delegation محدود و billing.read در
`USAGE-ADAPTER-REVIEW-2026-10-14.md` آمده‌اند. فایل‌های مشترک Workspace تغییر نکردند.
نمونهٔ mapper وابسته به نوع مرکزی باید در قرارداد انتقال مستقل تطبیق داده شود؛
این انتشار reference جدیدی به Web مرکزی در Portal ایجاد نکرد.

API معرفی عمومی و reader مالی مصرف‌کننده دو وابستگی جدا هستند. تا اعلام و
پذیرش هر یک، اتصال همان مسیر کامل محسوب نمی‌شود.
