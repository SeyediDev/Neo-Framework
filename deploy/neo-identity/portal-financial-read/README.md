# آماده‌سازی خواندن مالی پنجره واحد — SSO

وضعیت: **پیشنهاد قابل بازبینی؛ اعمال نشده**، ۲۵ اکتبر ۲۰۲۶.
مالکیت این بسته فقط پیکربندی SSO است. هیچ اتصال SSH، تغییر Keycloak، ثبت
محصول/تننت، grant، تغییر trust یا دریافت توکن انسانی در این مرحله انجام نمی‌شود.
اطلاعات ورود و رمزهای قبلی این چت مبنای این بررسی نیستند و نباید دوباره استفاده شوند.

## ورودی و خروجی

مشاهدهٔ فقط‌خواندنی Root در
`C:/Users/JavadSeyediPC/.codex/worktrees/fanasa-billing-integration-20261024/Neo/.build/center-integration-20261025/FINANCIAL-BINDING-OBSERVATION.json`
مبنای وضعیت runtime است؛ این بسته مشاهدهٔ تازهٔ VPS نیست.
نام scope و کلید پیشنهادی محصول با پیش‌نویس
`FINANCIAL-ACTIVATION-DRAFT.json` در همان پوشه هماهنگ‌اند؛ این هماهنگی به معنی
تصویب یا ایجاد آن‌ها نیست. کلید پیشنهادی محصول `fanasa-unified-portal` و آدرس
مشاهده‌شدهٔ آن `https://hub.fanasa.net.local/` است. شناسهٔ واقعی محصول، مالک،
مرکز رسمی و سازمان مصرف‌کننده در این بسته عمداً خالی مانده‌اند.
کلاینت موجود `fanasa-unified-portal-web` فعال و public با Standard Flow و بدون
Service Account است. انتخاب سازمان مصرف‌کننده هنوز ورودی مصوب ندارد؛ از وجود
membership یا `billing.read` نمی‌توان سازمان انتخابی را حدس زد.

- `client-scope.json`: بدنهٔ پیشنهادی واقعی Keycloak ClientScopeRepresentation.
  scope پیشنهادی `consumer.financial.read` فقط یک Audience mapper دارد.
- `review-plan.json`: قرارداد claim، انتساب **Optional به فقط همین کلاینت**،
  تنظیمات غیرفعال Portal/PCC و دروازه‌های پذیرش. این فایل realm import نیست.
- `portal-request-scope.patch`: اصلاح سه‌خطی پیشنهادی برای مالک پنجره واحد؛ هنوز
  روی Program.cs اعمال نشده. فقط با پیکربندی معتبر و Enabled، scope درخواست می‌شود.
- `review.py`: بررسی آفلاین پیشنهاد و اثرانگشت کد؛ شبکه و قابلیت apply ندارد.
- `test_review.py`: آزمون جلوگیری از گسترش ناخواستهٔ همین پیشنهاد، نه آزمون IdP.

OAuth scope با grant یکسان نیست: API علاوه بر امضای معتبر، issuer و audience،
عضویت فعال، کلاینت فعال ثبت‌شده با AllowedScopes و grant سازمانی `billing.read`
را دوباره بررسی می‌کند. scope پیشنهادی شامل write، meter، registry، admin یا
offline_access نیست. این بسته grant سازمانی را داخل JWT کپی نمی‌کند.

## قرارداد claim و منبع آن

| claim | منبع / شرط |
| --- | --- |
| `iss` | issuer فعلی `https://sso.fanasa.net.local/realms/fanasa`، بدون تغییر |
| `aud` | شامل `fanasa-access-management-web` مطابق `PlatformBearer` موجود |
| `azp` | دقیقاً `fanasa-unified-portal-web`، از صادرکننده، نه mapper دست‌ساز |
| `scope` | شامل نام کامل `consumer.financial.read` به‌صورت عضو مستقل |
| `sub` | یک UUID غیرخالی lowercase-D، برابر کاربر نشست محافظت‌شده Portal |
| `sid` | یک UUID غیرخالی lowercase-D، برابر نشست ورود Portal؛ نه `session_state` |
| `auth_time` | عدد صحیح NumericDate از نشست انسانی، `auth_time <= now < exp` |
| `exp` | انقضای واقعی صادرکننده، عدد صحیح و آینده؛ طول عمر تغییر نمی‌کند |

`basic` در مشاهدهٔ Root منتسب است، ولی mapper مؤثر آن بررسی نشده است.
در [کد مرجع Keycloak 26.4.7](https://github.com/keycloak/keycloak/blob/26.4.7/services/src/main/java/org/keycloak/protocol/oidc/OIDCLoginProtocolFactory.java)
این scope شامل Subject و User Session Noteِ `AUTH_TIME -> auth_time` با نوع `long`
است. [TokenManager همان نسخه](https://github.com/keycloak/keycloak/blob/26.4.7/services/src/main/java/org/keycloak/protocol/oidc/TokenManager.java)
sid، authorized party و expiry را از نشست/کلاینت می‌سازد.
نسخهٔ فعلی نصب‌شده از مشاهدهٔ ورودی معلوم نیست؛ 26.4.7 صرفاً مرجع سورس است.
قبل از فعال‌سازی باید mapperهای مؤثر نسخهٔ نصب‌شده را کنترل کرد؛ mapper تکراری،
hardcoded، user-attribute برای identity/time یا alias به جای sid اضافه نشود.
وجود sid/auth_time به‌تنهایی اثبات عمومی انسانی بودن همهٔ توکن‌ها نیست؛
پذیرش به همین کلاینت محدود، service account غیرفعال، flowهای بازبینی‌شده و
تطبیق نشست تکیه دارد. Direct Access Grants، Implicit و Token Exchange هنوز بررسی نشده‌اند.

## مسیر رسمی و نواقص فعلی

1. مرکز راهبری `/ProductRegistration` فرم Preview/Receipt و نقش `PlatformAdmin`
   دارد. فعلاً فقط محصول را ثبت می‌کند، نه client metadata یا scope Keycloak.
   نام محصول، مالک و مرکز رسمی باید تصویب شوند؛ سازمان را خودکار انتخاب نکنید.
2. `/Products` اطلاعات معماری محصول/کلاینت را نمایش می‌دهد؛ فرم عملیاتی ثبت
   client metadata در صفحهٔ بررسی‌شده وجود ندارد.
3. مسیر `POST /api/access/catalog/products/{productId}/clients` و مدل
   `RegisterClientRequest(Key, DisplayName, ClientType, RedirectUris, AllowedScopes)`
   در سورس وجود دارند؛ ثبت **فرادادهٔ همین کلاینت موجود** به معنی ساخت کلاینت
   Keycloak نیست. مقدار Key همین clientId، نوع public و AllowedScopes شامل
   scope پیشنهادی باشد؛ RedirectUris فقط از تنظیم واقعی تأییدشده کپی شود.
   **این مسیر هنوز مسیر مدیریتیِ تأییدشده محسوب نمی‌شود:** متد فاقد annotation
   محلی PlatformAdmin و antiforgery است. Program.cs صرفاً authorization عمومی
   روی controllerها دارد. مالک مرکز راهبری باید اثر filterهای سراسری، محدودیت
   ادمین و CSRF را بررسی/تکمیل کند؛ از این نقص برای ثبت استفاده نکنید و SQL ننویسید.
4. ایجاد scope واقعی و Optional assignment از **کنسول مدیریتی خود Keycloak**
   است؛ فرم مرکز راهبری فعلی چنین عملیاتی ندارد. پس از تصویب جداگانه، Client
   scopes → تعریف scope با Audience mapper فایل JSON → Clients → همان کلاینت
   موجود → Client scopes → Add client scope → Optional. آن را Realm Default
   یا منتسب به کلاینت‌های دیگر نکنید. سازوکار Optional مطابق
   [راهنمای رسمی Keycloak](https://www.keycloak.org/docs/latest/server_admin/index.html#_client_scopes) است.
5. سورس Portal در هر دو checkout فقط `openid profile` را درخواست می‌کند؛
   بدون اصلاح پیشنهادی، scope جدید وارد درخواست نمی‌شود. نوشتن env به‌تنهایی
   این نقیصه را رفع نمی‌کند. تغییر این فایل متعلق به مالک Portal است.
6. `ConsumerBilling:Audience` باید با API برابر باشد؛ `fanasa-api` در تست‌های
   ساختگی نمونه است، نه audience فعلی API. `ConsumerUsageReport:PortalProductKey`
   هم باید پس از ثبت مرکزی با کلید مصوب پر شود؛ عمداً در پیشنهاد مقدار ندارد.

## چک‌های تکمیل پس از ورودی و مجوز مصوب

پیش از فعال‌سازی، انتخاب سازمان، ثبت پایدار محصول/client در مرکز، حفظ callbackهای
موجود، PKCE S256، mapperهای مؤثر و flowهای مجاز بررسی شوند. مشاهدهٔ
`fullScopeAllowed=true` در Root مجوز گسترش نقش نیست؛ scopeهای دیگر تغییر نکنند.
سپس مالک‌های Portal/PCC/Gateway مسیر HTTPS مجاز و تنظیمات خواندنی هماهنگ را
نصب و تست کنند. تغییر این مرحله شامل refresh/offline یا طول عمر SSO نیست.

توکن باید تنها در callback سروری اعتبارسنجی و در vault فرایندی موجود نگهداری شود؛
نه چاپ، log، فایل، task، browser storage، خواندن cookie یا خواندن توکن مرورگر.
خروجی شاهد فقط boolean، reason و وضعیت HTTP باشد. فلو انسانی واقعی و رد
scope/audience/client/tenant خارجی، عضویت غیرفعال و grant منقضی هنوز باید تأیید شوند.
vault فعلی سقف **پنج دقیقه** دارد و refresh خودکار ندارد؛ این محدودیت را با
تغییر عمر توکن SSO یا قبول token منقضی پنهان نکنید. بعد از انقضا نمایش مالی باید
fail closed بماند و تجربهٔ ورود دوباره را مالک Portal تصمیم بگیرد.

## اجرای بازبینی مستقل

```powershell
python -B -m unittest discover -s deploy/neo-identity/portal-financial-read -p test_review.py -v
python -B deploy/neo-identity/portal-financial-read/review.py
git apply --check deploy/neo-identity/portal-financial-read/portal-request-scope.patch
```

برای بررسی checkout دیگر از `--repo-root <absolute-checkout>` استفاده کنید.
اثر انگشت و یافته‌ها در stdout فقط مربوط به کد است؛ خروجی موفق این ابزار
**به معنی آماده‌بودن اتصال مالی یا تأیید توکن انسانی نیست**.
