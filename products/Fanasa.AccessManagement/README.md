# مدیریت دسترسی فن‌آسا

پنل مستقل مدیریت tenant، عضویت، نقش و مجوزهای SaaS. این محصول جایگزین
Keycloak نیست و هویت یا رمز عبور کاربر را ذخیره نمی‌کند.

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
