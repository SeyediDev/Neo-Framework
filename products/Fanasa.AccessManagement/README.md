# مرکز راهبری پلتفرم فن‌آسا (Fanasa Platform Control Center)

API شخصی اندروید در `GET /api/mobile/catalog/tenants` و `GET /api/mobile/catalog/tenants/{tenantId}/apps` افزوده شده است؛ bearer کاربر با scope مستقل `platform.catalog.self` و public client ثبت‌شده فقط کاتالوگ subject خود را می‌خواند. مسیر delegation سرویس تغییر نکرده است. قابلیت پیش‌فرض غیرفعال است؛ [قرارداد، ثبت مرکزی و نمونه تنظیمات IdP](docs/MOBILE-CATALOG.fa.md) و [آزمون HTTP محلی](tests/MobileApplicationCatalog.Tests/README.md) وضعیت پیاده‌سازی و حدود پذیرش را مشخص می‌کنند. این تغییر ثبت واقعی client، grant، انتشار یا پذیرش ورود دستگاه نیست.

ماژول Contract Policy در DI همین میزبان ثبت شده است: ingress قرارداد با JWT اختصاصی producer، projection/receipt اتمی در Fabric، reader معتبر، سیاست نسخه‌دار مستقل و انتساب صریح نقش محصول. ادامه `0.4.0`، endpoint اختصاصی `POST /api/gateway/decision` و گیرندهٔ مستقیم mTLS/HMAC را اضافه می‌کند؛ guard داخل تراکنش از اجرای درخواست منقضی یا ابطال‌شده جلوگیری می‌کند. فعال‌سازی نیازمند پیکربندی و ثبت‌های معتبر است؛ executor پیش‌فرض Gateway بسته می‌ماند. [راهنمای پایه](../../docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/PCC-INTEGRATION.md) و [اتصال Gateway](../../docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/PCC-GATEWAY.md).

## اطلس معماری

صفحه `/Architecture` از منوی اصلی و نمای کلی قابل دسترسی است. نقشه تعاملی مسئولیت‌ها، نماهای هویت، محصول و آینده، جزئیات المان‌ها، سند معماری و چاپ/PDF را ارائه می‌کند. پیوند مستقیم مانند `/Architecture#system-observe` انتخاب المان را حفظ می‌کند. وضعیت مانیتورینگ و درگاه هدف، چشم‌انداز است و نقشه تله‌متری زنده نیست. لینک ورود محصولات از `PlatformRegistry` با همان محدودیت subject صفحه Platform خوانده می‌شود؛ کاتالوگ موازی یا ثبت خودکار ایجاد نمی‌شود. صفحه زیر سیاست ورود موجود قرار دارد و تغییری در مجوز مقصد نمی‌دهد.

آزمون رفتار انتخاب، پیوند مستقیم، نمای آینده و چاپ: `node products/Fanasa.AccessManagement/tests/architecture-ui.test.cjs`.

مرجع مرکزی ثبت سامانه‌ها، محصول‌ها، کلاینت‌ها، tenant، عضویت، نقش، مجوز و مصرف SaaS. این محصول جایگزین
Keycloak نیست و هویت یا رمز عبور کاربر را ذخیره نمی‌کند.

ماژول ساختار سازمانی در `/Organization` و اکانتینگ مصرفی/پس‌پرداخت در
`/Accounting` اضافه شده است. واحد، نقش سازمانی، سمت، انتصاب زمان‌دار و نسخه‌های
تغییر ثبت می‌شوند؛ مجوز نرم‌افزاری از نقش سازمانی به‌صورت خودکار اعطا نمی‌شود.
اکانتینگ شامل کیف اعتبار، سقف بدهی، تعرفه شاخص مصرف و صورتحساب ماهانه است.
مقایسه دو نسخه چارت، تغییرات واحد، نقش، سمت و انتصاب را در سطح فیلد نشان
می‌دهد؛ نسخه صفر نمای ساختار خالی است. API خواندنی
`GET /api/organization/tenants/{tenantId}/compare?from=0&to=1&offset=0`
با مجوز `organization.read` و اشتراک فعال، حداکثر ۱۰۰ تغییر در هر صفحه
برمی‌گرداند. بازه معکوس یا offset منفی پذیرفته نمی‌شود.
جزئیات API، claimها، subscription gate و محدودیت‌های استقرار در
[تصمیم معماری](docs/IDENTITY-ORGANIZATION.md) آمده است.

تقویم کاری، تعطیلی و پنجرهٔ داده در پنل سازمان و سیاست‌های وابسته به آن‌ها در
راهبری تعریف می‌شوند. نمونهٔ آموزشی «سازمان الگو» و مرز فعال‌سازی محدودیت زمانی
در [راهنمای تقویم و دسترسی زمانی](docs/ORGANIZATION-TEMPORAL-ACCESS.fa.md) توضیح داده شده‌اند؛
اجرای زنده به‌صورت پیش‌فرض خاموش است و فقط برای tenantهای مشخص و مجوزهای
`organization.read` و `organization.write` قابل فعال‌سازی است.

منابع PIP در پنل سازمان با مسیر «تعریف، آزمون قرارداد، فعال‌سازی، دریافت، بررسی هویت و شبیه‌سازی» مدیریت می‌شوند.
[راهنمای عملیاتی PIP](docs/PIP-OPERATIONS.fa.md) و [نمونه‌های سازمان الگو](docs/pip-educational-journey.md)
مرز تازگی داده، سیاست‌های پیش‌نویس و تفاوت آزمون اتصال با دادهٔ قابل استفاده در تصمیم را توضیح می‌دهند.
خواندن صفات HR قرارداد نسخهٔ ۲ و هویت محدود دارد؛ تنظیم آن در [اتصال HR](../../deploy/fanasa-organization-hr/PIP-ATTRIBUTES.md) آمده است.

```powershell
dotnet run --project products/Fanasa.AccessManagement/tests/Organization.Tests
```

عضویت، پلن، اشتراک، مجوز، چارت و حساب در SQLite پایدار می‌شوند. صفحه
`/Tenancy` مدیریت اعضا و اشتراک را پوشش می‌دهد. با فعال‌سازی
`Organization__MeterChanges=true`، تغییر چارت و مصرف در یک تراکنش ثبت می‌شوند؛
تعرفه `organization.change` و اعتبار کافی باید قبلاً آماده باشند.

مسیر نسخه‌دار جدید برای `unit.save` با `UsageMetering__Organization__Enabled=true`
قابل انتخاب است و پیش‌فرض خاموش می‌ماند. نرخ هنگام پذیرش ثابت می‌شود و تغییر چارت،
رسید مصرف، بدهی و رویداد کامل در همان Fabric commit می‌شوند. این گزینه و
`Organization__MeterChanges` هم‌زمان مجاز نیستند. منبع قابل انتخاب قرارداد و تخصیص،
نگاشت تنظیمات یا projection احراز‌شدهٔ ContractPolicy با قیمت نسخه‌دار پایدار است.
تحویل outbox با retry و ack نیز opt-in و پیش‌فرض خاموش است؛ مقصد واقعی متصل نشده است.
تطبیق خواندنی receipt/دفتر/outbox و retry مدیریتیِ شواهد معتبر pending نیز فراهم است؛
retry فقط حسابداری همان رویداد و نرخ تاریخی را اجرا می‌کند. ورودی عمومی مصرف بیرونی
هنوز به هویت producer مورد تأیید و شاهد پایدار پذیرش تاریخی نیاز دارد.
پیش‌نیازها، تنظیمات و بازیابی در [راهنمای مصرف نسخه‌دار](docs/USAGE-ACCOUNTING.md) آمده است.
پنل مدیر پلتفرم در `/UsageAccounting`، گزارش سلامت شواهد، جست‌وجوی مصرف و تلاش
مجدد با دلیل و شناسهٔ ثابت برای پاسخ نامشخص را ارائه می‌کند.

مسیر `Fabric__DataDirectory` باید روی volume محلی پایدار با backup باشد.
ابزار backup/verify/restore دو پایگاه با مقصد تازه و کنترل integrity در
[راهنمای بازیابی](docs/RECOVERY.md) آمده است.
انتقال JSON قدیمی فقط با `Fabric__ImportLegacyOnStartup=true` و هویت tenant
موجود انجام می‌شود. زرین‌پال برای شارژ و تسویه با verify سروری اضافه شده است؛
تنظیمات و محدودیت‌ها در [راهنمای زرین‌پال](docs/ZARINPAL.md) آمده است.
dispatcher بیرونی outbox، آزمون SSO واقعی و استقرار چندمیزبان هنوز تکمیل
نشده‌اند. SQL adapter آزمایشی فعال نیست.

## مرز معماری

صفحه `/Governance` برای درخواست ورود/جابه‌جایی/خروج، بررسی مستقل، تعریف
الگوهای نقش و زمان و شبیه‌سازی تصمیم بر مبنای مفاهیم XACML اضافه شده است.
محدودیت زمانی دسترسی سازمان به‌صورت opt-in و پیش‌فرض خاموش پیاده شده است؛
اجرای خودکار درخواست‌های چرخهٔ هویت هنوز فعال نیست. دامنهٔ سیاست‌ها در
[راهنمای راهبری و XACML](docs/GOVERNANCE-XACML.md) و مرز اتصال زمانی در
[راهنمای تقویم و دسترسی زمانی](docs/ORGANIZATION-TEMPORAL-ACCESS.fa.md) آمده است.

صفحه `/Audit` اکنون سوابق واقعی هر سازمان را با فیلتر نوع رویداد و صفحه‌بندی
نمایش می‌دهد. هر دسته به مجوز مشاهده همان بخش وابسته است؛ جزئیات در
[راهنمای رویدادها](AUDIT.md) آمده است.

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
