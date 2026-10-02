# بک‌لاگ SSO و احراز هویت فن‌آسا

## انجام‌شده

- [x] میزبان مستقل `Fanasa.UnifiedPortal.Web` با OIDC Authorization Code + PKCE
- [x] client مستقل `fanasa-unified-portal-web` در realm `fanasa`
- [x] callbackهای local روی پورت ۵۱۹۰
- [x] تم برند فن‌آسا در Keycloak و فعال‌سازی آن برای realm
- [x] حذف صفحه و سرویس واسط SSO
- [x] ثبت Skill اتصال مستقیم SSO برای محصولات بعدی

## باقی‌مانده برای production

- [ ] تعیین hostname عمومی HTTPS برای Keycloak و پورتال
- [ ] جایگزینی callbackهای local با callback و logout URLهای دقیق production
- [ ] دریافت و ثبت claimهای رسمی tenant، سازمان و نقش انسانی از امنیت فن‌آسا
- [ ] تعیین audience/scope نهایی APIها و تست JWT با issuer واقعی
- [ ] فعال‌سازی MFA و سیاست‌های password/session/timeout طبق سیاست سازمان
- [ ] ثبت secretهای confidential client در secret manager، در صورت نیاز
- [ ] اجرای تست پذیرش با یک کاربر واقعی، کاربر بدون grant و tenant اشتباه
- [ ] تنظیم reverse proxy و TLS؛ پورت داخلی Keycloak مستقیماً عمومی نشود

این موارد خارج از کد پورتال نیستند و بدون hostname، claim و سیاست رسمی نباید
حدس زده یا در source hard-code شوند.
