# مهار redirect در اتصال سرویس — ۱۴ اکتبر ۲۰۲۶

## تغییر و منشأ

کامیت `77f043fb2ba2f892022b00bc03ba1e6f696a6d96` فقط ثبت HTTP clientهای مرکزی،
آزمون transport و مستندات همین قابلیت را تغییر می‌دهد. هر سه کلاینت token،
workspace و discovery اکنون redirect خودکار را دنبال نمی‌کنند. سقف زمان‌های
۱۵، ۱۵ و ۳۰ ثانیه حفظ شده است. جریان انسانی OIDC/PKCE، تنظیمات IdP، grantها،
registry، وب Workspace و production تغییر نکرده‌اند.

قاعدهٔ تفکیک هویت سرویس از ورود انسان در اسکیل اتصال SSO رعایت شد. پنجرهٔ واحد
IdP یا مجوز تازه نمی‌سازد. تغییر قرارداد خارجی، MCP یا نیاز به تغییر Skill
ایجاد نشده؛ سیاست transport و دستور آزمون در SERVICE-DISCOVERY.md به‌روز شدند.

## آزمون و ساخت

- ۷۲ بررسی discovery شامل ۲۲ بررسی جدید redirect، موفق؛ اجرای تکراری روی
  snapshot مستقل نیز موفق بود. تکرار را در آمار دو بار نمی‌شماریم.
- ۴۹ رگرسیون فضای کار روی همان snapshot مستقل موفق.
- ۱۹ آزمون Node نمایش کاتالوگ و client فضای کار موفق؛ فایل‌های JS تغییر نکردند.
- در مجموع ۱۴۰ بررسی؛ transport و هویت‌های آزمون مصنوعی‌اند. هیچ credential
  عملیاتی در آزمون‌ها وارد نشد و میزبان‌های HTTP فقط loopback و موقت‌اند.

کنترل آزمون ثابت کرد runtime نصب‌شده با تنظیم پیش‌فرض، بدنهٔ POST شامل secret
مصنوعی را روی 307 به مبدأ دوم می‌فرستد. ثبت واقعی کلاینت‌های محصول جلوی 307 و
308، هم‌مبدأ و مبدأ متفاوت، را در چهار مرحلهٔ token/discovery/membership/products
گرفت. پاسخ مستقیم موفق همچنان catalog و grant تأییدشده را تولید می‌کند.

اجرای اولیهٔ build در sandbox به منع نوشتن فایل موقت برخورد کرد و نتیجهٔ
موفق محسوب نشد؛ اجرای مجاز بعدی موفق شد. در ساخت مستقل query اختیاری SCM و
SourceLink خاموش بود. NuGetAudit در restore مستقل خاموش بود؛ این شواهد، ادعای
اسکن آسیب‌پذیری وابستگی‌ها نیستند. هیچ فایل dirty سایر چت‌ها وارد snapshot نشد.
tree مبنای آزمون و بسته با tree کامیت ثبت‌شده تطبیق داده شد.

artifact:

```text
.artifacts/portal-redirect-20261014/release/fanasa-unified-portal.zip
SHA256: fde2eac9f0cb226b70695ec8562c54e8df4dacb2c848ebc197b00203180b79c6
```

publish واقعی موفق بود؛ manifest همین commit و `containsCredentials: false`
دارد و appsettings بسته فاقد client secret است. credential SSH فقط در حافظه
خوانده شد و host key با known_hosts موجود اعتبارسنجی شد.

## انتشار توسعه و پذیرش

پیش از switch، دریافت توکن با تنظیمات واقعی سرویس Portal و بدون دنبال‌کردن
redirect، HTTP 200 و فیلد access_token در پاسخ داشت. تنها وجود فیلد توکن
بررسی شد؛ مقدار آن ثبت یا نمایش داده نشد. این نتیجه، تأیید مجوز API مالی یا
کاتالوگ معرفی نیست.

بسته پس از تطبیق checksum و manifest و کنترل مسیرهای ZIP روی VPS مستقر شد:

```text
fanasa-unified-portal@development
/opt/fanasa-unified-portal/development/releases/77f043fb2ba2f892022b00bc03ba1e6f696a6d96
```

current با switch اتمیک تغییر کرد. فقط سرویس development پورتال restart شد؛
environment file تغییر نکرد. نسخهٔ `775605b613de72e89bf86fe4984523b086733320`
برای rollback باقی ماند و rollback لازم نشد.

پذیرش روی VPS موفق:

- health: HTTP 200 / Healthy.
- پاسخ فضای کار ناشناس: no-store / SignInRequired؛ بدون سازمان یا محصول خصوصی.
- discovery: NotConfigured، بدون metadata یا سرویس ساختگی.
- اسکریپت کاتالوگ سرو شد و هر ۱۴ محل نمایش مراکز در HTML حاضر بود.

پس از انتشار، readlink و systemd نسخهٔ فعال بالا را تأیید کردند. HTTPS canonical
از همین رایانه نیز با TLS عادی بررسی شد: health و workspace هر دو HTTP 200؛
Healthy / no-store / SignInRequired و سازمان و محصول ناشناس هر دو صفر. درخواست
اول داخل sandbox رد اتصال داد؛ تکرار مجاز خارج از آن موفق شد. DNS، proxy، trust
store یا سیاست شبکه تغییر نکردند.

این تغییر UI ندارد؛ آزمون دیداری تازه یا ورود واقعی کاربر، خرید و grant تازه
ادعا نمی‌شود. قرارداد API معرفی و مسیر acquisition مرکزی هنوز تحویل نشده؛
reader مالی مستقل نیز به قرارداد delegation و `billing.read` نیاز دارد.
مالکیت فایل‌های مرکزی و Workspace چت دیگر حفظ شد. برد عملیاتی در ابزارهای این
نشست موجود نبود؛ این سند ثبت شواهد محلی است، نه تغییر وضعیت task در دیتابیس.

کامیت با index مستقل شامل فقط پنج فایل همین اصلاح انجام شد و فهرست staged
سایر چت‌ها پیش و پس از آن تطبیق داشت. عملیات porcelain نخست طولانی و بدون
نتیجه لغو شد؛ فقط lock خالی index موقت خود این کار پاک شد، نه دادهٔ پروژه.
تلاش بعدی با غیرفعال‌کردن نمایش untracked کامل شد. مسیر جایگزین commit-tree
به‌علت تغییر HEAD متوقف شد و کامیت یا ref اضافی نساخت.
