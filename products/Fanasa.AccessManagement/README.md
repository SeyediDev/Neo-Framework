# راهبری هویت و سازمان فن‌آسا

پنل مستقل مدیریت tenant، عضویت، نقش و مجوزهای SaaS. این محصول جایگزین
Keycloak نیست و هویت یا رمز عبور کاربر را ذخیره نمی‌کند.

ماژول ساختار سازمانی در `/Organization` و اکانتینگ مصرفی/پس‌پرداخت در
`/Accounting` اضافه شده است. واحد، نقش سازمانی، سمت، انتصاب زمان‌دار و نسخه‌های
تغییر ثبت می‌شوند؛ مجوز نرم‌افزاری از نقش سازمانی به‌صورت خودکار اعطا نمی‌شود.
اکانتینگ شامل کیف اعتبار، سقف بدهی، تعرفه شاخص مصرف و صورتحساب ماهانه است.
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
انتقال JSON قدیمی فقط با `Fabric__ImportLegacyOnStartup=true` و هویت tenant
موجود انجام می‌شود. زرین‌پال برای شارژ و تسویه با verify سروری اضافه شده است؛
تنظیمات و محدودیت‌ها در [راهنمای زرین‌پال](docs/ZARINPAL.md) آمده است.
dispatcher بیرونی outbox، آزمون SSO واقعی و استقرار چندمیزبان هنوز تکمیل
نشده‌اند. SQL adapter آزمایشی فعال نیست.

## مرز معماری

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
