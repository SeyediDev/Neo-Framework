# نامزد محلی PDP خواندن منابع انسانی

این بسته مرز HTTP احرازشده و ارزیابی HR3 را به‌صورت opt-in پیاده می‌کند. وضعیت آن **نامزد محلی و غیرفعال در محیط زنده** است. ساختن این بسته، ثبت یک route، معتبر بودن فایل تنظیمات یا موفقیت fixtureها به معنی اتصال به مرجع مرکزی، ایجاد قرارداد یا پذیرش APISIX نیست.

بستهٔ مجاور `hr-read-profile-v3` منبع ثابت evaluator، provider، schemaها، Fabric و اعتبارسنج گواهی است و تغییر نمی‌کند. شناسهٔ مستقل manifest آن:

```text
9875088ffe6e3efc9553b0ca42873fa52b8a05d23d8101acd084c69e16b82329
```

هدف endpoint جدید این است که Gateway برای درخواست واقعی chart یا history، تصمیم سیاست را از دادهٔ معتبر و جاری بگیرد. این endpoint خود chart یا history را برنمی‌گرداند و خواندن دادهٔ سازمانی را اجرا نمی‌کند. برای مثال، درخواست chart فقط می‌تواند action برابر `organization.hr.chart.read` داشته باشد؛ تغییر مسیر به history، action برابر `organization.hr.history.read` می‌خواهد. داشتن scope مشترک `organization.hr.read` به‌تنهایی مجوز هیچ‌یک از این دو عملیات نیست: قرارداد، entitlement، نقش، grant، allocation، عضویت و محدودیت‌های محیط نیز باید با همان درخواست تطبیق کنند.

درخواست به `POST /internal/contract-policy/hr-read/v3/evaluate` می‌رود. دو bearer مستقل لازم است: هویت سرویس Gateway در `Authorization` و token موضوع HR در `X-Fanasa-Subject-Authorization`. schemeهای اختصاصی به‌ترتیب `HrReadPdpGatewayBearer` و `OrganizationHrPdpSubjectBearer` هستند؛ cookie، `HttpContext.User` یا token کانال دیگر جای آن‌ها را نمی‌گیرد. اتصال مستقیم TLS 1.2/1.3، همان feature بومی handshake و certificate، pin گواهی، عمر معتبر، EKU صریح clientAuth و زنجیرهٔ سیستم با revocation آنلاین نیز لازم‌اند. جزئیات بستهٔ wire در [WIRE-CONTRACT.fa.md](WIRE-CONTRACT.fa.md) آمده است.

`AddHrReadPdpAuthentication` فقط با فراخوانی صریح میزبان، دو handler واقعی JwtBearer را ثبت می‌کند؛ issuer/audience/signature/lifetime بررسی می‌شوند، skew صفر است، raw claimهای تکراری و تاریخ عددی غیر canonical رد می‌شوند و default scheme تغییر نمی‌کند. `MapHrReadPdp` نیز فقط با فراخوانی صریح یک route جدید ثبت می‌کند. هیچ‌یک Program، route موجود یا تنظیمات زنده را خودکار تغییر نمی‌دهد. template سرویس در [caller-settings.template.json](caller-settings.template.json) عمداً disabled و فاقد هویت است؛ binding موضوع HR همان [receiver.template.json](../hr-read-profile-v3/receiver.template.json) در ریشهٔ بستهٔ مجاور است. پر کردن این فایل‌ها منبع authority ایجاد نمی‌کند.

مسیر ارزیابی پس از احراز چنین است:

1. هویت و عمر دو token، hash دقیق token موضوع، مسیر GET موردنظر و tuple کامل درخواست به proof جداگانه متصل می‌شوند.
2. `TrustedAccessSnapshotProvider` و evaluator ثابت HR3 از همان بستهٔ مجاور استفاده می‌شوند. selector چهار بعد consumer/profile/product/contract دارد؛ انتخاب مبهم رد می‌شود.
3. `ExistingFabricHrReadPdpSourceFactory` فقط همان instance موجود `FabricDatabase` میزبان را می‌گیرد؛ مسیر DB تازه‌ای قبول نمی‌کند. منبع واقعی `HrReadPdpFabricSource` از bucketهای موجود projection، allocation، policy، policy-version و role-assignment در snapshot صرفاً خواندنی می‌خواند. کلیدهای HR3 وابسته به selector کامل‌اند؛ alias قدیمی consumer-only جایگزین آن‌ها نیست. scan bucket projection با بیش از ۱۰۰۰۰ سند یا حتی یک projection malformed، کل خواندن HR را unavailable می‌کند. API موجود ابتدا string سند را می‌سازد و بعد سقف ۱ MiB سند کنترل می‌شود؛ این کد سقف حافظهٔ کلی scan یا allocation اولیهٔ string را تضمین نمی‌کند.
4. پاسخ ابتدا با سقف ۶۴ KiB serialize می‌شود. سپس منبع در snapshot تازه دوباره خوانده و تصمیم، binding و deadline مقایسه می‌شوند. proof احراز، گواهی همان اتصال و زمان درست پیش از ارسال دوباره کنترل می‌شوند؛ پس از gate نهایی عملیات async احراز یا پایگاه داده انجام نمی‌شود.

**اتصال مرکزی هنوز موجود نیست:** `IHrReadPdpSourceFactory` پیش‌فرض unavailable است. حتی factory واقعی Fabric بدون `IHrReadPdpHostRecords` معتبر، از `UnavailableHrReadPdpHostRecords` استفاده می‌کند و اجازهٔ پاسخ تصمیم نمی‌دهد. میزبان باید adapter مرجع برای delegation جاری Gateway، introspection معتبر و یکتای consumer، client، ثبت محصول و مالکیت جاری، tenant، membership، role، direct grant، مجوز بومی و legacy veto را تحویل دهد. دادهٔ request یا فایل binding جای ثبت مرکزی را نمی‌گیرد. crypto/introspection async باید پیش از snapshot تمام شود؛ داخل `ReadCurrent` هیچ await، sync-over-async، transaction دیگر یا write مجاز نیست. هر فراخوانی باید انقضا، revocation و نسخه‌های مرجع را دوباره کنترل کند.

proof متن کامل scope احرازشده را نگه می‌دارد. query هسته فقط زیرمجموعهٔ مرتب و سازگار با schema سیاست را از همان scopes می‌سازد؛ `organization.hr.read` همچنان لازم است. scopeهای OAuth مانند `offline_access` و `api:read` در احراز پذیرفته و در proof حفظ می‌شوند و فقط از context canonical سیاست حذف می‌شوند؛ وجود آن‌ها به خودی خود token معتبر را رد نمی‌کند و حذفشان مجوز جدیدی نمی‌سازد.

publisher مجاز برای provision کردن policy/assignment/projection با کلیدهای HR3، تنظیمات واقعی caller، اتصال listener و CA، identityهای سرویس و subject و اتصال APISIX نیز در این بسته ساخته یا فعال نشده‌اند. هیچ quota، قیمت، قرارداد، نقش، grant یا allocation تجاری از مقادیر template تولید نمی‌شود. **Permit این PDP فقط تصمیم به‌همراه obligationهای انجام‌نشده است**؛ `admissionConfirmed=false` باقی می‌ماند و این کد هیچ nonce، debit، ledger یا رسید fulfillment تولید نمی‌کند. Gateway/PEP همچنان باید obligationهای واقعی را در مسیر خود انجام دهند.

سقف بدنهٔ واقعی ۳۲ KiB و سقف پاسخ PDP برابر ۶۴ KiB است. freshness پروفایل و بودجهٔ کامل HTTP از پیش از خواندن بدنه حداکثر ۵ ثانیه‌اند؛ token، گواهی، evidence و پایان دوره می‌توانند آن را کوتاه‌تر کنند. بودجه با `RequestAborted` و CT فراخوان ترکیب می‌شود و انتظار body/auth/source/send را نیز محدود می‌کند. عملیات خارجی که API توقف ندارد ممکن است در پشت‌صحنه تمام شود؛ نتیجهٔ دیررس احراز/منبع از gate عبور نمی‌کند. خطای ارسال و لغو درخواست به پاسخ موفق یا denial جایگزین نمی‌شوند. پس از تلاش برای write، حتی اگر HasStarted هنوز false باشد، timeout منتشر می‌شود و پاسخ بازنویسی نمی‌شود؛ این قرارداد توقف اجباری stream خارجیِ بی‌اعتنا به cancellation را ادعا نمی‌کند.

بودجهٔ ۵ ثانیه‌ای، deadline و درخواست cancellation است؛ کد همگام `ReadCurrent` یا فراخوان SQLite را به اجبار preempt نمی‌کند. SQLite موجود timeout پیش‌فرض ۱۵ ثانیه دارد و یک عملیات همگامِ مسدود ممکن است تا بازگشت کنترل از بودجه عبور کند؛ پس از بازگشت، نتیجهٔ منقضی اجازهٔ ارسال نمی‌گیرد. این بسته تضمین تأخیر دیواری حداکثر ۵ ثانیه برای کد دلخواه میزبان نمی‌دهد.

آزمون‌ها محلی‌اند: parser، named authentication، JWTهای واقعاً امضاشده با RSA و metadata ثابت fixture، بودجه و cancellation، certificate fixture، منبع SQLite/Fabric موجود با دادهٔ مصنوعی و gateهای تازگی. این آزمون‌ها پذیرش TLS زنده، زنجیرهٔ CA عملیاتی، introspection، PCC، provisioning، Redis یا APISIX را اثبات نمی‌کنند. نتیجهٔ نهایی اجرای همین نامزد باید در evidence انتشار جداگانه ثبت شود؛ این متن تعداد آزمون یا موفقیت اجرا را از پیش ادعا نمی‌کند.

برای اجرای suite با بستهٔ ثابت موجود، مقدار `HrReadProfileRoot` را صریح به مسیر آن بدهید:

```powershell
dotnet run --project tests/Fanasa.HrReadPdp.Tests.csproj -p:HrReadProfileRoot="<مسیر مطلق بستهٔ ثابت hr-read-profile-v3>"
```

در چیدمان دو بستهٔ مجاور، مقدار پیش‌فرض `../hr-read-profile-v3` همین وابستگی را انتخاب می‌کند. build این نامزد هیچ مجوزی برای ویرایش بستهٔ ثابت یا فعال‌سازی میزبان زنده ایجاد نمی‌کند.
