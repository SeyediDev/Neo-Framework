# Neo.FanasaIdentity

مرجع هویت مرکزی فناسا برای محصولات Neo، بر بستر Keycloak.

این محصول مالک رمز عبور کاربران نیست؛ Keycloak و در ادامه SSO مرکزی فناسا مالک
احراز هویت هستند. این سرویس فقط subject پایدار، tenant membership و نقش انسانی
را به قرارداد استاندارد تبدیل و در دامنه Identity ثبت می‌کند.

## مرز دامنه‌ها

- `Contracts`: قرارداد claims و API مشترک.
- `Domain`: هویت انسانی، tenant membership و قوانین ثبت idempotent.
- `Application`: use-case ثبت و همگام‌سازی هویت.
- `Infrastructure`: اعتبارسنجی JWT از issuer Keycloak.
- `Api`: endpointهای health، مشاهده هویت جاری و ثبت هویت.

دامنه‌های orchestration، قرارداد و حسابداری نباید entityهای Keycloak را reference
کنند؛ آنها فقط از `sub`، provider، tenant و نقش‌های تأییدشده مصرف می‌کنند.

## پیکربندی فعلی

پیش‌فرض محلی روی realm `neo` در Keycloak VPS تنظیم شده است. برای محیط عمومی باید
`FanasaIdentity:Authority` به hostname HTTPS نهایی SSO تغییر کند و audience برابر
client API ثبت‌شده در Keycloak باشد.

در استقرار فعلی client محافظت‌شده‌ی `neo-api` در realm `neo` ساخته شده است؛ این
client از نوع bearer-only است و برای نگهداری secret یا ورود مستقیم کاربر استفاده
نمی‌شود. clientهای UI هر محصول باید جداگانه و با redirect URL همان محصول ثبت شوند.

## اتصال SSO مرکزی فناسا

پس از تحویل issuer/client registration از تیم امنیت فناسا، federation OIDC یا
SAML در realm `neo` ثبت می‌شود. claimهای `tenant`، `tenants`، `human_role` و
`sub` قرارداد مشترک تمام سامانه‌ها هستند. ایجاد کاربر در سامانه‌های دیگر باید از
endpoint ثبت هویت همین محصول یا provisioning رسمی انجام شود، نه از Keycloak Admin
API.
