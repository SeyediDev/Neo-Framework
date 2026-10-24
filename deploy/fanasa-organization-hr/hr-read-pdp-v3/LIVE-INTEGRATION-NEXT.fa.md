# اتصال باقی‌مانده به میزبان واقعی

کد و ۳۴۱ assertion محلی این بسته برای transport و ارزیابی خواندنی آماده‌اند. endpoint در میزبان جاری ثبت نشده و این فایل مجوز عملیاتی یا سند فعال‌سازی نیست. موارد زیر از source میزبان بررسی شده‌اند؛ ساخت دادهٔ نمونه در آزمون جای آن‌ها را نمی‌گیرد.

| بخش | منبع موجود و نحوهٔ مصرف |
|---|---|
| ثبت مرکزی | `products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/PlatformRegistry.cs`، متد `Products()`؛ تطبیق یکتای Id/Key/Audience/Owner/TenantId. DB ثبت محصول جداست؛ capture پیش و پس از خواندن Fabric لازم است. |
| tenant و membership | `PersistentAccessManagement.cs`، متدهای `GetTenants()` و `GetUsers(tenant)`؛ از instance موجود استفاده شود. membership عمر پایان مستقل ندارد؛ lease مشاهده، مجوز دائمی تولید نمی‌کند. |
| client و role | همان سرویس، `GetClients(productId)` و `GetRoles(tenant, productKey)`؛ client فعال برای محصول دقیق، نقش `HrReader` و permission برابر `organization.read` لازم است. |
| grant بومی | `GetGrants(subject)`؛ tenant، subject، permission و expiry جاری باید دقیق باشند. view خواندنی از این grant و assignment نباید grant جدید ایجاد کند. |
| veto قدیمی | `AccessPolicies.cs`، فراخوانی خواندنی `AccessPolicyStore.Evaluate` برای `organization.read`؛ فقط Permit بدون obligation پذیرفته شود. `Authorize()` مناسب این snapshot نیست زیرا outbox می‌نویسد. |
| منبع میزبان | `FabricServices.cs`، ثبت موجود `FabricDatabase` و `PersistentAccessManagement`؛ ساختن `AccessDbContext` تازه مسیر authorization فعال این میزبان نیست. |

سه بخش هنوز باید در میزبان و با منابع معتبر تکمیل شوند:

1. **آماده‌سازی async هویت جاری:** introspection معتبر برای token واقعی، active/expiry/sub/client/hash و mapping یکتای token به consumer، پیش از ورود به snapshot. JWT امضاشده یا عضویت در tenant انتخاب‌شدهٔ route به‌تنهایی این mapping را ثابت نمی‌کند. `ReadCurrent` همگام باقی می‌ماند و در هر فراخوانی lease، revocation و نسخهٔ همان شواهد را کنترل می‌کند؛ HTTP داخل آن یا sync-over-async مجاز نیست.
2. **delegation مخصوص HR3:** سرویس Gateway باید در مرجع مرکزی برای همین audience، scope و عملیات تصمیم‌گیری مجاز باشد. `ContractPolicyGatewaySecurity.cs` موجود مربوط به binding قدیمی است و بدون binding مستقل HR3 مجوز سرویس تازه محسوب نمی‌شود.
3. **publisher مجاز HR3:** `docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/pcc/ContractPolicyStore.cs` فعلی schema v2، `ApiInvoker/organization.write`، owner tenant غیرnull و کلیدهای legacy را publish می‌کند. رکوردهای HR3 باید با validator و selector جدید در همان bucketها، از قرارداد/سیاست/allocation و assignment مصوب واقعی ساخته شوند؛ این بسته writer یا مهاجرت خودکار ندارد.

پس از تکمیل این موارد، تنظیمات فعال caller و subject، listener مستقیم mTLS، certificate pin/trust و اتصال APISIX باید با داده و هویت واقعی آزموده شوند. Gateway باید schema پاسخ، شناسه‌ها، token hash، مسیر، tuple، deadline و دوره را مستقل تطبیق دهد و obligationهای admission/resource را واقعاً انجام دهد. `200` یا Permit به‌تنهایی مجوز ارسال درخواست به HR نیست.

کارهای source این بسته مسیر اجرای زنده، دیتابیس PVC، pipeline انتخاب‌شده یا نسخهٔ فعال را تغییر نمی‌دهند. انتشار source هنوز انجام نشده است؛ درخواست تأیید انتقال بستهٔ قبلی به VPS/GitLab پس از رد بررسی خودکار، باز مانده است.
