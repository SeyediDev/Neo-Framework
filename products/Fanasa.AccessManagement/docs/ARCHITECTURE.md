# معماری مدیریت دسترسی

`Fanasa.AccessManagement.Web` فقط پنل احراز هویت‌شده است. دامنهٔ دسترسی باید
در سرویس مستقل خود پیاده شود و به دامنهٔ Keycloak یا دامنهٔ محصولات وارد نشود.

## مدل دادهٔ پیشنهادی

- `Tenant`: سازمان و مرز مالکیت داده؛
- `TenantMembership`: اتصال `KeycloakSubject` به tenant؛
- `Product`: محصول ثبت‌شده و audience آن؛
- `Role`: نقش محصولی؛
- `Permission`: مجوز اتمیک؛
- `RolePermission`: اتصال نقش به مجوز؛
- `MembershipGrant`: نقش/مجوز اعطاشده به membership؛
- `AccessAuditEntry`: رخداد تغییر دسترسی با subject و tenant.

هیچ جدول password، refresh token یا کپی پروفایل کاربر ایجاد نمی‌شود. `sub`
تنها شناسهٔ مرجع کاربر است و نمایش نام از claimهای session یا پروفایل Keycloak
می‌آید.
