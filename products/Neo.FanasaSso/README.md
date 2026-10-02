# Neo.FanasaSso

صفحه SSO مرکزی فناسا برای Neo.

این محصول فقط یک OIDC client است. Keycloak در realm `fanasa` خودِ IdP است و
مالک login، session و کاربران می‌ماند. این پروژه هیچ جدول کاربر، password store،
Identity Provider یا user registry جداگانه‌ای ندارد.

مسیرها:

- `/` صفحه ورود branded فناسا؛
- `/login` شروع Authorization Code + PKCE؛
- `/logout` خروج از session محلی و Keycloak؛
- `/me` مشاهده claimهای کاربر جاری برای تست؛
- `/health` سلامت سرویس.

در محیط عمومی مقدار `FanasaSso:Authority` باید hostname HTTPS عمومی Keycloak و
`ClientId` باید client ثبت‌شده همان host باشد. client فعلی `fanasa-web` فقط برای
محیط VPS و تست bootstrap است.
