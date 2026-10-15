# پاسخ حداقلی فضای کار — ۱۵ اکتبر ۲۰۲۶

## اصلاح

پاسخ `GET /?handler=Workspace` قبلاً `PlatformProductView` را مستقیماً serialize
می‌کرد. این DTO انتقال مرکزی، علاوه بر اطلاعات کارت، `Audience`، `Repository`،
تننت تولیدکننده، `Owner` و `Attributes` دلخواه داشت. نیاز نداشتن رابط به این
فیلدها جلوی ارسال آن‌ها به مرورگر را نمی‌گرفت.

اکنون قرارداد مرورگر فقط پنج فیلد `id`، `key`، `displayName`، `center` و `url`
دارد. URL نیز قبل از serialize در سرور پالایش می‌شود؛ نشانی با scheme نامعتبر
یا userinfo به null تبدیل می‌شود. مدل مرکزی، API مرکز راهبری و مجوز ورود تغییر
نکردند. صفات جدیدی که بعداً به DTO مرکزی افزوده شوند خودکار وارد پاسخ مرورگر نمی‌شوند.

تست مدل و HTTP واقعی loopback، پاسخ شخصی `no-store`، حذف markerهای داخلی و
حفظ هویت و URL محصول مجاز را بررسی می‌کنند. هویت، محصول و transport این
تست‌ها مصنوعی‌اند؛ هیچ کلید یا دادهٔ واقعی در fixture قرار ندارد.

## وابستگی مرکزی و هماهنگی

بررسی تازه در `2026-10-15T10:38:20Z`، با TLS معتبر و Host/SNI رسمی روی loopback
VPS، همان نتیجه را داشت: health برابر 200؛ published-services،
application-tenants و usage-report برابر 404؛ products ناشناس برابر 401.
وجود سورس reader یا 401 محصولات، سازگاری یا مجوز consumer را ثابت نمی‌کند.

در همین نوبت، با اجازهٔ قبلی کاربر، پیام ابزار با پاسخ موفق برای چت مالک
`Fanasa Platform Control Center` ارسال شد: ناسازگاری products/consumer، مسیر
سازمان‌ها، activation کاتالوگ و نیاز credential انسانی مالی گزارش شدند.
این تحویل، تکمیل API، تصویب provisioning یا انتشار مرکزی نیست. وضعیت آخر چت
مالک، کار فعال و نیاز به تأیید در همان چت بود؛ به‌جای کاربر تأییدی انجام نشد.

فایل‌های dirty مربوط به Workspace، صفحهٔ خدمات سازمان و SSO و staging چت‌های
دیگر در این اصلاح وارد نمی‌شوند. هیچ grant یا محصول ساختگی ایجاد نشده است.

نام مستند client ورود `fanasa-unified-portal-web` است. کد ورود فعلی فقط
`openid profile` درخواست می‌کند و `SaveTokens=false` دارد؛ شاهد مصوبی برای
scope مالی downstream نداریم. این اصلاح scope، audience یا ذخیره‌سازی توکن را
تغییر نمی‌دهد. اعلام تکمیل آزمون‌های محلی reader توسط مالک مرکزی، از استقرار
و پذیرش هویت انسانی آن جداست.

## نتیجهٔ آزمون

وب Release کامپایل شد. ۷۸ بررسی discovery/HTTP/redirect، ۴۹ رگرسیون
سازمان/محصول و ۱۹ آزمون JavaScript موفق شدند؛ مجموع ۱۴۶ بررسی این مرحله.
دو suite دات‌نت از سورس isolated بدون تغییرات dirty Workspace اجرا شدند.
داده‌های آزمون مصنوعی‌اند. `NuGetAudit=false` برای build محلی بود و نتیجهٔ
اسکن آسیب‌پذیری ادعا نمی‌شود. این پوشش، اتصال واقعی grant یا گزارش مالی نیست.

## اصلاح بستهٔ ویندوز به لینوکس

نخستین تلاش انتشار، پیش از تغییر release فعال با `Unsafe archive path` رد شد.
بسته‌ساز Windows PowerShell با `Compress-Archive` مسیرهایی مانند
`web\appsettings.json` تولید کرده بود. اعتبارسنجی لینوکس درست آن را رد کرد؛
این کنترل تضعیف نشد. بستهٔ اول و release قبلی برای تشخیص/بازگشت محفوظ ماندند.

بسته‌ساز اکنون از ZipArchive با مسیرهای نسبی استاندارد `/`، درج فایل‌های dot،
رد symlink/reparse point و نهایی‌سازی atomic بدون overwrite استفاده می‌کند.
۸ آزمون بسته با publish مصنوعی موفق شدند؛ این آزمون‌ها جای build و بررسی
بستهٔ واقعی روی لینوکس را نمی‌گیرند.

## انتشار توسعه و پذیرش

- اصلاح پاسخ: کامیت `f1ef75ceea3f5e62497e4b88b0b4a1909a8a6249`.
- اصلاح ZIP و release فعال: `394fd730b618372d1b083d34e7f94c5d5f4518ba`.
- SHA256 بستهٔ قابل‌حمل:
  `b351dc58be03b46fff723068989be76b302f9d84070a7b14ba14433d8c3d66e5`.
- manifest همان commit را دارد و `containsCredentials=false` است؛ appsettings
  بسته فاقد secret است. محتوا از سورس isolated و ابزار کامیت‌شده ساخته شد؛
  فایل‌های dirty Workspace در بسته نیستند.
- فعال‌سازی پس از تطبیق checksum، مسیرهای ZIP، manifest، نبود secret و release
  قبلی به‌صورت atomic انجام شد. فقط `fanasa-unified-portal@development` بازراه‌اندازی
  شد. release قبلی `77f043fb2ba2f892022b00bc03ba1e6f696a6d96` برای rollback محفوظ است.
- probe VPS، health، `no-store`، عدم افشای سازمان/محصول به ناشناس، script کاتالوگ
  و ۱۴ mount مرکز را تأیید کرد. اتصال کاتالوگ همچنان `NotConfigured` است.
- HTTPS رسمی `https://hub.fanasa.net.local/health/live` از رایانه 200/Healthy و
  `/?handler=Workspace` برابر 200، `no-store`، `SignInRequired`، صفر
  سازمان/محصول و discovery برابر `NotConfigured` بود. اجرای sandbox ابتدا رد
  اتصال داد؛ همان درخواست بدون تغییر TLS/trust در اجرای مجاز موفق شد.

۱۴۶ بررسی وب/HTTP/JS و ۸ آزمون بسته، مجموعاً ۱۵۴ بررسی موفق این مرحله بودند.
حساب واقعی دارای grant، خرید، گزارش مصرف و اتصال مالی زنده در این انتشار
پذیرفته نشده‌اند؛ وابستگی مرکزی 404 و تنظیم هویت انسانی مالی همچنان باقی‌اند.
سامانه مرکزی، SSO، grantها، production، بانک و تنظیمات محیطی تغییر نکردند.
