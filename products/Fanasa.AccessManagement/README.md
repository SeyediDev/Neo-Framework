# مرکز راهبری پلتفرم فن‌آسا (Fanasa Platform Control Center)

مرجع مرکزی ثبت سامانه‌ها، محصول‌ها، کلاینت‌ها، tenant، عضویت، نقش، مجوز و مصرف SaaS. این محصول جایگزین
Keycloak نیست و هویت یا رمز عبور کاربر را ذخیره نمی‌کند.

ماژول ساختار سازمانی در `/Organization` و اکانتینگ مصرفی/پس‌پرداخت در
`/Accounting` اضافه شده است. واحد، نقش سازمانی، سمت، انتصاب زمان‌دار و نسخه‌های
تغییر ثبت می‌شوند؛ مجوز نرم‌افزاری از نقش سازمانی به‌صورت خودکار اعطا نمی‌شود.
اکانتینگ شامل کیف اعتبار، سقف بدهی، تعرفه شاخص مصرف و صورتحساب ماهانه است.
مقایسه دو نسخه چارت، تغییرات واحد، نقش، سمت و انتصاب را در سطح فیلد نشان
می‌دهد؛ نسخه صفر نمای ساختار خالی است. API خواندنی
`GET /api/organization/tenants/{tenantId}/compare?from=0&to=1&offset=0`
با مجوز `organization.read` و اشتراک فعال، حداکثر ۱۰۰ تغییر در هر صفحه
برمی‌گرداند. بازه معکوس یا offset منفی پذیرفته نمی‌شود.
جزئیات API، claimها، subscription gate و محدودیت‌های استقرار در
[تصمیم معماری](docs/IDENTITY-ORGANIZATION.md) آمده است.

```powershell
dotnet run --project products/Fanasa.AccessManagement/tests/Organization.Tests
```

عضویت، پلن، اشتراک، مجوز، چارت و حساب در SQLite پایدار می‌شوند. صفحه
`/Tenancy` مدیریت اعضا و اشتراک را پوشش می‌دهد. با فعال‌سازی
`Organization__MeterChanges=true`، تغییر چارت و مصرف در یک تراکنش ثبت می‌شوند؛
تعرفه `organization.change` و اعتبار کافی باید قبلاً آماده باشند.

مسیر `Fabric__DataDirectory` باید روی volume محلی پایدار با backup باشد.
ابزار backup/verify/restore دو پایگاه با مقصد تازه و کنترل integrity در
[راهنمای بازیابی](docs/RECOVERY.md) آمده است.
انتقال JSON قدیمی فقط با `Fabric__ImportLegacyOnStartup=true` و هویت tenant
موجود انجام می‌شود. زرین‌پال برای شارژ و تسویه با verify سروری اضافه شده است؛
تنظیمات و محدودیت‌ها در [راهنمای زرین‌پال](docs/ZARINPAL.md) آمده است.
dispatcher بیرونی outbox، آزمون SSO واقعی و استقرار چندمیزبان هنوز تکمیل
نشده‌اند. SQL adapter آزمایشی فعال نیست.

## مرز معماری

صفحه `/Governance` برای درخواست ورود/جابه‌جایی/خروج، بررسی مستقل، تعریف
الگوهای نقش و زمان و شبیه‌سازی تصمیم بر مبنای مفاهیم XACML اضافه شده است.
اتصال سیاست به دسترسی زنده و اجرای خودکار درخواست‌ها هنوز فعال نیست؛
دامنه پشتیبانی و مرز فعال‌سازی در [راهنمای راهبری و XACML](docs/GOVERNANCE-XACML.md) آمده است.

صفحه `/Audit` اکنون سوابق واقعی هر سازمان را با فیلتر نوع رویداد و صفحه‌بندی
نمایش می‌دهد. هر دسته به مجوز مشاهده همان بخش وابسته است؛ جزئیات در
[راهنمای رویدادها](AUDIT.md) آمده است.

- Keycloak در realm `fanasa`: ورود، MFA، session و شناسهٔ پایدار `sub`؛
- این محصول: tenant، membership، product role، grant و audit؛
- API هر محصول: بررسی issuer، audience، scope و grant نهایی.

اتصال وب مستقیم با OIDC Authorization Code + PKCE انجام می‌شود. token در
مرورگر یا local storage ذخیره نمی‌شود و فقط session cookie امن استفاده می‌شود.

## تنظیمات

```text
Authentication__Authority=https://<identity-host>/realms/fanasa
Authentication__ClientId=fanasa-access-management-web
Authentication__ClientSecret=<server-side-only-if-confidential>
```

client مستقل `fanasa-access-management-web` باید با callback دقیق محیط اجرا در
Keycloak ثبت شود. در production فقط HTTPS مجاز است.
