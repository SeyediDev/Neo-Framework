# اتصال حساب سازمان به Portal

پایهٔ اولیهٔ کپی ایزوله: `98ecb758`، شاخهٔ تحویل `codex/payg-billing-20261024`.
ادغام نهایی روی `4b3cfbccedac2facb356e3742f60387a5f28bad2` با commit کد
`6975719073f8dd644d58d715f8c1b81091ba09e8` انجام شد.
کد اتصال و نمایش در کپی ایزوله پیاده شده است؛ تنظیمات پیش‌فرض خاموش‌اند.
هیچ استقرار، ورود واقعی، درخواست مالی به Gateway واقعی یا تغییر scope/client/grant
در این کار انجام نشده است.

## مجوز و سابقهٔ بازبینی

بازبینی خودکار ابتدا patch ثبت auth event و reader شبکه در Program را با این
دلیل رد کرد: «این تغییر اتصال فعالِ نشست به Gateway را در Portal برقرار می‌کند
و access token انسانی را برای خواندن دادهٔ مالی ارسال می‌نماید؛ مقصد و افشای این
credential در درخواست کاربر به‌طور مشخص مجاز نشده است.» patch ردشده اعمال نشد؛
تا روشن‌شدن مجوز فقط parser، renderer و آزمون‌های آفلاین ادامه یافتند.

سپس کاربر در ۲۰۲۶/۱۰/۲۴ برای انتقال توکن نشست SSO فقط از سرور به
`https://api.fanasa.net.local` در کپی ایزوله، بدون فعال‌سازی زنده، پاسخ صریح
«بله، فقط همین Gateway» داد. پس از این پاسخ، patch دوباره با مقصد ثابت ارائه،
تأیید و اعمال شد. این پاسخ مجوز انتشار یا تغییر دسترسی زنده نیست.

## مسیر اجرا

- `ConsumerBillingOptions`: feature باید صریحاً روشن و issuer/audience/scope
  مصوب فراهم باشد. هم هنگام خواندن config و هم در IsConfigured، تنها
  `https://api.fanasa.net.local:443/` بدون userinfo/path/query/fragment پذیرفته
  می‌شود. credential یا origin کاتالوگ fallback نیست.
- `ConsumerBillingOidc`: access token برگشتی همان OIDC callback معتبر، با امضا،
  issuer، audience، client، scope، exp/auth_time، sub و sid تطبیق دارد.
  client_credentials رد می‌شود. SaveTokens=false و scopeهای درخواست ورود فعلی
  حفظ شده‌اند. اگر realm توکن مالی مصوب صادر نکند، خواندن مالی غیرفعال می‌ماند.
- `ConsumerBillingSessionVault`: bearer فقط در حافظهٔ سرور، حداکثر پنج دقیقه و
  حداکثر ۲۰۴۸ نشست نگهداری می‌شود. ticket محافظت‌شده فقط handle تصادفی دارد.
  provider هر بار cookie احرازشده، principal جاری، sub/sid و expiry را تطبیق
  می‌دهد. logout ticket اصلی را می‌خواند و handle را پاک می‌کند؛ به Properties
  خالی فراخوانی SignOut متکی نیست. restart/expiry نیازمند ورود دوباره است.
- `GatewayWorkspaceConsumerBillingReader`: دو GET محدود، بدون redirect، cookie،
  ambient credential، retry یا subject ارسالی؛ timeout کل ۳۰ ثانیه، پاسخ JSON و
  no-store، حد حجم و تطبیق envelope/subject/consumer/period. پس از خواندن، اعتبار
  نشست دوباره بررسی می‌شود. توکن/subject وارد browser DTO نمی‌شود.
- `WorkspaceModel`: خواندن فقط پس از نشست انسانی، فهرست سازمان‌های مجاز، انتخاب
  سازمان و تطبیق scope کاتالوگ انجام می‌شود. تغییر سازمان، پاسخ consumer دیگر و
  حالت ناشناس دادهٔ مالی قبلی را پاک می‌کنند. Preview وارد reader واقعی نمی‌شود.

مسیرهای Gateway فقط قراردادهای موجود مرکزی‌اند:

- `GET /api/accounting/v1/consumers/{consumerTenantId:D}/usage-report?from=...&to=...`
- `GET /api/accounting/v1/consumers/{consumerTenantId:D}/account`

مرکز راهبری مجوز زندهٔ billing.read، عضویت فعال، client/scope و نشست انسانی را
مستقل بررسی می‌کند. توکن platform.catalog مجوز خواندن مالی نیست.

## نمایش

مصرف، مانده و بدهی از رشته‌های decimal دقیق می‌آیند؛ تبدیل Number یا قیمت‌گذاری
مجدد وجود ندارد. حساب ثبت‌نشده مبلغ نامعلوم دارد؛ حساب ثبت‌شدهٔ صفر واقعاً صفر
نشان می‌دهد. ردیف legacy مقدار مصرف «نامعلوم» دارد و quota حدس زده نمی‌شود.

مبلغ ثابت صورتحساب، بستانکاری، پرداخت و مانده جدا هستند. حالت postpaid وضعیت‌های
issued/partiallyPaid/paid/credited دارد. حالت payg فقط statement است؛ صورت مصرف
و عبارت «از اعتبار کسر شده» نشان می‌دهد و ستون پرداخت/مانده برای پرداخت دوم ندارد.

دو renderer از textContent، RTL، aria-live و table قابل پیمایش با صفحه‌کلید
استفاده می‌کنند. CSS از tokenهای فعلی Portal استفاده می‌کند؛ شعار، راهنمای
معماری، credential یا توضیح توسعه در UI اضافه نشده است.

## اعتبارسنجی محلی

- Release Portal: صفر هشدار و خطا.
- harness مستقل BillingIntegration.Tests: ۶۷ بررسی موفق روی OIDC امضاشدهٔ مصنوعی،
  cookie/vault/provider، Gateway حافظه‌ای، projection واقعی مصرف و حساب، مقدار
  بزرگ، expiry/logout، scope، subject و مقصد ثابت. آزمون نهایی پس از اصلاح event
  خروج، با SignOutProperties خالی نیز موفق شد.
- تست موجود Portal.Tests: ۱۲۵ بررسی موفق، شامل اصلاح upstream برای تمایز
  تاریخچهٔ ناموجود و تاریخچهٔ خالی؛ Program تست مشترک تغییر نکرده است.
- Node Workspace: ۳۱ گروه موفق، شامل نگه‌داشتن وضعیت مستقل تاریخچه و پاک‌سازی
  گزارش مالی قبلی در loading، خطا، تغییر سازمان و پاسخ consumer دیگر.
- Node renderer حساب: ۱۴ گروه موفق.
- Chromium آفلاین با CSS واقعی Portal: ۷ گروه موفق در ۱۴۴۰، ۳۹۰ و ۳۲۰ پیکسل،
  بدون درخواست شبکه یا خطای صفحه. تصویر ۳۲۰ پیکسل شخصاً بررسی شد و اندازهٔ مبلغ
  و شکست خط ارز در کارت حساب اصلاح و دوباره آزموده شد.
- BillingWorkspace.Tests مستقل: ۱۴ بررسی موفق Bootstrap/PageModel شامل سازمان
  مجاز، تغییر سازمان/subject، پاسخ consumer دیگر، جداسازی preview و مستقل‌بودن
  خطای تاریخچه از گزارش مالی معتبر.

در اجرای نهایی Chromium 156، هر ۷ گروه UI با صفر network/page error موفق شدند،
اما بستن فرایند Chrome متوقف شد. تنها یک اصلاح هدفمند harness انجام شد:
بستن page/context، مهلت ده‌ثانیه‌ای برای هر مرحلهٔ shutdown و کنترل child process
اختصاصی. در همان یک retry نیز ۷ گروه گذشتند ولی پایان فرایند Chrome timeout
داد و harness با exit 1 خاتمه یافت. اجرای اولیه فقط برای Node و فرزندان Chrome
متعلق به همین آزمون با PID/command تأییدشده پاک‌سازی شد. در retry، fallback خود
harness اجرا شد؛ تلاش پاک‌سازی بیرونی بعدی به علت نبودن PID تأییدشده متوقف شد
و هیچ PID دیگری لمس نشد. شاهد هر دو اجرا و cleanup حفظ شد. خروج موفق فرایند
مرورگر ادعا نمی‌شود؛ کد محصول تغییر نکرد و تلاش دیگری انجام نشد.

لاگ‌های نهایی و manifest هش‌ها در `.build/payg-billing-20261024/portal-*` هستند.
`portal-final-verification.json` شمارش‌ها، محدودیت shutdown، هش DLL میزبان و
برابری نسخهٔ DLL در خروجی دو تستی را که واقعاً میزبان را reference می‌کنند ثبت
می‌کند. harness مالی ۶۷ بررسی، sourceها را link می‌کند و Web DLL را load نمی‌کند؛
نوع شواهد آن صریحاً مستقل و linked-source ثبت شده است.

SHA256 میزبان نهایی Portal:
`40fd81778cf39c76ef21a6491ce74ecffb9e968dd6e738b1d965282314293d8b`.
manifest سورس و تنظیمات build مرتبط در `portal-source-sha256.json` ثبت شده است.

در ادغام، `JourneyStatus` و `BillingReadStatus` مستقل باقی ماندند. هنگام
refresh، `consumerUsage` و `consumerAccount` صریحاً null می‌شوند؛ کارت مبلغ و
rendererها نیز loading، وضعیت کاتالوگ و consumer فعال را پیش از نمایش بررسی
می‌کنند. گزارش مالی آماده، سابقهٔ درخواست ناموجود را به سابقهٔ خالی تبدیل نمی‌کند.

خروجی browser در مسیر زیر است:

`products/Fanasa.UnifiedPortal/docs/fixtures/consumer-billing-2026-10-24/result.json`

اجرای harnessها از root کپی ایزوله، با .NET 10 و packageهای cache:

```powershell
dotnet build products/Fanasa.UnifiedPortal/src/Fanasa.UnifiedPortal.Web/Fanasa.UnifiedPortal.Web.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
dotnet build products/Fanasa.UnifiedPortal/tests/BillingIntegration.Tests/BillingIntegration.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
dotnet products/Fanasa.UnifiedPortal/tests/BillingIntegration.Tests/bin/Release/net10.0/BillingIntegration.Tests.dll
dotnet build products/Fanasa.UnifiedPortal/tests/BillingWorkspace.Tests/BillingWorkspace.Tests.csproj -c Release --no-restore -p:BuildProjectReferences=false -m:1 -p:UseSharedCompilation=false
dotnet products/Fanasa.UnifiedPortal/tests/BillingWorkspace.Tests/bin/Release/net10.0/BillingWorkspace.Tests.dll
node products/Fanasa.UnifiedPortal/tests/consumer-billing.test.cjs
node products/Fanasa.UnifiedPortal/tests/consumer-billing-browser-check.cjs
```

برای QA مرورگر، FANASA_UI_PLAYWRIGHT_MODULE و FANASA_UI_BROWSER_EXECUTABLE باید به
نصب موجود محلی اشاره کنند؛ script چیزی نصب نمی‌کند و همهٔ درخواست‌های صفحه را
abort می‌کند. fixtureها ساختگی‌اند و سند مالی واقعی نیستند.

## فعال‌سازی بعدی

ConsumerBilling:Enabled=false در appsettings است. GatewayOrigin/Audience/Scope
خالی‌اند. مالک محیط باید origin ثابت، audience و scope از قبل مصوب، صدور human
session token با sub/sid صحیح، مسیر Gateway و مجوز زندهٔ billing.read را تأیید
کند. این بسته realm، scope، client، grant، routing زنده یا secrets را تغییر نداده
است. مانع فعلی فعال‌سازی محیط است؛ کد اتصال محلی و تست‌ها جای شاهد اجرای زنده نیستند.
