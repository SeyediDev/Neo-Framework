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

آداپتورهای جدید فایل پایدار تک‌میزبان دارند؛ مرجع tenant/user فعلی هنوز حافظه‌ای
است. اتصال SQL، metering خودکار، درگاه پرداخت و آزمون SSO واقعی تکمیل نشده‌اند.

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
