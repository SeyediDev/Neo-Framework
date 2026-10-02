# SSO مرکزی فن‌آسا

این پورتال یک OIDC client مستقیم برای realm `fanasa` است. Keycloak تنها مالک
کاربران، رمز، MFA و session مرکزی است؛ این محصول جدول کاربر یا صفحه‌ی ورود
موازی ندارد.

## تنظیمات

- `Authentication__Authority`: issuer واقعی `https://<identity-host>/realms/fanasa`؛
- `Authentication__ClientId`: `fanasa-unified-portal-web`؛
- `Authentication__ClientSecret`: فقط در صورت confidential بودن client و فقط از secret manager.

برای local، callbackهای زیر در client ثبت شده باشند:

- `http://127.0.0.1:5190/signin-oidc`
- `http://localhost:5190/signin-oidc`

در production فقط HTTPS و hostname نهایی مجاز است. `redirect_uri`، logout URL و
audience باید در Keycloak دقیق و allow-list شده باشند. access token در browser یا
local storage ذخیره نمی‌شود.
