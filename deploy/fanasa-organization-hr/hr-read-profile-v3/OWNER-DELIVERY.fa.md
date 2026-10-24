# تحویل پیاده‌سازی خواندن HR به مالک Gateway

این تحویل محلی طراحی و کد جدید است؛ `operationalBindingReady=false` و `operationalAcceptance=false` می‌مانند. source برنامهٔ فعال همان `597dab599177f98f7d260d00c72adb736614ad42` و CI آن pipeline10 است. پروفایل جدید در آن source منتشر نشده است. هیچ receipt، pin، initial journal، marker، مسیر زنده یا پذیرش قبلی بازنویسی نشده است.

## قرارداد اجراشده در کد نامزد

| بخش | مقدار |
|---|---|
| projection/event/context/decision schema | `3.0.0` |
| profile | `fanasa.organization-hr-read/1` |
| template / combining | `organization-hr-read/1` / `deny-overrides` |
| action Chart | `organization.hr.chart.read` |
| action History | `organization.hr.history.read` |
| required scope هر دو | `organization.hr.read` |
| operation group | `organization.hr.read` |
| eligible role / native permission | `HrReader` / `organization.read` |
| product key / audience | `organization-fabric` / `fanasa-organization-fabric` |
| freshness / witness / execution سقف | ۵ثانیه؛ انقضای شواهد می‌تواند کوتاه‌تر باشد |
| پاسخ | کامل، مستقل، حداکثر1MiB، `no-store` |
| بدنه | صفرbyte واقعی؛ probe روی stream |
| تاریخچه | revision مثبت canonical Int64؛ امضای `History(Guid,long)` در HR597 بررسی شده است |
| selector | consumer + profile + product + contract؛ ambiguity بسته |

مالکیت ثبت مرکزی فعلی: محصول UUID `b735c486-e711-49cb-bd62-0ce900ad557e`، `owner=platform` و `tenantId=null`. HR3 آن را با `ownership={kind:"Platform", owner:"platform", registeredProductId:"..."}` و `ownerTenantId=null` مدل می‌کند. نوع Tenant نیز UUID canonical و تطبیق owner با ownerTenantId می‌خواهد. registry جاری باید این reference را دوباره تأیید کند؛ snapshot و JSON امضاشده به‌تنهایی اثبات اختیار نیستند.

شناسهٔ محصول و audience فعلی از PVC مرجع مشاهده شدند. tenant/client/subject/grant توسعهٔ قبلی یا قیمت/سهمیهٔ fixture، قرارداد یا allocation HR3 نمی‌سازند. قالب receiver تعمداً consumer، contract، allocation، key/pin و zone را خالی نگه می‌دارد و اعتبارسنجی را پاس نمی‌کند.

## سازگاری wire و شکاف اتصال

قاب HMAC هفت‌جزئی قبلی عیناً حفظ و با known-answer مستقل آزموده شده است: domain `fanasa.context.v1`، method، RawTarget، hash Authorization، hash body، hash Idempotency-Key و exact Base64 envelope؛ هر جزء با طول UTF8 و colon. JSON canonicalization انجام نمی‌شود. Idempotency-Key خواندن اختیاری و با همان bytes است؛ nonce از `tier:requestId` مستقل می‌ماند.

پروژهٔ اختیاری `http`، verifier مستقل پیام نسخه‌دار `fanasa.hr-read.context/1` و adapter احراز/endpoint خواندن را در همین نامزد اضافه می‌کند. نسخهٔ جدید فقط قرارداد محلی این نامزد است؛ با sender جاری Gateway یکپارچه یا به‌عنوان قرارداد مشترک پذیرفته نشده است. receiver فرمان فعلی همچنان فقطPOST و قراردادv2 را می‌پذیرد و تغییر نمی‌کند. `ownership` جدید HR3 را نمی‌توان با UUID ساختگی در `ownerTenantId` wire قدیمی جای داد. endpoint جدید در میزبان زنده ثبت نشده و PDP HTTP میزبان هنوز v3-aware نشده است.

`IHrReadAuthority` و `IHrReadCurrentGuard` پیش‌فرض unavailable هستند. اتصال میزبان باید named `OrganizationHrBearer`، direct native TLS validity/EKU/pin همان peer/key، exact headers/RawTarget، JWT identity و نگاشت authenticated introspection consumer یکتا را کنترل کند. هیچ flag ورودی مشتری، forwarded certificate یا ساختن Admission در کد، authority نمی‌سازد.

Fabric adapter واقعی فقط همان instance/schema موجود را مصرف می‌کند: nonce در `contract-policy.gateway-context.replay.v1` با کلید `tier:requestId`، commit مستقل پیش از ReadSnapshot، خواندن همگام و detached، serialization محدود، سپس snapshot تازهٔ guard. source DB API پیش از کنترل اندازه string می‌سازد؛ سقف حافظهٔ کلی ادعا نمی‌شود. policy/assignment/projection persistence میزبان هنوز باید profile-aware شود و producer/provisioner/observer مرجع تحویل شود. قیمت، quota، rate یا تخصیص تجاری از این بسته تولید نمی‌شود.

## شواهد و حدود

- suite جدید: ۳۱۴ assertion موفق شامل SQLite واقعی موقت، nonce پس از reopen/key change، claim هم‌زمان، فساد داده، لغو، fresh-gate revocation و404.
- suite مستقل HTTP/envelope: ۱۸۵ assertion موفق، شامل parser و امضا، نسخه/مالکیت/هویت، native-feature و گواهی‌های fixture، default-deny، ترتیب fresh-gate، انتشار خطای ارسال و سه درخواست واقعی به Kestrel موقت در loopback. مثبت‌های احراز و chain با providerهای fixture هستند؛ پذیرش trust بومیِ زنده ادعا نمی‌شود.
- regression اصلی v1/v2: ۳۴۴موفق، صفرشکست؛ هر۱۰schema قدیمی byte-identical. شش warning nullable فقط در fixture اصلیِ کپی‌شده با widening مدل مالکیت مستند شده‌اند.
- بازبینی مستقل نامزد: ایراد قابل اقدام P1/P2 پیدا نشد. این review و تست، پذیرش native TLS/introspection/PCC/Redis/Collector/APISIX واقعی نیستند.
- source pinها مبنای working tree بررسی‌شده را مشخص می‌کنند؛ snapshot یک فایل HR597 با checkout کامل source597 یکسان معرفی نشده است.
- اختلال repo-server Argo با تغییر تنها timeout liveness از۱به۵ رفع شد. HR و۵برنامهٔ دیگر Healthy/Synced شدند؛ source597، Podها، PVC و hashes پذیرش قبلی حفظ شدند. شاهد نهایی در `argocd-repair-final.json` است.

کد قابل حمل در `deploy/fanasa-organization-hr/hr-read-profile-v3` با manifestSHA256 تحویل می‌شود. تا اتصال و پذیرش واقعی تمام مرزهای بالا، `operationalBindingReady` نباید true شود و هیچ cutover انجام نشود.

منابع `IHrReadTrustedKeySource` و `IHrReadHttpAuthoritySource` پیش‌فرض unavailable هستند. قرارداد دقیق transport/envelope در `http/WIRE-CONTRACT.fa.md` ثبت شده است: epoch عددی با حداکثر۷رقم اعشار و عمر پیام/تصمیم حداکثر۵ثانیه، بدون تبدیل یا canonicalization متن امضاشده. sender باید نسخهٔ جدید و همین سقف را صریح رعایت کند. نبود نگاشت معتبر consumerTenantId با ساختن فیلد یا تنظیمات محلی جبران نمی‌شود؛ آن نگاشت باید توسط introspection معتبر و منبع مرجع تأیید شود.
