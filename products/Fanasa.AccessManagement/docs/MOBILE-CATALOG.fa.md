# کاتالوگ شخصی اپ اندروید — نسخه اول

این قرارداد در سورس محلی پیاده شده است؛ فعال‌سازی زنده، ثبت Keycloak، grant مصرف، انتشار backend و پذیرش ورود موبایل را اثبات نمی‌کند. API جدید از همان رجیستری، عضویت‌ها، عرضه‌ها و grantهای مرکزی استفاده می‌کند و هیچ حساب، رجیستری یا مجوز جدیدی ایجاد نمی‌کند.

**وضعیت محیط در ۲۰۲۶/۱۰/۲۷:** محصول `fanasa-unified-portal` با شناسهٔ `ad2d62fd-7c68-4d66-a66f-1b0c30e32013` برای سازمان فن‌آسا (`fanasa-internal`) و مرکز رایان (`fanasa.rayan`) از فرم رسمی مرکزی ثبت و مستقل بازخوانی شد. کلید دقیق `Platform:MobileCatalog:ClientProductKey` برای همین محیط اکنون مشخص است؛ runtime زنده هنوز MobileCatalog و استثنای callback Android را ندارد و client موبایل یا API فعال نشده‌اند. DNS/TLS عمومی آماده است، ولی issuer خصوصی و تنظیمات قبلی SSO حفظ شده‌اند؛ ثبت محصول پذیرش ورود موبایل محسوب نمی‌شود.

## قرارداد HTTP

| درخواست | پاسخ موفق |
| --- | --- |
| `GET /api/mobile/catalog/tenants` | آرایه `{ id, name, namespace }` برای عضویت‌های فعال خود کاربر |
| `GET /api/mobile/catalog/tenants/{tenantId}/apps` | آرایه `ConsumerApplicationView` برای مصرف‌کننده منتخب و مجاز |

شناسه tenant باید UUID کانونی lowercase با قالب `D` باشد. هویت تنها از `sub` توکن معتبر خوانده می‌شود؛ همه query parameterها و headerهای `X-Subject`, `X-Subject-Id`, `X-User-Id`, `X-Tenant`, `X-Tenant-Id`, `X-Consumer-Tenant-Id` رد می‌شوند. APIهای delegation موجود به public client اجازه نمی‌دهند subject دیگری را انتخاب کند.

DTO اپ همان قرارداد پالایش‌شده consumer است: `id`, `key`, `displayName`, `audience`, `center`, `url`, `repository:null`, `tenantId`, `owner:""`, `attributes:null`. `tenantId` محصول مالک/تولیدکننده است و نباید با tenant مصرف‌کننده مقایسه شود. `url` تنها از رجیستری مرکزی می‌آید؛ API هیچ URL دلخواه، token در URL یا نشانی callback از درخواست قبول نمی‌کند. لینک HTTPS کانونی می‌تواند با Android App Links به اپ مقصد برسد یا با مرورگر پیش‌فرض باز شود؛ این API association دامنه یا نصب اپ مقصد را تأیید نمی‌کند.

همه پاسخ‌های کنترلر `Cache-Control: no-store` دارند. `401` یعنی bearer معتبر نیست؛ `403` یعنی کلاینت ثبت‌شده، scope، subject یا عضویت tenant اجازه خواندن نمی‌دهد؛ `400` ورودی مسیر/query/header نامعتبر است؛ `503` پیکربندی آماده نیست یا داده مرجع قابل اعتماد خوانده نشد. `200 []` یک نتیجه خالی معتبر است و نباید جای خطا نمایش داده شود. قبل از اجرای کنترلر، پاسخ `401` توسط middleware صادر می‌شود.

## تنظیمات مرکزی

پیش‌فرض غیرفعال است. نمونه زیر فقط پس از تکمیل ثبت مرکزی و پیکربندی IdP برای محیط منتخب قابل فعال‌سازی است:

```text
Platform__MobileCatalog__Enabled=true
Platform__MobileCatalog__ClientId=fanasa-mobile-android
Platform__MobileCatalog__ClientProductKey=<exact-existing-unified-portal-product-key>
```

مقدار آخر باید **کلید دقیق محصول موجود پنجره واحد** باشد. سند تشخیص `portal-reader-503-diagnosis-2026-10-23.md` در workstream مرکز راهبری نشان می‌دهد این پیوند در بررسی خواندنی قبلی تکمیل نشده بود؛ این سند از نام client یا میزبان، کلید محصول را حدس نمی‌زند. محصول جدید موازی نسازید. اگر محصول موجود یا مالک فعال آن قابل شناسایی نیست، فعال‌سازی انجام نشود.

در همان محصول موجود، دقیقاً یک `ProductClient` با metadata زیر لازم است؛ ثبت از API موجود `POST /api/access/catalog/products/{productId}/clients` با نشست ادمین پلتفرم و CSRF معتبر انجام می‌شود و باید پس از restart بازخوانی شود. صفحه Products فعلی فرم اختصاصی ثبت client ندارد:

```json
{
  "key": "fanasa-mobile-android",
  "displayName": "پنجره واحد موبایل — Android",
  "clientType": "public",
  "redirectUris": ["net.fanasa.portal.mobile:/oauth2redirect"],
  "allowedScopes": ["openid", "profile", "platform.catalog.self"]
}
```

ثبت باید فعال باشد و محصول دقیقاً یک مالک tenant فعال داشته باشد. backend این وضعیت را در هر درخواست و هنگام بازخوانی snapshotها بررسی می‌کند. نبود یا لغو ثبت، scope، callback یا عضویت باعث رد دسترسی می‌شود. secret در اپ یا این API وجود ندارد. نمونه ثبت مرکزی، اجرای mutation یا اعطای دسترسی به اپ‌های تجاری نیست.

اعتبارسنجی عمومی `RegisterClient` همچنان HTTPS می‌خواهد. تنها استثنای native برای کلید دقیق `fanasa-mobile-android`، نوع `public`، scope شخصی فوق و **تنها یک** redirect برابر callback دقیق اندروید است. client دیگر، نوع service، scope غایب، scheme دیگر یا مجموعه چند callback نمی‌تواند از این استثنا استفاده کند. این استثنا نشانی ورود محصول یا URL کاتالوگ را تغییر نمی‌دهد.

## نمونه قابل بازبینی Keycloak

کلاینت اندروید public، Authorization Code و `S256` است. پیکربندی نمونه در [mobile-android-keycloak.example.json](mobile-android-keycloak.example.json) و تعریف scope در [mobile-android-scope.example.json](mobile-android-scope.example.json) قرار دارد. این‌ها export واقعی محیط یا اسکریپت import نیستند. ابتدا در محیط منتخب با نسخه نصب‌شده بررسی شوند؛ تغییر realm، client موجود یا mapperها بدون برنامه ثبت و بازخوانی انجام نشود.

توکن دسترسی API باید `azp=fanasa-mobile-android`، دقیقاً یک `aud=fanasa-access-management-web` و scope شامل `platform.catalog.self` داشته باشد. `sub` دقیقاً یک UUID کانونی است؛ امضا، issuer، lifetime، audience و metadata HTTPS با `PlatformBearer` موجود بررسی می‌شوند. backend زمان `exp` و `nbf` و ثبت جاری public client را نیز در guardهای خواندن دوباره کنترل می‌کند. این scope شخصی جای `platform.catalog` سرویس Portal یا `platform.registry` توسعه‌دهنده نیست.

callback ورود دقیق `net.fanasa.portal.mobile:/oauth2redirect` و callback خروج دقیق `net.fanasa.portal.mobile:/logout` است. refresh token و access token فقط در ذخیره‌سازی رمزگذاری‌شده native دستگاه نگهداری شوند. ورود وب‌اپ‌های مقصد با نشست مرورگر سیستم انجام شود؛ token یا Cookie موبایل به اپ دیگر یا query URL کپی نشود. نشست هر اپ مقصد و لغو/خروج آن مستقل است؛ خروج این کلاینت به‌تنهایی تضمین خروج همزمان تمام اپ‌ها نیست.

## آزمون و محدودیت پذیرش

[MobileApplicationCatalog.Tests](../tests/MobileApplicationCatalog.Tests/README.md) مسیر واقعی HTTP/JWT را با داده مصنوعی بررسی می‌کند و عدم تغییر تمام جدول‌ها و schema هر دو SQLite را پس از هر درخواست اثبات می‌کند. آزمون قبلی ConsumerApplicationCatalog باید همچنان موفق بماند تا مسیر delegation سرویس و کنترل‌های consumer حفظ شوند.

این تغییر schema و migration ندارد. دو بانک Fabric و رجیستری snapshot مشترک تراکنشی ندارند؛ guardها لغو عضویت و ثبت را بازبینی می‌کنند، ولی تضمین سراسری توزیع‌شده ایجاد نمی‌کنند. شبکه، DNS و TLS موبایل، Keycloak واقعی، محصول/grant واقعی و Android App Links همچنان نیازمند پذیرش مستقل محیط‌اند. دامنه خصوصی `fanasa.net.local` به‌خودی‌خود از اینترنت موبایل در دسترس نیست.
