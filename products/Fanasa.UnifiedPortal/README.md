# پنجره واحد فن‌آسا

درگاه برندشده‌ی دسترسی به محصولات فن‌آسا. این محصول فقط ناوبری و session وب را
مدیریت می‌کند و هیچ منطق دامنه‌ای از محصولات را در خود کپی نمی‌کند.

محصولات فعلی:

- مدیریت کار ایجنتیک؛
- مدیریت قرارداد.

احراز هویت مستقیماً با OIDC به realm مرکزی `fanasa` در Keycloak انجام می‌شود.
برای این درگاه client مستقل `fanasa-unified-portal-web` استفاده می‌شود؛ صفحه‌ی
ورود واسط یا user store جداگانه وجود ندارد.

تنظیمات:

- `Authentication__Authority`
- `Authentication__ClientId`
- `Authentication__ClientSecret` (فقط برای confidential client)
- `PlatformControlCenter__BaseUrl`
- `PlatformControlCenter__Authority` (در صورت خالی بودن، Authority ورود استفاده می‌شود)
- `PlatformControlCenter__ClientId`
- `PlatformControlCenter__ClientSecret` (فقط در secret store)
- `PlatformControlCenter__Scope` (پیش‌فرض: `platform.catalog`؛ scope اختصاصی خواندن کاتالوگ)

در production مقدار Authority باید HTTPS باشد. سازمان‌ها از
`GET /api/platform/application-tenants?subject=...` و محصولات از
`GET /api/platform/products?subject=...&tenant=...` دریافت می‌شوند. subject فقط از نشست
احراز هویت‌شده استخراج می‌شود؛ انتخاب سازمان به‌تنهایی مجوز ایجاد نمی‌کند.

سازمان تک‌عضوی خودکار انتخاب می‌شود. تغییر سازمان و تازه‌سازی فهرست بدون بارگذاری
مجدد صفحه انجام می‌شود. نبود عضویت، تنظیمات ناقص، خطای مجوز و قطعی سرویس وضعیت‌های
جداگانه دارند. محصولات فقط برای سازمان مجاز و انتخاب‌شده نمایش داده می‌شوند.
ناوبری داخلی منوی بالا در صفحه اصلی نیز همان سند را نگه می‌دارد و انتخاب سازمان،
جست‌وجو و فیلتر را پاک نمی‌کند. دریافت فضای کار در مرورگر، شامل خواندن بدنه پاسخ،
سقف انتظار ۵۰ثانیه‌ای دارد؛ پس از timeout کنترل‌ها آزاد می‌شوند و تلاش مجدد ممکن است.
پاسخ درخواست قدیمی یا لغوشده اجازه بازنویسی درخواست جدید را ندارد.
پارامتر `tenant` سازمان مصرف‌کننده است؛ `TenantId` در مشخصات محصول متعلق به مالک
محصول است و برای فیلتر مصرف‌کننده استفاده نمی‌شود. مرکز راهبری عرضه، عضویت و grant
مصرف‌کننده را بررسی می‌کند. پنجره واحد پاسخ محصولات را به همان سازمان درخواست‌شده
متصل می‌کند؛ بدون انتخاب معتبر، فهرست تجمیعی محصولات درخواست نمی‌شود.
شمارنده فقط پس از دریافت موفق اطلاعات سازمان انتخاب‌شده، تعداد سامانه‌ها را نشان
می‌دهد؛ خطای اتصال یا نیاز به ورود هرگز «۰ سامانه» نمایش داده نمی‌شود. عنوان،
راهنمای انتخاب سازمان و وضعیت ورود در HTML اولیه و پاسخ AJAX از یک مدل مشترک
تولید می‌شوند. هنگام خطا لینک‌های قبلی سامانه‌ها نمایش داده نمی‌شوند.
پاسخ `null`، JSON نامعتبر و رکورد فاقد شناسه/نام، فهرست خالی موفق محسوب نمی‌شوند.
اگر دریافت محصولات شکست بخورد، سازمان‌های تأییدشده در همان درخواست برای تلاش
مجدد حفظ می‌شوند، ولی هیچ لینک محصولی نمایش داده نمی‌شود. پس از دریافت موفق
عضویت خالی، درخواست اضافی دریافت محصولات ارسال نمی‌شود.

آزمون‌های متمرکز:

```powershell
dotnet run --project products/Fanasa.UnifiedPortal/tests/Portal.Tests -c Release
node --test products/Fanasa.UnifiedPortal/tests/portal-client.test.cjs
& products/Fanasa.UnifiedPortal/tests/Test-PortalHttp.ps1 -BaseUrl https://hub.fanasa.net.local
& products/Fanasa.UnifiedPortal/tests/Test-PackageLocal.ps1
```

آزمون HTTP را فقط به origin مورداعتماد خود بدهید. هشت بررسی ناشناس شامل ناوبری
بدون reload در `/` و aliasهای `/Index` و `/index`، بازگشت از صفحه خطا، سلامت،
حفاظت فضای کار ناشناس و فایل‌های JS/CSS است. این آزمون جای پذیرش دیداری موبایل،
ورود واقعی SSO یا بررسی محصولات مجاز سازمان را نمی‌گیرد.

بسته‌سازی محلی فقط در مسیر خروجی و مسیر ZIP تازه مجاز است؛ هیچ پوشه یا بسته قبلی
پاک یا بازنویسی نمی‌شود. پس از شکست publish یا نبود DLL، manifest و ZIP ایجاد
نمی‌شوند و خروجی ناقص برای تشخیص باقی می‌ماند. `Test-PackageLocal.ps1` این guardها
را با publish شبیه‌سازی‌شده می‌سنجد، نه با بیلد واقعی؛ scratch موقت خودش را هم پاک نمی‌کند.
یافته‌های CI متعلق به مالک pipeline در [تحویل انتشار](docs/PIPELINE-HANDOFF-2026-10-10.md)
ثبت شده‌اند؛ اضافه‌شدن تست به محصول، اجرای آن در CI را تضمین نمی‌کند.
برای بسته واقعی ابتدا restore انجام دهید و `GITHUB_SHA` را به کامیت منبع تنظیم کنید:

```powershell
$env:GITHUB_SHA = (git rev-parse HEAD).Trim()
& products/Fanasa.UnifiedPortal/tools/Package-Local.ps1 -OutputDirectory .artifacts/portal-release-unique/package
```

قرارداد استقرار و نیاز هماهنگی مرکز راهبری در
[CATALOG-INTEGRATION.md](docs/CATALOG-INTEGRATION.md) ثبت شده است.
شواهد و محدودیت پذیرش اصلاحات ناوبری، timeout و موبایل در
[UI-RESILIENCE-2026-10-10.md](docs/UI-RESILIENCE-2026-10-10.md) نگهداری می‌شوند.
