# پنجره واحد فن‌آسا

درگاه برندشده‌ی دسترسی به محصولات فن‌آسا. این محصول فقط ناوبری و session وب را
مدیریت می‌کند و هیچ منطق دامنه‌ای از محصولات را در خود کپی نمی‌کند.

محصولات فعلی:

- مدیریت کار ایجنتیک؛
- مدیریت قرارداد.

احراز هویت مستقیماً با OIDC به realm مرکزی `fanasa` در Keycloak انجام می‌شود.
برای این درگاه client مستقل `fanasa-unified-portal-web` استفاده می‌شود؛ صفحه‌ی
ورود واسط یا user store جداگانه وجود ندارد.

تنظیمات:

- `Authentication__Authority`
- `Authentication__ClientId`
- `Authentication__ClientSecret` (فقط برای confidential client)
- `Products__WorkManagementUrl`
- `Products__ContractManagementUrl`

در production مقدار Authority باید HTTPS عمومی باشد. URL هر محصول از تنظیمات
خوانده می‌شود و در source hard-code نشده است.
