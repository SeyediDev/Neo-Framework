---
name: fanasa-sso-integration
description: Connect a web product directly to the central Fanasa Keycloak realm using secure OIDC; use this for new products, panels, and human-role sign-in without creating an intermediary login page or a second user store.
metadata:
  short-description: Direct Fanasa Keycloak SSO integration
---

# اتصال SSO مرکزی فن‌آسا

این Skill قرارداد ثابت اتصال سامانه‌ها به هویت مرکزی فن‌آسا است. Keycloak در
realm `fanasa` تنها IdP و مالک کاربران، ورود، MFA، session و نقش‌های هویتی است.
سامانه‌ی محصول نباید صفحه‌ی ورود واسط، Identity API مستقل، جدول password یا
کپی کاربر بسازد.

## الگوی اجباری

- هر محصول یک OIDC client مستقل و نام‌گذاری‌شده دارد؛ client محصول دیگری را
  دوباره استفاده نکن.
- ورود از مسیر `/login` خود محصول با Authorization Code و PKCE آغاز می‌شود و
  مستقیماً به issuer زیر می‌رود:
  `https://<identity-host>/realms/fanasa`.
- برای web app، access/ID token در browser یا local storage ذخیره نشود؛ فقط
  cookie امن و HttpOnly و ترجیحاً server-side ticket/session استفاده شود.
- `redirect_uri` و `post_logout_redirect_uri` باید دقیق، محدود و HTTPS باشند؛
  return URL فقط local/allow-listed باشد.
- APIها JWT را با issuer، audience، امضا، انقضا و scope/role بررسی کنند. ورود
  موفق به‌تنهایی مجوز دسترسی به tenant یا API نیست.
- برای tenant و نقش انسانی از claimهای پایدار IdP و membership/grant داخلی
  استفاده کن؛ credential یا مدل‌های Keycloak را وارد domain محصول نکن.
- service identity و اتصال Harness/agent از ورود انسان جدا باشد و با client
  credential یا روش امن سرویس‌به‌سرویس مدیریت شود؛ token سرویس را با session
  کاربر قاطی نکن.

## تنظیمات حداقلی هر محصول

تنظیمات را از environment/secret manager بخوان، نه از source:

- `Authority`: issuer واقعی realm `fanasa`؛ در production HTTPS اجباری؛
- `ClientId`: client اختصاصی همان محصول؛
- `ClientSecret`: فقط برای confidential client و فقط سمت سرور؛
- scopeهای `openid profile` و scopeهای API مصوب؛
- audience/resource و claim mapping مورد انتظار API؛
- callback و logout URLهای ثبت‌شده برای هر محیط.

در local development می‌توان issuer loopback را با HTTPS metadata خاموش‌شده
استفاده کرد، اما این استثنا نباید به production یا reverse proxy منتقل شود.

## پذیرش قبل از تحویل

1. کاربر ناشناس از `/login` مستقیماً به صفحه‌ی Keycloak با تم فن‌آسا می‌رود؛
   هیچ صفحه‌ی login تکراری بین محصول و Keycloak نمایش داده نمی‌شود.
2. callback فقط برای client و URL ثبت‌شده قبول می‌شود و CSRF/state/PKCE فعال
   است.
3. کاربر پس از ورود، claim subject یکتا، tenant membership و نقش مجاز دارد؛
   کاربر بدون grant به API پاسخ 403 می‌گیرد.
4. خروج، هم session محصول و هم session مرکزی را طبق قرارداد logout خاتمه می‌دهد.
5. هیچ رمز، access token، refresh token یا secret در HTML، log، task، Git یا
   local storage دیده نمی‌شود.
6. discovery issuer، امضای JWT، audience، expiration، scope و callback با تست
   واقعی همان محیط بررسی می‌شود.

## تصمیم معماری

صفحه‌ی مستقل SSO فقط در صورت نیاز به landing عمومی یا راهنمای ورود اختیاری
قابل نگهداری است و جزو authentication flow نیست. در حالت عادی آن را نساز یا
حذف کن؛ محصول باید مستقیم به Keycloak متصل شود و خود Keycloak با تم برند فن‌آسا
صفحه‌ی ورود را ارائه کند.
