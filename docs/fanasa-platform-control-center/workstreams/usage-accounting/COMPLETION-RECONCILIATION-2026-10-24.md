# تطبیق تکمیل PAYG، Accounting و Billing — ۲۴ اکتبر ۲۰۲۶

مالک تخصصی هر سه حوزه چت موجود «Fanasa Usage & Accounting» است؛ هماهنگی ادغام و انتشار با «Fanasa Platform Control Center» می‌ماند. پیاده‌سازی دامنهٔ تحویل‌شده کامل و پذیرفته‌شدهٔ محلی است. کار باقی‌مانده، تطبیق جانشین انتشار و پذیرش عملیاتی است؛ آماده‌بودن کد به معنی نصب یا پرداخت فعال نیست. در این تطبیق نقص مستقل تازه‌ای در منطق مالی اثبات نشد و کد پذیرفته‌شده دوباره پیاده‌سازی نشد.

## مبنا و شواهد خوانده‌شده

- تحویل مالک: `fabc3f51808ce60d32772b674072db9a58dc401d`، بستهٔ ۶۹مسیره و [پذیرش محلی](ACCEPTANCE-2026-10-24.md). این سند تنها تغییر تازهٔ همین ورک‌تری است؛ rebase یا تغییر source مشترک انجام نشد.
- مانیفست مرکزی واقعاً خوانده شد: `E:/SJVS/Projects/Neo/.build/financial-integration-manifest-20261024/README.fa.md`، همراه `financial-integration-manifest.json` و `SOURCE-CITATIONS.json`؛ مبنای آن `21d5aea3c7bd9c8efdcf2092db15908918458c07` است. SHA256 فایل README: `58905816e58e8a8c76bcd3e772a33bf24472e1d25b2778ec1c07e08503f6d288`.
- مرجع Gateway نسخهٔ دوم است: `E:/SJVS/Projects/NeoCluster/.build/financial-gateway-plan-20261024/ARTIFACT-MANIFEST.v2.json` با SHA256 `521a0c5efc4a8dc45187f1acedd1f6baba368570f7a72805fbf698b7dc1a92f0`. الحاقیه، `OWNER-RECONCILIATION.v2.json` و `PROBE-MATRIX.json` نیز خوانده شدند؛ رسید v2 دارای `ownerManifestReconciled=true`، `proposalOnly=true` و `liveAcceptance=false` است. ۲۴ گروه probe تعریف و **صفر گروه اجرا** شده است.
- اصلاح `Guid.Empty` در commit مرکزی `2fcb18341eb6fff5f1223da3ae88b72992cd104c` از روی diff بررسی شد: هر دو GET مصرف و حساب، برای درخواست احرازشده و پیکربندی‌شده شناسهٔ صفر را پیش از projection با 400 رد می‌کنند؛ تقدم 401 middleware و 503 تنظیمات خاموش حفظ می‌شود. این نقص رفع‌شده است، نه کار باز مالک مالی.
- رسید `deploy/fanasa-organization-hr/HR3-R8-LOCAL-INTEGRATION.fa.md` از commit `b47c125cf0b5f0019687ab7d8451e53719001324` خوانده شد: جانشین مهرشدهٔ r8 و ثبت محلی CI آماده‌اند؛ APIهای مصرف و مالی با DLL واقعی opt-in به‌ترتیب ۱۱۱ و ۶۵ بررسی موفق داشته‌اند. این نتیجه پذیرش زندهٔ مالی یا CI معتبر انتشار نیست. طبق رسید، r8 push یا نصب نشده است؛ Gateway v2 هنوز به مبنای 21d5 اشاره دارد و باید جانشین را با رسید تازه تطبیق دهد، نه اینکه سند تاریخی v2 بازنویسی شود.

## دامنهٔ تکمیل‌شده

| حوزه | رفتار موجود و شاهد پذیرش محلی |
| --- | --- |
| قرارداد، پلن و نرخ | snapshot معتبر قرارداد/پلن و نرخ نسخه‌دار در مصرف حفظ می‌شود؛ حساب و billingMode باید سازگار باشند. replay قیمت امروز را انتخاب نمی‌کند. شاهد: [راهنمای مصرف](../../../../products/Fanasa.AccessManagement/docs/USAGE-ACCOUNTING.md)، PaygMetering:۴۳ و suite سازمان در رسید ادغام مرکزی. provisioning واقعی از این شاهد نتیجه نمی‌شود. |
| مرز مصرف و ثبت یکباره | در پایلوت `unit.save`، commit معتبر تغییر و quantity=1، receipt، debit و outbox در یک تراکنش Fabric‌اند؛ رد، rollback و retry همسان charge تازه نمی‌سازند؛ actor/payload متعارض رد می‌شود. HTTP200 یا فرم مرورگر شاهد billable نیست. [شاهد مصنوعی](../../../../products/Fanasa.AccessManagement/docs/fixtures/payg-billing-2026-10-24/unit-save-e2e.json): یک charge با نرخ تاریخی v7 برابر ۱۲ IRR، ماندهٔ ۸۸، بدهی صفر. |
| ledger، اعتبار و رزرو | محاسبهٔ دقیق، کف موجودی PAYG و سقف اعتبار postpaid، اصلاح محدود و منع مصرف دوبارهٔ ماندهٔ رزروشدهٔ پرداخت موجود پیاده‌اند. رزرو مستقل برای unit.save کوتاه و اتمیک لازم نیست؛ رزرو عمومی provider دیرپاسخ پیاده نشده است. شاهد: BillingLifecycle:۸۱ و Organization financial regression:۳۸۶. |
| صورتحساب و credit-note | ماه UTC پایان‌یافته، خطوط و قیمت تاریخی، hash ثابت، یک سند برای هر دوره و credit-note جدا و محدود موجودند. مصرف دیررس خودکار سند بسته را تغییر نمی‌دهد؛ PAYG statement مطالبهٔ پرداخت دوم نیست. شاهد: [چرخهٔ مالی](../../../../products/Fanasa.AccessManagement/docs/BILLING-LIFECYCLE.md)، BillingLifecycle و Billing API پذیرفته‌شده. |
| outbox و بازیابی | outbox پایدار، lease/fence/ack/retry، reconciliation و retry فقط حسابداریِ شاهد pending موجودند؛ backend دوباره اجرا نمی‌شود. crash/rollback و backup/restore در suiteهای قبلی پوشش دارند. restore منسجم همچنان receipt مقصد و رعایت قرارداد replay می‌خواهد؛ restore واقعی محیط مقصد پذیرفته نشده است. |
| API و Portal | دو خواندن محدود مصرف/حساب، خلاصهٔ invoices، مبلغ رشته‌ای دقیق، مرز انسانی و grant زنده، جداسازی سازمان و پاک‌کردن پاسخ قبلی موجودند. رسید ادغام مرکزی: Portal برابر ۱۲۵/۶۷/۲۴؛ ۶۷ مورد linked-source است. timeout جمعی reader برابر ۳۰ثانیه و انتظار افزودهٔ مالی تا ثانیهٔ ۴۸ درخواست محدود است؛ مرورگر ۵۰ثانیه دارد. این مهلت، سقف همهٔ providerهای کاتالوگ نیست. |

اعداد مستقل‌اند، جمع نمی‌شوند و در این نوبت دوباره اجرا نشده‌اند. شواهد مرورگر قبلی Accounting هشت بررسی موفق و Portal هفت گروه UI موفق دارد؛ shutdown Chrome محلی timeout داشت و موفقیت کامل runner ادعا نمی‌شود. CI/مهر r8 این محدودیت تاریخی را به موفقیت تبدیل نمی‌کند.

## قرارداد اتصال خواندنی

| روش و مسیر موجود | هویت و مجوز |
| --- | --- |
| `GET /api/accounting/v1/consumers/{consumer}/usage-report?from=...&to=...` | `PlatformBearer` انسانی؛ دقیقاً یک from و to به UTC، بازهٔ نیمه‌باز کوتاه‌تر از ۳۶۶روز |
| `GET /api/accounting/v1/consumers/{consumer}/account` | همان مرز انسانی؛ بدون query؛ صورتحساب‌ها در `account.invoices` هستند و GET مستقل consumer/invoices وجود ندارد |
| `GET /api/accounting/v1/tenants/{tenant}/account` | Cookie انسانی و `billing.read` زنده؛ خارج از دو route پیشنهادی Gateway |
| `POST /api/accounting/v1/tenants/{tenant}/invoices` و `credit-notes` | Cookie، CSRF و `billing.write` زنده در writer؛ خارج از اتصال read-only Gateway |

audience backend در کد `fanasa-access-management-web` است؛ issuer از `Authentication:Authority` می‌آید. `ConsumerBilling:Scope` و `ConsumerUsageReport:Scope` باید scope مصوب یکسان باشند و در token.scope و AllowedScopes client موجود باشند. **`billing.read` permission زندهٔ tenant است، نه مقدار فرضی OAuth scope.** `Authentication:ClientId` پورتال، azp توکن و `ConsumerUsageReport:PortalClientId` باید با client فعال محصول ثبت‌شده تطبیق کنند.

subject از sub معتبر توکن امضاشده، همراه sid/auth_time/exp معتبر می‌آید؛ از ایمیل، query یا header کاربر ساخته نمی‌شود. consumer همان سازمان انتخاب‌شده در فهرست عضویت مجاز Workspace است؛ backend مستقلاً فعالیت consumer، عضویت و grant منقضی‌نشده را بازبینی می‌کند. tenant مالک محصول با consumer یکی فرض نمی‌شود. credential کاتالوگ/client_credentials و `X-Subject`/`X-User-Id` جایگزین نیستند.

`ConsumerBilling.Enabled=false` و GatewayOrigin/Audience/Scope خالی باقی‌اند. مقصد مجاز فقط ریشهٔ `https://api.fanasa.net.local:443/` است؛ redirect، credential fallback و انتقال توکن به مرورگر وجود ندارد. توکن انسانی کوتاه‌عمر سمت سرور می‌ماند؛ restart/expiry/logout نیازمند نشست معتبر تازه است.

ماتریس خواندنی موجود: bearer نامعتبر پیش از controller →401؛ bearer معتبر ولی client/scope/member/grant/timing نامعتبر →403؛ expiry داخل تحمل clock-skew middleware می‌تواند در کنترل سخت‌گیرانهٔ دامنه 403 شود. تنظیمات خاموش/ناقص برای درخواست احرازشده →503؛ query/header ممنوع →400؛ خطای منبع catchشده →503 بدون دادهٔ مالی جزئی. Portal، 401 را signInRequired، 403 را forbidden و timeout/خطای منبع را unavailable می‌کند؛ این وضعیت داخل Snapshot لزوماً status HTTP خود صفحه نیست. لغو فراخواننده منتشر و پاسخ دیررس کنار گذاشته می‌شود؛ دادهٔ سازمان قبلی یا صفر ساختگی نشان داده نمی‌شود. no-store controller برای challenge middleware به‌تنهایی تضمین نیست.

## باقی‌ماندهٔ واقعی: اقدام، مسئول و شاهد پایان

| اقدام باقی‌مانده | مسئول هماهنگ‌شونده | شاهد لازم برای پذیرش |
| --- | --- | --- |
| انتخاب و تطبیق جانشین b47/r8 با manifest مالی و Gateway v2؛ تولید source/image/form و artifact متناظر | مرکز + مالک Agentic CI/CD و انتشار | receipt دقیق commit/tree/source inventory، CI بومی معتبر و audit فعال، provenance artifact نصب؛ صرف ثبت محلی CI کافی نیست |
| تعیین product/client/scope موجود، قرارداد token انسانی، consumer و عضویت/grant مجاز | مالکان SSO، ثبت مرکزی و دسترسی؛ مالک مالی بازبین قرارداد | تنظیم مصوب بدون راز و پذیرش staging از sub/sid/auth_time/azp/aud/scope؛ رد scope صحیح بدون grant و grant صحیح بدون scope؛ tenant از ایمیل حدس زده نشود |
| تعیین upstream/شبکه/CA/SNI و binding/admission خواندنی؛ render و نصب فقط دو GET محدود | مالک Gateway + مالک شبکه/محیط | config اجرایی بازبینی‌شده، TLS معتبر، منع redirect/route نوشتنی، forwarding bearer اصلی و نبود admission دوم؛ proposal JSON فعلی config نصب‌شده نیست |
| تعیین تکلیف GET body و deadline backend/hop پیش از پذیرش | مالک Gateway و backend با بازبینی مالک مالی | سیاست body صریح و probe آن؛ timeout اندازه‌گیری‌شدهٔ hopها زیر بودجهٔ جمعی reader، لغو/پاسخ دیررس و حفظ کاتالوگ. controller همگام deadline مستقل HTTP ندارد؛ SQLite DefaultTimeout=15 معادل آن نیست. این محدودیت هنوز رفع‌شده معرفی نمی‌شود |
| پذیرش دو GET بدون charge/payment در staging ایزوله | مالک محیط/پذیرش با مالکان مالی، SSO و Gateway؛ مجری نام‌گذاری شود | اجرای ثبت‌شدهٔ هر ۲۴ گروه PROBE-MATRIX، شامل 401/403، consumer دیگر، revoke حین خواندن، envelope، TLS، no-store و privacy؛ ledger/outbox/مانده پیش و پس یکسان بماند |
| پایلوت metering و بازیابی قابل نگهداری، جدا از فاز خواندنی | مالک مالی + مالک سرویس و قرارداد/دسترسی + عملیات | runbook با consumer/owner، نسخهٔ پلن/نرخ/قرارداد و trusted admission مصوب، baseline همان Fabric، backup/restore آزموده، reconciliation، retry همان operation و rollback. اجرای تغییردهندهٔ واقعی نیازمند مجوز و پذیرش مستقل است؛ این سند آن را صادر نمی‌کند |

نام فرد/چت مجری شبکه، نصب و پذیرش staging در artifacts مشخص نشده است؛ مسئولیت نقش‌ها بالا روشن است و مرکز باید مصداق را تعیین کند. اطلاعات واقعاً لازم از انسان/محیط، همین bindingها و مالک/consumer/client/scope مصوب و هویت artifact نهایی است؛ نبود آن‌ها مانع سند یا تکمیل کد محلی نیست، مانع پذیرش live است. مقدار نرخ، شارژ، grant یا credential تازه از این سند فرض نمی‌شود.

معیار پایان محلی این نوبت: فقط همین سند مستقل با commit قابل بررسی؛ بدون build/test/network یا تغییر runtime، shared tree، Gateway/CI/VPS. معیار پایان عملیاتی: رسیدهای همهٔ ردیف‌های بالا برای artifact دقیق نصب‌شده و rollback قابل اجرا، با تأیید مالکان مربوط. ERP، مالیات/صورتحساب قانونی، refund/تسویهٔ بانکی، payout و رزرو عمومی providerهای طولانی خارج از دامنهٔ تحویل حاضرند.
