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

## مرجع محصول و SaaS

مرکز دسترسی، مرجع حقیقت `Product`، `ProductClient` و `TenantSubscription` است.
هر محصول با یک `Key` یکتا، audience، repository، ویژگی‌های اتصال و دقیقاً یک
مرکز رسمی فن‌آسا ثبت می‌شود؛ سامانه‌های دیگر نباید این اطلاعات را محلی کپی یا
با نام دیگری بازتعریف کنند. کلاینت فقط metadata اتصال و scopeهای مجاز را ثبت
می‌کند و secret در این سامانه نگهداری یا نمایش داده نمی‌شود.

لایهٔ تجاری نیز از دامنهٔ قرارداد جداست: `PricingPlan` و `PricingRule` مدل
محاسبهٔ محصول‌اند، `TenantSubscription` رابطهٔ SaaS است، `UsageEvent` مصرف
idempotent را ثبت می‌کند و `UsageCharge`/`RevenueSnapshot` برای محاسبه و تحلیل
درآمد هستند. قرارداد تنها entitlement و lifecycle را به Access می‌رساند؛
خود قرارداد، مشتری و قیمت‌گذاری قراردادی در دامنهٔ مدیریت قرارداد باقی می‌ماند.

APIهای مرجع فعلی:

- `GET/POST /api/access/catalog/products`
- `GET/POST /api/access/catalog/products/{productId}/clients`
- `GET/POST /api/access/catalog/plans` و `.../rules`
- `POST /api/access/subscriptions`
- `POST /api/access/contract-events`
- `POST /api/access/usage-events` و `.../charges`
- `GET /api/access/catalog/products/{productKey}/revenue`

پیاده‌سازی فعلی برای توسعه با adapter حافظه‌ای است؛ پیش از استقرار production
باید adapter پایدار Neo/EF، migration، outbox رویداد و سیاست‌های tenant
isolation به آن متصل شوند.

هیچ جدول password، refresh token یا کپی پروفایل کاربر ایجاد نمی‌شود. `sub`
تنها شناسهٔ مرجع کاربر است و نمایش نام از claimهای session یا پروفایل Keycloak
می‌آید.
