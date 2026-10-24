# قرارداد HTTP تصمیم HR3

این قرارداد فقط transport جدید PDP را توصیف می‌کند؛ authority و اتصال زنده پیش‌فرض unavailable هستند. endpoint جدید و opt-in:

```text
POST /internal/contract-policy/hr-read/v3/evaluate
Content-Type: application/json
Authorization: Bearer <Gateway service access token>
X-Fanasa-Subject-Authorization: Bearer <HR subject access token>
```

`RawTarget` واقعی endpoint باید دقیقاً همین مسیر، بدون query و trailing slash باشد. `Content-Type` فقط یک مقدار `application/json`، بدون پارامتر charset دارد؛ مقایسهٔ خود media type به حروف حساس نیست. `Content-Encoding` و `Transfer-Encoding` پذیرفته نمی‌شوند. بدنهٔ واقعی مستقل از Content-Length تا ۳۲۷۶۸ بایت خوانده می‌شود؛ طول اعلام‌شدهٔ موجود باید با طول واقعی برابر باشد. هر دو Authorization دقیقاً یک مقدار با `Bearer ` canonical و یک token بدون فاصله/کاما دارند. raw token موضوع داخل JSON یا پاسخ کپی نمی‌شود.

transport فقط روی HTTPS مستقیم TLS 1.2/1.3 پذیرفته می‌شود. `ITlsHandshakeFeature` و `ITlsConnectionFeature` باید همان object بومی باشند. certificate-forwarding middleware روی این listener نباید گواهی peer را جایگزین کند. گواهی peer از اتصال بومی async گرفته می‌شود؛ زمان، EKU صریح clientAuth، SHA256 دقیق DER و زنجیرهٔ trust سیستم با revocation آنلاین بررسی می‌شوند. pin تنظیمات caller و binding HR باید یکسان باشد و issuer هر دو تنظیمات همان issuer canonical Fanasa باشد. فایل template، forwarded header یا certificate ارسالی در JSON مرجع TLS نیستند؛ این کتابخانه root CA نصب نمی‌کند.

احراز Gateway با scheme دقیق `HrReadPdpGatewayBearer` و احراز موضوع HR با `OrganizationHrPdpSubjectBearer` انجام می‌شود. نتیجه و AuthenticationTicket هرکدام باید همان scheme را داشته باشند. handler موضوع فقط header اختصاصی همین endpoint را می‌خواند و به Authorization fallback ندارد. issuer، audience، امضا، exp/nbf، azp/sub، scope و client_id/active اختیاری در کانال مربوط بررسی می‌شوند. یک identity احرازشده لازم است؛ scalarهای تکراری، audience یا scope تکراری و raw claim تکراری رد می‌شوند. handler استاندارد JWT با `MapInboundClaims=false`، metadata HTTPS، امضای الزامی و ClockSkew صفر ثبت می‌شود. authorization جاری سرویس در مرجع مرکزی مرحلهٔ جداگانهٔ source است.

audience می‌تواند یک مقدار یا آرایهٔ یکتای حداکثر ۱۶ مقدار باشد؛ audience دقیق تنظیم‌شدهٔ همان کانال باید در آن وجود داشته باشد. audienceهای اضافهٔ یکتا معتبرند و جای audience لازم را نمی‌گیرند؛ آرایهٔ تکراری، خالی یا با عنصر غیررشته‌ای رد می‌شود.

scope در JSON درخواست یک binding عملیاتی دقیق `organization.hr.read` است؛ token موضوع می‌تواند scopeهای اضافی OAuth داشته باشد. متن کامل و ترتیب scopeهای احرازشده در proof می‌ماند. context هسته زیرمجموعهٔ مرتب سازگار با schema سیاست را استفاده می‌کند؛ `offline_access` و `api:read` در token معتبر پذیرفته می‌شوند و فقط در آن context قرار نمی‌گیرند. نبود scope خواندن یا duplicate scope همچنان درخواست را رد می‌کند.

ریشهٔ JSON تمام فیلدهای زیر را دقیق و فقط یک‌بار می‌خواهد؛ unknown field و duplicate، حتی با escape متفاوت همان نام، رد می‌شود:

| گروه | فیلدها و قید |
|---|---|
| نسخه | `transportVersion=fanasa.hr-read.pdp.request/1`، `schemaVersion=3.0.0`، `profileId=fanasa.organization-hr-read/1` |
| درخواست منبع | `method=GET`، `rawTarget`، `requestId` به شکل `g-` و ۳۲ hex کوچک، `requestedAt` epoch canonical با حداکثر ۷ رقم اعشار |
| هویت موضوع | `subjectId`، `clientId`، `audience=fanasa-organization-fabric`، `scope=organization.hr.read`، `tokenSha256` hex کوچک ۶۴ کاراکتری SHA256 روی bytes UTF-8 **خود token بدون Bearer**، `tokenExpiresAt` عدد صحیح canonical برابر exp احرازشده |
| مالکیت/منبع | `consumerTenantId`، `ownerTenantId` nullable، `ownership` با دقیقاً `kind`، `owner`، `registeredProductId`، `productId=organization-fabric`، `environment` و `zone` |
| قرارداد | `contractId`، `contractVersion`، `policyId`، `policyVersion`، `allocationId`، `allocationScope`، `configRevision` |
| عملیات | `action` وابسته به مسیر و `operationGroup=organization.hr.read` |

همهٔ مقدارهای binding باید دقیقاً با `HrReadReceiverSettings` میزبان برابر باشند؛ نسخه‌های contract/policy/config مستقل و مثبت‌اند. `ownership.kind=Platform` نیازمند `owner=platform` و `ownerTenantId=null` است؛ نوع Tenant UUID مالک canonical و برابر ownerTenantId می‌خواهد. `registeredProductId` UUID canonical محصول مرکزی است و source باید آن را با ثبت جاری تطبیق دهد. JSON هیچ `authenticationValid`، `clientAuthorized`، grant، عضویت، quota یا Permit دریافتی از caller را قبول نمی‌کند.

دو `rawTarget` مجاز منبع:

```text
GET /api/organization-hr/v1/tenants/{consumerTenantId}/chart
GET /api/organization-hr/v1/tenants/{consumerTenantId}/history/{revision}
```

در اولی action دقیق `organization.hr.chart.read` و در دومی `organization.hr.history.read` است. revision عدد صحیح مثبت canonical تا Int64.MaxValue است؛ صفر، پیش‌صفر، query، trailing slash و مسیرهای commands پذیرفته نیستند. هیچ تبدیل یا نرمال‌سازی URL جای bytes مسیر واقعی را نمی‌گیرد. proof فقط به همان parsed request متصل است و نمی‌توان آن را برای target یا tuple دیگری استفاده کرد.

در زمان parse، `requestedAt` آینده پذیرفته نیست و اکنون باید **کمتر از** requestedAt+MaximumWitnessSeconds و tokenExpiresAt باشد؛ MaximumWitnessSeconds از ۵ بزرگ‌تر نمی‌شود. deadline نهایی کمینهٔ عمر درخواست، دو token، گواهی، evidence، تصمیم و دورهٔ allocation است. پاسخ ابتدا با سقف ۶۵۵۳۶ بایت کامل serialize می‌شود؛ بعد provider منبع را دوباره در snapshot تازه می‌خواند، تصمیم و binding تطبیق داده می‌شوند و proof/time پیش از اولین byte کنترل می‌شوند. بودجهٔ HTTP حداکثر ۵ ثانیه از پیش از body است و با cancellation فراخوان و RequestAborted ترکیب می‌شود.

این بودجه کد همگام SQLite یا `ReadCurrent` را به اجبار متوقف نمی‌کند. timeout پیش‌فرض SQLite موجود ۱۵ ثانیه است؛ درخواست cancellation و gateهای deadline پس از بازگشت کنترل اعمال می‌شوند و نتیجهٔ دیررس را رد می‌کنند. بنابراین «۵ ثانیه» تضمین hard preemption یا حداکثر latency دیواری برای adapter دلخواه میزبان نیست.

پاسخ JSON با `transportVersion=fanasa.hr-read.pdp.response/1`، نسخه/profile، requestId و binding کامل، hash/expiry موضوع، action/path، قرارداد/سیاست/allocation/configRevision، allocationPeriodId و مرزهای دوره، evaluatedAt/validUntil epoch، `admissionConfirmed=false` و `decision` هسته بازمی‌گردد. دادهٔ HR و raw bearer در آن نیست. تصمیم Permit هنوز obligationهای `admit-allocation` و `resource-tenant` را انجام‌نشده همراه دارد؛ پاسخ PDP receipt مصرف یا تایید admission نیست.

در منبع Fabric، scan bucket projection با بیش از ۱۰۰۰۰ سند یا وجود هر projection malformed unavailable است و به projection قبلی fallback نمی‌کند. سقف هر سند ۱ MiB پس از دریافت string از API موجود کنترل می‌شود؛ از این سقف نباید تضمین حافظهٔ کلی یا محدودبودن allocation اولیهٔ رشته استنباط کرد.

| نتیجه | رفتار |
|---|---|
| `200 application/json` | پاسخ تصمیم معتبر و تازه؛ decision می‌تواند Permit، Deny، NotApplicable یا Indeterminate باشد. کد HTTP به‌تنهایی مجوز اجرای HR نیست. |
| `400`، بدنهٔ خالی | JSON/نسخه/tuple/time assertion نامعتبر یا اختلاف Content-Length و بدنهٔ واقعی. |
| `403`، بدنهٔ خالی | transport/config/auth نامعتبر، service/source/authority ناموجود، شواهد غیرجاری یا پایان بودجهٔ داخلی پیش از هر تلاش برای ارسال پاسخ. |
| `413`، بدنهٔ خالی | طول اعلامی یا طول واقعی بدنه بیشتر از ۳۲ KiB. |
| لغو/خطای ارسال | CT فراخوان و RequestAborted و خطای write منتشر می‌شوند؛ پاسخ آغازشده جایگزین نمی‌شود. پس از تلاش برای write، timeout داخلی حتی اگر HasStarted هنوز false باشد منتشر می‌شود و denial جایگزین نمی‌سازد. |

تمام پاسخ‌های ساخته‌شدهٔ این boundary، `Cache-Control: no-store` دارند. `MapPost` در routing میزبان برای روش‌های ثبت‌نشده ممکن است 404/405 چارچوب بدهد؛ جدول بالا رفتار خود executor را بیان می‌کند. boundary روی پاسخ از قبل آغازشده اصلاً اجرا نمی‌شود. ارسال شکست‌خورده به denial یا موفقیت ساختگی تبدیل نمی‌شود.
