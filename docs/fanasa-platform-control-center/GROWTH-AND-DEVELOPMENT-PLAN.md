# برنامه رشد و توسعه مرکز راهبری پلتفرم فن‌آسا

تاریخ: ۲۰۲۶-۱۰-۱۰ — نسخه مرجع طراحی

این برنامه همراه با [معماری مرجع](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/ARCHITECTURE.md)، مسیر توسعه Fanasa Platform Control Center را از یک MVP قابل آزمون تا بستر همکاری شرکت‌ها، Generalistها و اقتصاد مصرف مشخص می‌کند. مبنای آن، دستور فعلی کاربر، بحث‌های پیشین و طراحی‌ها و پیاده‌سازی‌های قابل استناد است؛ این سند خروجی طراحی و توسعه است. موضوعات استخراج‌شده از مرور مقدماتی جلسات نیز ورودی طراحی‌اند؛ تأیید صوتی، انتساب به گوینده و تصویب پیشنهادهای تیم ارائه‌دهنده از این سند نتیجه نمی‌شود.

**این بک‌لاگ اهداف پذیرش پیشنهادی را مشخص می‌کند.** بخش‌هایی پیاده‌سازی موجود دارند و بازاستفاده آن‌ها به تطبیق نسخه و پذیرش عملیاتی وابسته است؛ هیچ آیتمی صرفاً با نگارش این سند تکمیل‌شده محسوب نمی‌شود. شاهدهای موجود در بخش مبنا جدا ثبت شده‌اند. اعداد عملکرد، ظرفیت، زمان و اقتصاد در این سند هدف پیشنهادی آزمون یا داده نمونه‌اند؛ KPI اندازه‌گیری‌شده، SLA قراردادی، تعرفه یا درصد تسهیم مصوب نیستند. زمان‌بندی بر حسب عبور از دروازه پذیرش است و تعهد تقویمی ایجاد نمی‌کند.

## ۱. مقصد و مبنای شروع

شرکت‌ها مالک محصول، تیم و تصمیم‌های دامنه خود می‌مانند. فن‌آسا هویت مرکزی، رجیستری، قواعد دسترسی، مسیر عرضه API، منابع مشترک، تجربه توسعه و دفتر مصرف را فراهم می‌کند. رشد پلتفرم باید با افزایش محصولات قابل استفاده و مصرف قابل تطبیق سنجیده شود؛ تعداد صفحه‌ها، routeها یا عامل‌ها به‌تنهایی معیار پذیرش نیست.

| موضوع | مبنای مستند در ۱۰ اکتبر | کار لازم برای پذیرش |
| --- | --- | --- |
| پنجره واحد | سند اتصال، استقرار توسعه نسخه 499ac1cd9ae4f619fb9e61ba03b6f267b5867e78، دریافت موفق scope برابر platform.catalog و audience برابر fanasa-access-management-web، ورود SSO و انتخاب سازمان را گزارش می‌کند | محصول واقعی مجاز، کاربر بدون grant، دسترسی مستقیم مقصد و SaaS با مالک متفاوت هنوز پذیرش کامل نشده‌اند |
| کاتالوگ واقعی | برای حساب آزموده‌شده، سازمان مجاز پاسخ موفق با صفر سامانه گرفته است | ثبت محصول، عرضه، entitlement، grant مصرف و اعتبار زمانی بررسی شوند؛ علت از خالی بودن پاسخ معلوم نیست |
| تطبیق نسخه | Program.cs پوشه فعلی همچنان PlatformService توسعه‌دهنده با platform.registry دارد؛ PlatformApi این پوشه با قرارداد application-tenants نسخه مستقر یکسان نیست | manifest انتشار و artifact مستقر با منبع و قرارداد API تطبیق داده شوند؛ ورک‌تری قدیمی روی منبع فعلی جایگزین نشود |
| قرارداد و دسترسی موجود | در منبع توسعه‌یافته ورک‌تری 8f13، ApplicationTenancy کنترل قرارداد/پلن/تننت/محصول/زمان فعال و سقف seat را دارد؛ ContractIntegrationApi با platform.contracts نیز موجود است. این فایل‌ها در منبع فعلی E: نیستند | این قابلیت‌ها پس از تطبیق artifact بازاستفاده/ادغام کنترل‌شده شوند؛ Rule، role binding، TPS و burst نسخه‌دار گسترش هدف‌اند و از رویداد quantity/unit/date/status موجود اثبات نمی‌شوند |
| دوام داده | اسناد و ترکیب فعلی، Fabric و رجیستری SQLite پایدار را فعال معرفی می‌کنند؛ SQL adapter قبلی فعال نیست | backup بیرونی، تمرین restore، ظرفیت و توپولوژی چندمیزبان با شاهد عملیاتی بررسی شوند |
| سیاست و چرخه هویت | نگارش/انتشار مستقل سیاست، شبیه‌سازی و درخواست‌های lifecycle پایدار مستند شده‌اند؛ پروفایل موجود محدود است | اجرای زنده PDP روی درخواست‌ها و worker زمان‌بندی‌شده در مبنا فعال نیستند؛ وجود UI به معنی enforcement نیست |
| مصرف و پرداخت | دفتر مصرف idempotent و تراکنش محلی با outbox مستند است؛ آزمون‌های fixture و درخواست sandbox پرداخت گزارش شده‌اند | dispatcher بیرونی، تطبیق انتهابه‌انتها، پرداخت واقعی پذیرنده و صورتحساب قانونی تأیید عملیاتی ندارند |
| Gateway | در طراحی NeoCluster، Apache APISIX مقصد data plane است؛ نصب یا مهاجرت کامل از این شاهد اثبات نمی‌شود | inventory، انتخاب حالت استقرار، پایلوت مستقل، سیاست، limiter اتمیک و بستن bypass پس از پذیرش لازم‌اند |
| عرضه عمومی | میزبان‌های فعلی داخلی با پسوند fanasa.net.local هستند | DNS عمومی، TLS معتبر، قرارداد عملیات و الزامات عرضه بیرونی در فاز مربوط فراهم شوند |

تشخیص قدیمی خطای اتصال Portal، با شاهد استقرار ۱۰ اکتبر جایگزین شده است. برنامه این خطا را به‌عنوان مانع جاری باز نمی‌کند و برای پُر کردن کاتالوگ، محصول یا دسترسی ساختگی ایجاد نمی‌کند.

## ۲. قراردادهای ثابت توسعه

این قیود در همه فازها برقرارند؛ تغییر آن‌ها باید به‌عنوان تغییر معماری با اثر و مسیر مهاجرت ثبت شود.

1. **یک مرجع هویت:** Keycloak مرکزی مالک هویت، رمز، MFA، session و sub است. محصولات مستقیماً با client اختصاصی و OIDC به آن متصل می‌شوند. عضویت و مجوز استفاده از سامانه از مرکز راهبری می‌آید.
2. **یک رجیستری:** مرکز راهبری مرجع محصول، client، مالک، مرکز قابلیت، عرضه و entitlement است. Portal فقط کاتالوگ مجاز تننت فعال را مصرف می‌کند؛ cache مشتق‌شده باید محدودیت، زمان تازگی و رفتار خطا داشته باشد.
3. **دو تننت مستقل:** ownerTenantId مالک محصول است؛ consumerTenantId مرز مصرف، مجوز و هزینه است. Managed/External و اختصاصی/SaaS دو محور مستقل‌اند. مالکیت محصول به‌تنهایی مجوز مصرف در تننت دیگر نمی‌دهد.
4. **دو مسیر ثبت:** محصول Managed از رخداد قابل اعتبارسنجی deployment با شناسه ثابت وارد رجیستری می‌شود. محصول External مستقیماً در مرکز ثبت و پذیرش امنیتی می‌شود. PublishAsApplication عرضه در کاتالوگ را کنترل می‌کند؛ هر جزء داخلی الزاماً سامانه قابل عرضه نیست.
5. **زنجیره قرارداد:** مدیریت قرارداد مالک مشتری، قرارداد، نرخ و lifecycle است. پارامترهای آن به entitlement و policy نسخه‌دار، eligibility نقش، role binding محدود به عضویت، تصمیم PDP، سهمیه مصرف و دفتر مالی نگاشت می‌شوند. خرید قرارداد، نقش مدیر را خودکار به خریدار نمی‌دهد.
6. **تفکیک تصمیم و مصرف:** PDP بدون اثر جانبی تصمیم دسترسی می‌دهد؛ PEP تعهدات تصمیم را اجرا می‌کند. پذیرش TPS، burst، concurrency و رزرو بودجه پیش از forwarding و به‌صورت اتمیک در data plane انجام می‌شود؛ Permit سهمیه مصرف را تضمین نمی‌کند.
7. **اجرای چندلایه:** Gateway و backend هر دو PEP دارند. Gateway هویت و مجوز مسیر/عملیات را اعمال می‌کند؛ backend محدوده داده و منطق دامنه را نیز بررسی می‌کند. هدر tenant/subject/role مشتری مرجع اعتماد نیست. مسیر مستقیم خارج از مرز مجاز باید بسته باشد.
8. **مصرف قابل تطبیق:** تحویل رویداد می‌تواند حداقل یک بار باشد؛ idempotency، deduplication، رزرو/نهایی‌سازی و reconciliation مانع هزینه تکراری می‌شوند. موفقیت workflow یا پاسخ HTTP معادل تسویه مالی نیست.
9. **مالکیت صریح:** هر محصول، API، process، provider و Skill/MCP یک مالک پاسخ‌گو، نسخه، دامنه دسترسی و معیار پذیرش دارد. مرکز راهبری به محل پیاده‌سازی همه منطق محصولات تبدیل نمی‌شود.

چارچوب مرجع سیاست، XACML از OASIS است. برنامه، XACML 3.0 به همراه Errata 01 و قرارداد JSON/REST نسخه 1.1 را به‌عنوان هدف معماری دنبال می‌کند. پروفایل typed موجود را نباید موتور کامل یا دارای انطباق استاندارد معرفی کرد. دامنه پشتیبانی، آزمون معنایی و ادعای انطباق باید در FPC-007 روشن شوند.

## ۳. مسیر مشترک از قرارداد تا مصرف

### مسیر چارت سازمانی و اتصال منابع انسانی

درخواست تکمیلی ۱۲ اکتبر، مدل [چارت، هویت و دسترسی](ORGANIZATION-IDENTITY-ACCESS.md) و [API منابع انسانی](pilot/HR-API-INTEGRATION.md) را در همین برنامه قرار می‌دهد. ساخت مرجع دوم هویت یا registry لازم نیست. ترتیب رشد این قابلیت چنین است:

| گام | مالک و وابستگی | معیار پذیرش |
| --- | --- | --- |
| چارت و HR API محدود | مالک OrganizationStore و Gateway؛ رجیستری و عضویت موجود | chart/history/commands نسخه‌دار، service client و grant جاری tenant، TLS واقعی، deny بدون تغییر داده، retry بدون تاریخچه تکراری و reopen پایدار؛ route عملیاتی با تنظیمات واقعی جدا پذیرفته شود |
| binding نقش به محصول | مالک Access Management؛ FPC-008 و مدل سمت/انتصاب | binding مصوب با provenance، tenant/product/action/resource scope و زمان؛ پایان یک انتصاب فقط دسترسی وابسته به همان منشأ را حذف کند؛ grant مستقل و سمت دوم حفظ شوند |
| lifecycle و delegation | مالک هویت/منابع انسانی؛ binding و worker موجود پس از پذیرش | join/transfer/offboard و جانشینی زمان‌دار، بازسنجی backend و invalidation محدود، replay/out-of-order و rollback بررسی شوند؛ سلسله‌مراتب مجوز ضمنی نسازد |
| پذیرش HR واقعی | مالک اتصال HR و SRE؛ سه گام بالا در دامنه مورد نیاز | یک سامانه HR با client/tenant و گواهی معتبر، نگاشت external ID، تطبیق revision، لغو اختیار و trace بدون secret؛ به‌روزرسانی چارت از Gateway در محیط مقصد شاهد داشته باشد |

پیاده‌سازی API به معنی تکمیل binding خودکار، worker lifecycle یا پذیرش محصول واقعی نیست. نسخه نخست از هفت فرمان چارت و ثبت اتمیک موجود استفاده می‌کند؛ انتقال مستقیم و bulk overwrite تا پذیرش reconciliation عرضه نمی‌شوند.

مسیر توسعه و پذیرش هر سرویس فروشی یا محدودشده باید این زنجیره را کامل کند:

**Contract parameters → entitlement/policy version → role eligibility و scoped role binding → PDP decision → PEP obligations → atomic admission/reservation → execution → idempotent usage → charge/reconciliation/billing**

| گام | ورودی و خروجی لازم | شرط پذیرش |
| --- | --- | --- |
| قرارداد | شناسه قرارداد، مشتری/consumer tenant، محصول، عملیات مجاز، زمان، Zone، Rule، نقش‌های قابل تخصیص، TPS/burst/concurrency، سهمیه و نرخ دارای نسخه | تولید نسخه entitlement با رویداد معتبر؛ داده ناقص یا تننت نامرتبط رد شود |
| تخصیص نقش | eligibility قرارداد همراه عضویت فعال و grant/تخصیص مجاز | نقش در product/consumer tenant/action محدود باشد؛ سمت سازمانی یا خرید به‌تنهایی مجوز نسازد |
| سیاست | subject/resource/action/environment از PIPهای معتبر با نسخه و زمان تازگی | contract، عضویت/grant، tenant و action در Permit شروط AND باشند؛ Deny معتبر با deny-overrides مقدم باشد |
| تصمیم و تعهدات | فقط Permit همراه تعهدات پشتیبانی‌شده قابل عبور است | Deny، NotApplicable، Indeterminate یا تعهد اجرا‌نشدنی مانع عبور شوند؛ PDP شمارنده یا صورتحساب تغییر ندهد |
| پذیرش مصرف | شناسه پایدار allocation، scope معتبر آن و نوع محدودیت؛ quota دوره‌ای با دوره مصوب | همه replicaها و routeهای همان allocation از وضعیت اتمیک مشترک استفاده کنند؛ محصول/محیط/group فقط با تخصیص مستقل قرارداد scope جدا بسازند؛ policy version سهمیه را با تمدید صفر نکند |
| اجرای عملیات | شناسه operation و request/trace، رزرو قابل بازیابی و PEP دامنه | شکست، timeout، cancellation و retry وضعیت روشن داشته باشند؛ اجرای دوباره بی‌اثر فقط در مرز idempotency تعریف‌شده باشد |
| مصرف و هزینه | usage event با کلید یکتا، consumer، owner، محصول، قرارداد، operation و snapshot نرخ/نسخه | رویداد تکراری یک بار اثر مالی بگذارد؛ اختلاف ledger با شاهد backend/provider قابل بررسی باشد |

برای درخواست‌های کوتاه، TPS و concurrency در لحظه ورود کنترل می‌شوند؛ concurrency lease پس از پایان آزاد می‌شود. برای GPU/job/مدل با هزینه نامعلوم، سقف رزرو و سیاست پایان/لغو پیش از پذیرش تعریف می‌شود. زمان انقضای lease به‌تنهایی اثبات توقف job نیست؛ تمدید، fencing، نتیجه backend و تطبیق مانده باید از کار تکراری یا پذیرش بیش از سقف جلوگیری کنند.

## ۴. فازها و نقاط عطف نسبی

هفت فاز P0 تا P6 تعریف شده‌اند. M0 تا M6 نقاط عطف پذیرش همین فازها هستند؛ شروع فاز بعد به‌معنی پایان خودکار فاز قبلی نیست. کارهای مستقل می‌توانند موازی پیش بروند، اما مسیر مصرف واقعی و عرضه باید از وابستگی‌های خود عبور کند.

| فاز | خروجی اصلی | نقطه عطف و شرط عبور |
| --- | --- | --- |
| P0 — تثبیت مبنا | release manifest، نقشه مرجع‌ها، inventory API و قراردادهای versioned | M0: منبع، artifact، محیط، دیتابیس و قرارداد API شناخته‌شده؛ اختلاف‌ها ثبت و مسیر تطبیق تعیین شده باشد |
| P1 — MVP | ثبت Managed/External، قرارداد و نقش، Gateway، PDP/PEP، سهمیه اتمیک، مصرف idempotent و Portal مجاز | M1: یک مسیر واقعی انتهابه‌انتها با آزمون مجاز/نامجاز، SaaS مالک متفاوت، retry، سهمیه چند replica و bypass مسدود پذیرفته شود |
| P2 — پایلوت عملیاتی | dispatcher، بازیابی، لغو دسترسی، تطبیق حساب و runbook | M2: قطع سرویس و replay مصرف پذیرفته‌شده را گم یا تکرار نکند؛ restore و تطبیق حساب در محیط پایلوت شاهد داشته باشند |
| P3 — اتصال و تجربه | Experience/Process/System API، BFF، سفر چندکانالی و process owner | M3: یک journey واقعی با ادامه بین دو کانال، trace و شکست/retry بدون هزینه تکراری پذیرفته شود |
| P4 — منابع و اقتصاد AI | provider registry، Token Broker، بودجه منابع، cloud/GPU/model و گزارش هزینه | M4: یک capability با دو provider قابل تعویض، رزرو هزینه و تطبیق مصرف/کیفیت آزموده شود |
| P5 — Generalist و اکوسیستم | workspace قابل کنترل، Skills/MCP نسخه‌دار، همکاری تیم و عرضه بیرونی | M5: یک تیم شرکت با مالک محصول روشن، از درخواست تا انتشار و مصرف تحت بودجه/دسترسی معتبر پیش برود |
| P6 — رشد ظرفیت | SLO، ظرفیت، isolation، اقتصاد و بهبود محصول بر اساس شواهد | M6: افزایش ظرفیت و تعداد تننت‌ها بر اساس اندازه‌گیری و بدون شکستن قیود امنیت/اقتصاد انجام شود |

### P0 — تثبیت مبنا پیش از توسعه

شروع با انتشار جدید یا تغییر دامنه دسترسی نیست. نسخه مرکز مستقر، منبع اصلی، ورک‌تری تاریخی و نسخه Portal باید در یک manifest قابل بررسی به هم وصل شوند. آخرین artifact واقعاً پذیرفته‌شده، مرجع مقایسه است؛ هیچ پوشه‌ای صرفاً به علت جدیدتر بودن فایل‌ها مرجع تلقی نمی‌شود.

این تطبیق برای هر ماژول جدا انجام می‌شود. کنترل entitlement/access و seat در منبع توسعه‌یافته موجود است و نباید از صفر بازسازی شود؛ پس از شناسایی نسخه صحیح، فقط بخش‌های قابل بازاستفاده با merge و migration کنترل‌شده به منبع مقصد آورده می‌شوند. اتصال جدید قرارداد به Rule/Role/TPS و enforcement Gateway، گسترش همین مرز است.

M0 علاوه بر صحت نسخه، موجودی محصولات/مصرف‌کنندگان و تفاوت قرارداد cookie/CSRF با Bearer را مشخص می‌کند. مدیریت قرارداد باید schema و مالک هر پارامتر اقتصادی/دسترسی را تعیین کند. APISIX مقصد Gateway و Redis مقصد شمارنده مشترک‌اند؛ نسخه، حالت استقرار، دوام و توپولوژی آن‌ها بر اساس ظرفیت VPS ثبت می‌شوند. مرجع اثر مالی store تراکنشی Accounting است. این برنامه نصب همه اجزای آینده روی یک VPS را فرض نمی‌کند.

### P1 — MVP با Gateway در هسته

برای شروع اجرایی با هزینه نگهداری محدود، ابتدا یک محصول Managed موجود، یک API، یک allocation و یک مصرف‌کننده متفاوت از مالک پذیرفته می‌شود. این مسیر، نخستین slice از P1 است؛ سپس پوشش کامل M1 شامل External و اتصال‌های دیگر تکمیل می‌شود. آزمون چند replica برای صحت شمارنده لازم است و الزام نصب سه replica روی VPS نیست. concurrency/reservation عمومی، workflow و runtimeهای مراحل بعد شرط این شروع نیستند؛ هر محدودیت الزامی قرارداد پایلوت باید واقعاً اعمال یا محصول از عرضه منع شود. مالکیت فایل‌ها و اجزای قابل تعویق در [هماهنگی اجرا](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/EXECUTION-COORDINATION.md) ثبت شده‌اند.

Gateway جزء MVP است. MVP بدون مسیریابی معتبر، PEP، سهمیه مشترک، مصرف idempotent و منع bypass پذیرفته نمی‌شود. مسیر پایلوت APISIX مستقل و قابل بازگشت راه می‌افتد؛ Admin API خصوصی و تغییرات route از desired state نسخه‌دار تولید می‌شوند.

ثبت داخلی از deployment قابل ردیابی می‌آید؛ نشر، ثبت API، عرضه application و عرضه ابزار/عامل گزینه‌های مستقل‌اند. ثبت External به pipeline داخلی وابسته نیست و مالک، URL، client، audience/scope و پذیرش backend خودش را دارد. هویت ماشین، webhook و client service از ابتدا دامنه محدود دارند؛ scope رجیستری توسعه‌دهنده به دسترسی کلی محصولات تعمیم نمی‌یابد.

سناریوی پیشنهادی M1 شامل یک محصول Managed و یک محصول External است؛ یکی به تننتی متفاوت از مالک عرضه می‌شود. حساب دارای grant آن را در Portal می‌بیند و استفاده می‌کند؛ حساب فاقد grant یا consumer tenant دستکاری‌شده هم در Portal و هم در Gateway/backend رد می‌شود. سهمیه نمونه روی سه replica، retry یک operation، تعلیق قرارداد و درخواست مستقیم backend آزموده می‌شوند. از M1، admission تازه پس از تعلیق در مهلت اندازه‌گیری‌شده حداکثر ۶۰ ثانیه رد می‌شود؛ آزمون partition و lifecycle کامل session/task در M2 گسترش می‌یابد. ثبت، grant و داده پایلوت باید از مسیر مجاز و مشخص باشد؛ fixture یا healthcheck شاهد پذیرش واقعی نیست.

### P2 — پایلوت عملیاتی و اقتصاد قابل تطبیق

dispatcher outbox، retry/dead-letter، رزروهای معلق، لغو دسترسی و restore باید آزمون اختلال داشته باشند. انتشار سیاست و route نسخه‌دار با وضعیت desired/observed، ack و freshness قابل ردیابی است. تصمیم cached پس از پایان زمان اعتبار یا فقدان شاهد معتبر اجازه تازه نمی‌دهد؛ مسیر قطع dependency با رفتار مشخص از قبل آزموده می‌شود.

دفتر مصرف به دفتر مالی قانونی تبدیل نمی‌شود. مسئول مالی قرارداد طبقه‌بندی مصرف billable/non-billable، نرخ، ارز، تسویه، credit/refund و حساب تولیدکننده را تعیین می‌کند. پرداخت واقعی و پذیرندگی شرط عرضه عمومی همان مسیر پرداخت است؛ نتیجه sandbox یا ثبت دستی credit جای آن را نمی‌گیرد.

### P3 — API-led و سفر چندکانالی

System API مرز قابلیت و داده سیستم را عرضه می‌کند؛ Process API مالک orchestration و state روند است؛ Experience API/BFF تجربه کانال را می‌سازد. Gateway منطق orchestration دامنه را حمل نمی‌کند. این لایه‌ها الزاماً سه سرویس فیزیکی تازه نیستند و ابتدا با مرز قراردادی روشن روی محصولات موجود شکل می‌گیرند.

journey، business operation و trace شناسه‌های مستقل و مرتبط دارند. کاربر باید بتواند یک فرآیند مجاز را از وب به کانال دیگر ادامه دهد، با بازسنجی عضویت و entitlement در هر مرحله. idempotency مرز عملیات مالی/تجاری را پوشش می‌دهد؛ correlation فقط ابزار تشخیص است و جای جلوگیری از اثر تکراری را نمی‌گیرد.

انتخاب BPMS/workflow/Saga بر اساس نیاز durability، مالک process و شرایط مجوز انجام می‌شود. اگر n8n یا ابزار مشابه انتخاب شود، مدل استفاده داخلی، میزبانی credential مشتری و embedded/SaaS جدا از هم بررسی می‌شوند؛ چند نمونه tenant مجوز عرضه را خودکار فراهم نمی‌کند.

### P4 — providerها، cloud/GPU و مصرف AI

Capability تجاری از provider اجرایی جدا می‌شود. providerها با کیفیت، منطقه، کلاس داده، سقف هزینه، availability و قرارداد معتبر ثبت می‌شوند. Token Broker دسترسی محدود به provider را طبق consumer tenant، محصول، قرارداد و budget انجام می‌دهد؛ secret خام و credential مدیر در اختیار عامل یا Portal قرار نمی‌گیرد.

مدل، GPU، Storage/CDN و منابع cloud ابتدا به‌صورت capability محدود با اندازه‌گیری مشخص پایلوت می‌شوند. واحد مصرف هرکدام مستقل تعریف می‌شود؛ تعداد token یا ثانیه GPU به‌تنهایی قیمت، کیفیت خروجی و حاشیه محصول را تعیین نمی‌کند. fallback تنها در محدوده provider، داده و سقف مجاز انجام می‌شود.

گزارش اقتصاد، هزینه مصرف‌کننده، هزینه واقعی provider و سهم/حساب تولیدکننده را جدا نشان می‌دهد. قاعده تسهیم، اختلاف نرخ، هزینه خطا، cancellation، ارز و تطبیق دیررس باید پیش از دریافت پول یا payout واقعی تعریف و آزموده شوند.

### P5 — Generalist، همکاری شرکت‌ها و عرضه اکوسیستم

Generalist در workspace متعلق به شرکت/پروژه، با دامنه دسترسی روشن و بودجه مدل/ابزار کار می‌کند. مالک محصول و متخصص دامنه نیاز را ثبت می‌کنند؛ عامل ابهام و تصمیم لازم را مشخص می‌کند؛ خروجی با شواهد build/test/review به توسعه و deployment می‌رود.

Skills و MCP بسته‌های قابل استفاده مجدد و source-pinned دارند. تبدیل API به ابزار، ثبت agent و عرضه application گزینه‌های مستقلی هستند و هیچ‌کدام خودکار اختیار deploy یا مدیریت دسترسی نمی‌دهند. LSP برای فهم کد workspace، ACP برای ارتباط ویرایشگر/پنل با عامل کدنویسی، MCP برای ابزار محدود و A2A برای همکاری عامل‌های مستقل به کار می‌روند؛ نسخه و قابلیت واقعی هر اتصال جدا آزموده می‌شود.

این فاز محصول موجود orchestration، مدیریت کار و deployment را به هم متصل می‌کند و سامانه موازی task/catalog/identity نمی‌سازد. پایلوت تجاری اکوسیستم به مالکیت دارایی، مسئولیت داده، قرارداد provider، نرخ و تسویه ثبت‌شده نیاز دارد. همکاری یا spin-off مطرح‌شده در ارائه دیگران، تعهد تجاری این برنامه نیست.

### P6 — رشد بر اساس شواهد

پس از پایلوت، تیم عملیات workload واقعی را با ظرفیت store، Gateway، PDP، provider و دفتر مصرف مقایسه می‌کند. جداسازی شبکه، data residency، shard، مهاجرت پایگاه یا استخراج service وقتی انجام می‌شود که SLO، ظرفیت یا مالکیت تیم دلیل مشخص داشته باشد. SQLite فعلی برای development و پایلوت تک‌میزبان حفظ می‌شود؛ مقصد adapter تراکنشی مرکز چندمیزبان، SQL Server مطابق معماری مرجع است. migration، outbox، concurrency، idempotency و restore پیش از cutover پذیرفته می‌شوند؛ رجیستری موجود حفظ و DB محصولات مستقل می‌مانند. SQLite روی filesystem مشترک مبنای مرکز چندمیزبان نیست. این گیت پیش از هر انتشار چندمیزبان مرکز، حتی اگر زودتر از P6 لازم شود، برقرار است.

Generalistها و تیم‌ها با الگوهای تثبیت‌شده محصول تازه می‌سازند. توسعه گسترده مرکزهای قابلیت، marketplace و مدل سهم درآمد پس از مشاهده پذیرش و اقتصاد چند محصول انجام می‌شود؛ شمارش connectorها یا اجرای demo معیار بازار نیست.

## ۵. بک‌لاگ با مالک، وابستگی و پذیرش

شناسه‌ها پایدارند. این جدول، بک‌لاگ مرجع است و جای برد مدیریت کار موجود را نمی‌گیرد. هنگام تبدیل به کار عملیاتی، همین شناسه، مالک واقعی، evidence و وضعیت در برد موجود پیوند داده شوند. «مالک» در این سند نقش پاسخ‌گو است؛ تعیین شخص/تیم در آغاز کار انجام می‌شود.

| شناسه | فاز و خروجی | وابستگی مستقیم | مالک پاسخ‌گو | معیار پذیرش قابل آزمون |
| --- | --- | --- | --- | --- |
| FPC-001 | P0؛ release manifest و تطبیق source/artifact/environment | ندارد | رهبر انتشار و مالک مرکز | hash/نسخه، قرارداد API، schema و مسیر داده هر artifact ثبت؛ اختلاف فعلی source و deployment طبقه‌بندی و مسیر merge/migration/rollback معلوم باشد |
| FPC-002 | P0؛ inventory محصول/API و مرجع‌های دامنه | FPC-001 | معمار پلتفرم | هر محصول owner، مرکز اصلی، URL، audience، نوع Managed/External و API owner داشته باشد؛ مسیرهای cookie-only و Bearer جدا شناسایی شوند |
| FPC-003 | P0؛ توسعه schema قرارداد، entitlement و نسخه رویداد | FPC-002 | مالک مدیریت قرارداد | schema موجود quantity/unit/date/status و کنترل access/seat نسخه صحیح شناسایی و حفظ شوند؛ Rule/Role/TPS/burst/concurrency/Zone/time/price با migration سازگار نگاشت شوند؛ رویداد ناقص و consumer نامرتبط رد شود |
| FPC-004 | P0؛ طراحی APISIX، store اتمیک و مرز شبکه | FPC-001, FPC-002 | رهبر SRE | ADR نسخه/حالت APISIX و Redis shared limiter، توپولوژی، تهدید bypass، failure mode، budget و rollback داشته باشد؛ اثر مالی در Accounting بماند؛ انتخاب مقصد از نصب آن تفکیک شود |
| FPC-005 | P1؛ ثبت Managed از deployment | FPC-002, FPC-003 | مالک deployment | رخداد امضاشده/معتبر با product key ثابت و نسخه environment ثبت شود؛ duplicate یا out-of-order محصول/route دوم نسازد؛ PublishAsApplication عرضه را کنترل کند |
| FPC-006 | P1؛ ثبت مستقیم External | FPC-002, FPC-003 | مالک رجیستری مرکز | ثبت از مرکز بدون build داخلی انجام شود؛ مالک/client/URL/مرکز/محیط و پاسخ‌گویی backend معتبر باشند؛ ورودی ناقص یا owner نامجاز رد شود |
| FPC-007 | P1؛ PAP/PIP و PDP هدف XACML | FPC-003 | رهبر امنیت و مالک policy | استاندارد/profile/نسخه pin شود؛ ماتریس supported/unsupported و golden tests چهار decision، deny-overrides، missing attribute و obligation وجود داشته باشد؛ انطباق اثبات‌نشده ادعا نشود |
| FPC-008 | P1؛ گسترش contract-to-entitlement و scoped role binding | FPC-003, FPC-007 | مالک دامنه دسترسی | gating موجود قرارداد/پلن/تننت/محصول/زمان و seat حفظ شود؛ create/amend/suspend/end نسخه ایجاد کنند؛ replay/reorder به وضعیت معتبر یکسان برسند؛ خرید eligibility بسازد و مدیر محصول فقط با عضویت معتبر و binding صریح مجاز شود؛ خرید بدون تخصیص/نقش فاقد حق قراردادی رد و platform.admin از خرید تولید نشود |
| FPC-009 | P1؛ پایلوت Gateway و route publication | FPC-004, FPC-005, FPC-006 | رهبر Gateway/SRE | مسیر product/version/environment به upstream معتبر متصل شود؛ Admin API خصوصی، route allowlist و desired/observed version و rollback آزموده شوند |
| FPC-010 | P1؛ هویت trusted و OAuth audience/scope | FPC-007, FPC-009 | مالک هویت مرکزی | issuer/signature/expiry/audience/scope/client و tenant context بررسی شوند؛ header جعلی حذف/رد شود؛ توکن غلط و cross-tenant در همه سناریوهای منفی رد شوند |
| FPC-011 | P1؛ Gateway PEP و backend PEP | FPC-008, FPC-010 | مالک امنیت API و مالک محصول پایلوت | فقط Permit با obligations اجراشده عبور کند؛ شروط contract/grant/tenant/action AND باشند؛ backend داده/عملیات تننت دیگر را مستقل رد کند؛ PDP بدون اثر جانبی باشد؛ admission تازه پس از تعلیق با freshness/TTL محدود در حداکثر ۶۰ ثانیه رد شود |
| FPC-012 | P1؛ TPS/burst/concurrency و admission اتمیک | FPC-004, FPC-011 | مالک data plane | دو route/group از allocation مشترک روی سه replica سقف کل و زیرسقف‌های لازم را رعایت کنند؛ scope از تعریف allocation بیاید و route/group آن را تکثیر نکند؛ رزرو پیش از forward اتمیک باشد؛ تعویض policy version سقف مصرف را reset نکند؛ store unavailable رفتار fail-closed داشته باشد |
| FPC-013 | P1؛ metering idempotent و رزرو/نهایی‌سازی | FPC-003, FPC-012 | مالک مصرف و اکانتینگ | duplicate/retry یک usage/charge بسازد؛ نرخ snapshot شود؛ timeout/cancel/partial outcome رزرو قابل تطبیق داشته باشد؛ مصرف پذیرفته‌شده در restart محفوظ باشد |
| FPC-014 | P1؛ machine clients و webhook | FPC-010, FPC-011, FPC-013 | مالک integration | client محدود و جدا از انسان باشد؛ webhook signature/issuer، expiry/replay key و tenant/product scope آزموده شوند؛ payload معتبر هم بدون entitlement عبور نکند |
| FPC-015 | P1؛ کاتالوگ مجاز Portal و پذیرش SaaS واقعی | FPC-005, FPC-006, FPC-008, FPC-010 | مالک Portal و مالک رجیستری | محصول owner متفاوت برای consumer مجاز نمایش/باز شود؛ کاربر بدون grant و tenant دستکاری‌شده رد شوند؛ Portal فقط reader و خطا مستقل از پاسخ خالی باشد |
| FPC-016 | P1؛ منع bypass و پذیرش M1 | FPC-011, FPC-012, FPC-013, FPC-014, FPC-015 | رهبر امنیت و مسئول QA | backend از اینترنت/شبکه نامجاز قابل دورزدن نباشد؛ service-to-service مجاز نیز از PEP و سهمیه مربوط عبور کند؛ رد admission تازه پس از تعلیق در تمام replicaها با مهلت حداکثر ۶۰ ثانیه و سایر سناریوهای واقعی M1 شاهد ثبت‌شده داشته باشند |
| FPC-017 | P2؛ outbox dispatcher و dead-letter/replay | FPC-013, FPC-016 | مالک integration/SRE | crash پیش/پس از ack و duplicate delivery مصرف را گم/دوبرابر نکند؛ poison event قابل مشاهده و replay کنترل‌شده با audit باشد |
| FPC-018 | P2؛ لغو دسترسی، invalidation و freshness | FPC-007, FPC-008, FPC-009, FPC-016 | مالک policy/SRE | suspend/end/offboard روی درخواست، session/task فعال و replicaها طبق target زمان‌دار اعمال شود؛ cache منقضی fail-closed و نسخه تصمیم قابل ردیابی باشد |
| FPC-019 | P2؛ backup/restore و reconciliation امنیتی/مالی | FPC-001, FPC-017, FPC-018 | مسئول SRE و مسئول مالی | restore به مقصد تازه تمرین شود؛ revokeهای بعد از backup و پرداخت/مصرف بیرونی پیش از بازگشایی تطبیق شوند؛ RPO/RTO با شاهد ثبت شود |
| FPC-020 | P2؛ مصرف، نرخ و حساب owner/consumer | FPC-003, FPC-013, FPC-017 | مالک اکانتینگ و مسئول مالی | billable boundary، ارز/نرخ، سهم تولیدکننده و اختلاف مصرف تعیین شوند؛ ledger مصنوعی دقیقاً تطبیق کند؛ قیمت/درصد نمونه به‌عنوان مصوب ثبت نشود |
| FPC-021 | P2؛ مسیر پرداخت و استثنای مالی | FPC-019, FPC-020 | مسئول مالی و مالک پرداخت | مسیر انتخاب‌شده پذیرندگی/دامنه/verify معتبر داشته باشد؛ replay اعتبار دوم نسازد؛ refund/credit note/payout اجرا‌نشده صریح باشند؛ sandbox به‌عنوان پرداخت واقعی پذیرفته نشود |
| FPC-022 | P2؛ runbook، telemetry و گیت پایلوت | FPC-016, FPC-017, FPC-018, FPC-019, FPC-020 | رهبر عملیات و مسئول QA | alert قطع dependency/مصرف اختلافی، trace بدون secret و rollback drill اجرا شود؛ مالک on-call و incident مشخص باشد؛ M2 با evidence پذیرفته شود |
| FPC-023 | P3؛ قرارداد Experience/Process/System و BFF | FPC-002, FPC-016 | معمار اتصال و مالکان محصولات | یک API هر لایه owner/version/semantics داشته باشد؛ BFF session/CSRF را درست نگه دارد؛ Gateway منطق process را اجرا نکند |
| FPC-024 | P3؛ journey، operation ID و trace چندکانالی | FPC-013, FPC-018, FPC-023 | مالک تجربه محصول و مسئول QA | فرآیند از دو کانال ادامه یابد؛ entitlement در ادامه بازسنجی شود؛ duplicate operation اثر/charge تکراری نسازد؛ trace سه لایه را متصل کند |
| FPC-025 | P3؛ durable process/Saga و انتخاب workflow | FPC-017, FPC-023, FPC-024 | مالک process و معمار اتصال | restart/retry/compensation/cancel در یک process واقعی آزموده شود؛ انتخاب ابزار و مجوز داخلی/embedded/SaaS روشن باشد؛ موفقیت ساختگی گزارش نشود |
| FPC-026 | P3؛ API lifecycle و SDK/OpenAPI | FPC-009, FPC-023, FPC-025 | مالک API Product | version/deprecation، test contract، discovery و نمونه client وجود داشته باشد؛ breaking change مصرف‌کننده ثبت‌شده را بدون مسیر مهاجرت قطع نکند |
| FPC-027 | P4؛ provider registry و مرز داده/قرارداد | FPC-002, FPC-020, FPC-022 | مالک منابع و مسئول امنیت داده | قابلیت از provider جدا باشد؛ هر provider داده مجاز/region/cost/quality/health و قرارداد معتبر داشته باشد؛ provider نامجاز انتخاب نشود |
| FPC-028 | P4؛ Token Broker و بودجه tenant/team/product | FPC-010, FPC-012, FPC-027 | مالک broker و امنیت | credential خام خارج نشود؛ delegation، حداقل scope، expiration و budget اتمیک آزموده شوند؛ توکن لغوشده/نامرتبط و خروج شبکه نامجاز رد شوند |
| FPC-029 | P4؛ واحد مصرف cloud/GPU/model و job reservation | FPC-013, FPC-017, FPC-028 | مالک AI/Cloud و اکانتینگ | واحد مصرف، رزرو سقف، lease/fencing، cancel و finalize مشخص باشد؛ restart/دیررس‌شدن نتیجه، job دوم یا هزینه نامعلوم رهاشده نسازد |
| FPC-030 | P4؛ تعویض provider و سنجش اقتصاد capability | FPC-020, FPC-024, FPC-027, FPC-029 | مالک capability و مسئول مالی | دو provider با contract test و benchmark کیفیت/هزینه مقایسه شوند؛ fallback فقط مجاز باشد؛ گزارش consumer cost/provider cost/owner account جدا و قابل تطبیق باشد |
| FPC-031 | P5؛ منشور مالکیت شرکت/Generalist و همکاری تیم | FPC-002, FPC-020 | مدیر محصول پلتفرم و نماینده شرکت مالک | مالک IP/داده/محصول، تصمیم دامنه، پاسخ‌گویی incident و قواعد اقتصادی ثبت شوند؛ پیشنهاد تیم دیگر بدون توافق، تعهد ثبت نشود |
| FPC-032 | P5؛ workspace محدود و چرخه Git تا deployment | FPC-005, FPC-018, FPC-022, FPC-028, FPC-031 | مالک تجربه توسعه‌دهنده | workspace به tenant/project/env محدود باشد؛ secret/egress/budget، cancel، evidence و review اعمال شوند؛ عامل از محدوده پروژه یا اختیار انتشار تجاوز نکند |
| FPC-033 | P5؛ کتابخانه Skills/MCP و نسخه پروتکل‌ها | FPC-026, FPC-028, FPC-032 | مالک orchestration و نگه‌دارنده دانش | بسته source-pinned با owner، contract، permission و compatibility test وجود داشته باشد؛ MCP محدود، ACP/LSP/A2A جدا آزموده شوند؛ API خودکار ابزار ادمین نشود |
| FPC-034 | P5؛ نیاز دامنه و تحویل Generalist | FPC-024, FPC-031, FPC-032, FPC-033 | مالک محصول شرکت و متخصص دامنه | یک درخواست واقعی با تصمیم‌های روشن، build/test/review و evidence به محصول پذیرفته‌شده برسد؛ ابهام دامنه یا ادعای تخصص با خروجی عامل تأییدشده تلقی نشود |
| FPC-035 | P5؛ عرضه عمومی و اقتصاد اکوسیستم | FPC-006, FPC-016, FPC-021, FPC-026, FPC-030, FPC-031, FPC-034 | مدیر اکوسیستم و مسئول مالی/SRE | DNS/TLS عمومی، terms، مسئولیت پشتیبانی، تعرفه/سهم/تسویه و onboarding معتبر باشند؛ نمونه تسویه با ورودی یکسان نتیجه یکسان بدهد؛ payout واقعی گیت مستقل داشته باشد |
| FPC-036 | P6؛ ظرفیت، isolation و استخراج مرز لازم | FPC-019, FPC-022, FPC-030, FPC-035 | رهبر معماری و SRE | workload اندازه‌گیری‌شده، bottleneck و SLO مبنای scale باشد؛ adapter SQL Server و migration/outbox/concurrency/idempotency/restore پیش از مرکز چندمیزبان پذیرفته شوند؛ noisy-neighbor/cross-tenant و limiter مشترک در topology جدید آزموده شوند |
| FPC-037 | P6؛ سنجش پذیرش و رشد محصول | FPC-020, FPC-034, FPC-035 | مدیر محصول پلتفرم | funnel ورود→فعال‌سازی→مصرف مفید→تمدید با privacy مناسب سنجیده شود؛ تصمیم رشد/pivot با شواهد مصرف و هزینه ثبت شود |
| FPC-038 | P6؛ بهبود تکرارشونده و تعهد خدمت | FPC-036, FPC-037 | کمیته محصول/عملیات با مالک مشخص | ظرفیت و اقتصاد چند دوره مشاهده شود؛ SLA تجاری فقط پس از سنجش و توافق منتشر شود؛ فاز بعد با اولویت و evidence روشن تعریف شود |

مسیر بحرانی MVP از FPC-001 شروع می‌شود و پس از قرارداد/سیاست، route/هویت، enforcement، limiter و metering به FPC-016 می‌رسد. FPC-005 و FPC-006 و آماده‌سازی Portal می‌توانند هم‌زمان پیش بروند. آماده‌سازی workflow/provider/workspace می‌تواند موازی باشد، اما عرضه و مصرف آن‌ها پیش از پذیرش وابستگی‌های امنیتی/مالی مجاز تلقی نمی‌شود.

## ۶. گیت‌ها و اهداف عددی پیشنهادی

اعداد زیر **پیشنهاد اولیه برای طراحی آزمون** هستند. پس از M0، تیم‌ها workload و توان زیرساخت را ثبت و target را نسخه‌دار می‌کنند. رسیدن به آن‌ها در این سند ادعا نشده است. هیچ نتیجه fixture جای سنجش محیط واقعی را نمی‌گیرد.

| شناسه معیار | هدف پیشنهادی و روش سنجش | گیت |
| --- | --- | --- |
| T-01؛ پوشش امنیتی | ۱۰۰٪ مسیرهای حفاظت‌شده پایلوت با Gateway/backend PEP؛ صفر عبور در ماتریس issuer/audience/scope/tenant/grant/expiry نامجاز | M1 |
| T-02؛ ثبت قابل تکرار | بازپخش ۱۰۰ رویداد تکراری و ۱۰۰ رویداد خارج از ترتیب روی Managed/External؛ صفر محصول/route/entitlement تکراری و وضعیت نهایی مطابق نسخه معتبر | M1 |
| T-03؛ limiter مشترک | دو route/group با یک allocation روی ۳ replica؛ تنظیم نمونه ۱۰ TPS، burst برابر ۲۰ و concurrency برابر ۵؛ ۱۰٬۰۰۰ درخواست رقابتی با زمان کنترل‌شده؛ صفر عبور خارج از سقف کل و زیرسقف‌های مصوب، بدون تکثیر سهمیه میان routeها | M1 |
| T-04؛ retry و هزینه | ۱۰٬۰۰۰ usage event یکتا همراه duplicate/retry و crash injection؛ دقیقاً یک اثر مالی برای هر رویداد billable معتبر و تطبیق کامل ledger با داده مصنوعی | M1/M2 |
| T-05؛ تأخیر افزوده | workload مستقل از T-03 با ۱۰۰ admission پذیرفته‌شده در ثانیه و ظرفیت مجموع allocation حداقل ۲۰۰ TPS؛ p95 مسیر Permit+Gateway+PDP+admission حداکثر ۵۰ ms و تصمیم PDP حداکثر ۲۰ ms؛ مسیر رد جدا گزارش شود؛ زمان backend، سخت‌افزار، concurrency و مجموعه policy روشن باشند | M2 |
| T-06؛ لغو و تازگی | فاصله commit لغو معتبر تا آخرین admission مجاز در همه replicaهای پایلوت حداکثر ۶۰ s؛ حد freshness و TTL تصمیم این هدف را پوشش دهند؛ حداقل تعلیق/admission در M1، تست partition و session/task فعال در M2 | M1/M2 |
| T-07؛ دوام و بازیابی | در قطع پردازش پس از commit، صفر گم‌شدن داده پذیرفته‌شده؛ برای بازیابی از بسته بیرونی، RPO پیشنهادی حداکثر ۲۴ ساعت و RTO حداکثر ۴ ساعت در پایلوت، با تطبیق revoke/پرداخت بیرونی پیش از بازگشایی | M2 |
| T-08؛ اختلال dependency | دست‌کم ۵ حالت قطع/کندی store، PDP، registry، dispatcher و provider آزموده شود؛ صفر پذیرش بدون policy/رزرو معتبر و صفر موفقیت ساختگی | M2/M4 |
| T-09؛ journey | دست‌کم ۱ journey واقعی در ۲ کانال، با ۱ شکست میانی و ۱ retry؛ یک business operation و یک charge قابل توضیح، با trace مرتبط و مجوز بازسنجی‌شده | M3 |
| T-10؛ provider | دست‌کم ۲ provider برای ۱ capability؛ ۱۰۰ job نمونه شامل timeout/cancel؛ صفر credential leak و صفر هزینه نهایی خارج از مدل رزرو/تطبیق تعریف‌شده | M4 |
| T-11؛ Generalist | ۱ تیم شرکت، ۱ محصول و ۱ چرخه نیاز تا انتشار؛ ۱۰۰٪ اقدامات حساس با صاحب اختیار و evidence؛ صفر خروج از محدوده workspace/tenant در سناریوهای منفی | M5 |
| T-12؛ رشد اقتصادی | ۳ دوره گزارش‌گیری پایلوت با تطبیق مصرف/هزینه/حساب تولیدکننده؛ اختلاف بدون توضیح برابر صفر؛ سپس تعیین target پذیرش/حاشیه و SLA بر اساس داده | M6 |

در T-03 سقف burst به الگوریتم منتخب وابسته است؛ جمع TPS و burst یک فرمول ثابت برای همه limiterها نیست. معیار، مدل زمان/وزن/پنجره تعریف‌شده و وضعیت اتمیک store است. آزادسازی lease باید وضعیت اجرای واقعی را رعایت کند. نمونه سهمیه و workload تعهد فروش هیچ مشتری نیست.

در T-07، «صفر گم‌شدن پس از commit محلی» با RPO نسخه backup یکی نیست. restore داده قدیمی را برمی‌گرداند؛ پذیرش مجدد کاربران و پرداخت پس از بازیابی به تطبیق revocationها و مرجع بیرونی وابسته است. هدف RPO/RTO عملیاتی باید پس از تمرین و بر اساس ریسک tenant تعیین شود.

## ۷. ریسک‌ها و پاسخ توسعه

| ریسک | اثر قابل مشاهده | پاسخ و کار مرتبط |
| --- | --- | --- |
| اختلاف منبع و artifact | تغییر ظاهراً درست، قرارداد API یا رفتار امنیتی مستقر را از بین می‌برد | manifest و تطبیق FPC-001 پیش از تغییر؛ حفظ مسیر rollback؛ عدم overwrite ورک‌تری |
| دو رجیستری/دو هویت | Portal و محصول یک کاربر یا سامانه را متفاوت می‌شناسند | یک مرکز ثبت و Keycloak؛ Portal reader؛ FPC-002, FPC-015 |
| یکی گرفتن owner و consumer | SaaS مجاز حذف یا دسترسی تننت دیگر باز می‌شود | مدل مستقل، role binding scoped و سناریوی واقعی cross-tenant؛ FPC-008, FPC-015, FPC-016 |
| stale policy و نقش token | قرارداد پایان یافته هنوز admission می‌گیرد | نسخه، invalidation، freshness bounded و آزمون session/task؛ FPC-018 |
| سهمیه محلی هر replica | مصرف کل با تعداد replica رشد می‌کند | store اتمیک مشترک، key allocation پایدار و آزمون هم‌زمان؛ FPC-012 |
| expiry نادرست رزرو | job ادامه دارد اما ظرفیت آزاد و دوباره مصرف می‌شود | lease/fencing/heartbeat و reconciliation نتیجه واقعی؛ FPC-013, FPC-029 |
| bypass backend یا header جعلی | مسیر مستقیم از محدودیت و هزینه عبور می‌کند | trusted context، backend PEP و محدودیت شبکه؛ FPC-010, FPC-011, FPC-016 |
| ادعای XACML بدون انطباق | خطا یا combining نامعتبر Permit تولید می‌کند | profile pin شده، golden tests و fail-closed؛ FPC-007 |
| رویداد تکراری/خارج از ترتیب | entitlement قدیمی فعال یا هزینه چندباره ایجاد می‌شود | نسخه aggregate، idempotency، outbox replay و reconcile؛ FPC-008, FPC-013, FPC-017 |
| وابستگی همه درخواست‌ها به مرکز | قطع control plane همه اجراها را متوقف یا cache ناامن می‌کند | تفکیک control/data plane، snapshot معتبر با حد تازگی و رفتار قطع آزموده؛ FPC-004, FPC-018, FPC-022 |
| restore بدون تطبیق | grant لغوشده فعال یا پرداخت بیرونی دوباره ثبت می‌شود | restore به مقصد تازه و تطبیق امنیتی/مالی؛ FPC-019 |
| اشتباه گرفتن مصرف با مالی قانونی | تسویه، ارز یا refund به اختلاف تجاری تبدیل می‌شود | مرز دفتر سرویس و سرویس مالی، مسئول نرخ/تسویه؛ FPC-020, FPC-021, FPC-035 |
| هزینه و کیفیت provider/مدل | fallback مجاز نیست یا بودجه/کیفیت محصول افت می‌کند | benchmark، محدودیت داده و budget reservation؛ FPC-027 تا FPC-030 |
| اختیار زیاد عامل/Generalist | دسترسی داده، secret یا انتشار از محدوده شرکت خارج می‌شود | workspace محدود، delegation، review و cancellation واقعی؛ FPC-031 تا FPC-034 |
| مالکیت و سهم مبهم | شرکت، تیم و Generalist بر دارایی/پشتیبانی/درآمد اختلاف دارند | منشور ثبت‌شده و مدل محاسبه قابل بازپخش پیش از پایلوت تجاری؛ FPC-031, FPC-035 |
| مجوز workflow/provider نامناسب | ابزار داخلی بدون مجوز به محصول مشتری تبدیل می‌شود | بررسی مدل دقیق استفاده و قرارداد رسمی قبل از عرضه؛ FPC-025, FPC-027, FPC-035 |
| رشد زودهنگام stack روی VPS | محدودیت حافظه/store و noisy-neighbor پایلوت را بی‌ثبات می‌کند | سهم منابع و capacity evidence؛ rollout محدود؛ FPC-004, FPC-036 |
| brand یا مرکز حدسی | محصول با مرکز/لوگوی اشتباه عرضه می‌شود | تطبیق با برندبوک و دارایی رسمی در پذیرش محصول؛ FPC-002, FPC-035 |

## ۸. شیوه همکاری و ثبت شواهد

مدیر محصول پلتفرم اولویت مشترک را نگه می‌دارد؛ مالک محصول شرکت درباره دامنه و پذیرش محصول تصمیم می‌گیرد؛ تیم قرارداد و مالی مرز entitlement/قیمت/تسویه را تعیین می‌کند؛ امنیت مسئول profile و مدل اعتماد است؛ SRE مسئول انتشار، topology، ظرفیت و بازیابی است. QA سناریوی پذیرش مشترک را به نتیجه قابل بازبینی وصل می‌کند.

برای هر milestone، بسته evidence باید نسخه artifact/config، نسخه قرارداد/policy، داده آزمون بدون اطلاعات حساس، نتیجه مثبت/منفی، محدودیت‌های باقی‌مانده و مسئول پذیرش را داشته باشد. وجود فایل، adapter، UI یا نمودار به‌تنهایی شاهد اتصال زنده نیست. log و evidence نباید credential، token، cookie، body حساس یا اطلاعات شخصی جلسه را منتشر کنند.

در پایان هر milestone، کارهای واقعاً پذیرفته‌شده در برد موجود به evidence پیوند می‌خورند؛ targetها با عدد واقعی مقایسه و محدودیت‌ها ثبت می‌شوند. تغییر target یا دامنه با دلیل و نسخه انجام می‌شود. نیاز بازار، کیفیت یا هزینه می‌تواند اولویت فازهای بعد را تغییر دهد، ولی مرجع هویت/رجیستری، جداسازی تننت، enforcement و تطبیق مصرف حذف نمی‌شوند.

## ۹. منابع و حدود اعتبار

- [معماری مرجع مرکز](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/ARCHITECTURE.md): طراحی هدف این برنامه و مرز control/data plane.
- [انتقال دانش](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/CONTEXT-HANDOFF-2026-10-09.md): سابقه و زمینه؛ تشخیص قدیمی Portal و محدودیت نگارش برنامه در آن، شاهد وضعیت فعلی یا مانع دستور تازه نیستند.
- [معماری محصول موجود](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/docs/ARCHITECTURE.md)، [هویت و سازمان](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/docs/IDENTITY-ORGANIZATION.md)، [پروفایل سیاست موجود](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/docs/GOVERNANCE-XACML.md)، [بازیابی](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/docs/RECOVERY.md)، [پرداخت](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/docs/ZARINPAL.md): قابلیت‌ها و محدودیت‌های مستند فعلی؛ نتیجه fixture جای پذیرش عملیاتی نیست.
- [اتصال و پذیرش توسعه Portal](/E:/SJVS/Projects/Neo/products/Fanasa.UnifiedPortal/docs/CATALOG-INTEGRATION.md): شاهد جدید ۱۰ اکتبر برای اتصال، SSO، سازمان و کاتالوگ خالی.
- NeoCluster/API-GATEWAY.fa.md و NeoCluster/INTEGRATION-ARCHITECTURE.fa.md: هدف APISIX، مسیر مهاجرت، مرز workflow/MCP/ACP/LSP/A2A و محدودیت‌های عرضه؛ این فایل‌ها نصب کامل stack را اثبات نمی‌کنند.
- [OASIS XACML 3.0](https://docs.oasis-open.org/xacml/3.0/xacml-3.0-core-spec-os-en.html): مرجع استاندارد تصمیم/اجرای سیاست؛ انتخاب profile و ادعای انطباق در FPC-007 آزموده می‌شود.
- [برندبوک رسمی فن‌آسا](https://fanasa.net/fa/brand): مرجع مرکز و دارایی برند. دریافت جاری این صفحه در بررسی این سند از ابزار وب موفق نبود؛ این محدودیت به معنی تأیید palette/لوگو/انتساب حدسی نیست.

هیچ اطلاعات خام جلسه، نام/تماس خصوصی، credential، مبلغ تجاری واقعی یا ادعای تخصصی محصول از گزارش‌های خصوصی به این برنامه منتقل نشده است. پیشنهادهای جلسه و تیم ارائه‌دهنده تنها در سطح موضوعات معماری به طرح تبدیل شده‌اند؛ این سند مصوبه آن جلسه نیست.
