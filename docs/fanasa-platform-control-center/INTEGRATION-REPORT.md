# گزارش تطبیق معماری فن‌آسا

تاریخ: ۱۰ اکتبر ۲۰۲۶. مرجع: [محدوده و نگاشت اتصال](INTEGRATION-ALIGNMENT.md) و [هماهنگی اجرا](EXECUTION-COORDINATION.md).

## دامنه و وضعیت پذیرش

این گزارش فقط اتصال محلی سه بستهٔ قرارداد/سیاست، API Gateway و مصرف/Accounting را ثبت می‌کند. یک محصول `organization-fabric`، عملیات `organization.unit.save`، owner و consumer متفاوت و allocation مشترک استفاده می‌شوند. [binding مشترک](integration/fixture-binding.v1.json) مصنوعی است؛ ثبت محصول، grant، قرارداد تجاری، نرخ مصوب یا مجوز واقعی از آن نتیجه نمی‌شود.

سیاست و حسابداری در مرز ماژول‌های PCC موجود می‌مانند. اجرای این کار سرویس، registry، broker، workflow engine یا پایگاه عملیاتی تازه‌ای اضافه نکرده است. APISIX و Redis مقصد اجرای پایلوت‌اند؛ اجرای native و انتشار هنوز پذیرفته نشده‌اند.

**تطبیق محلی سه بسته موفق است.** Gateway همان Decision تولیدشده را مصرف کرد و Accounting همان شاهد admission قطعی را گرفت. یک عملیات کسب‌وکار مصنوعی commit و یک بار بدهکار شد؛ duplicate و replay بدهی تازه ایجاد نکردند. این نتیجه، پذیرش runtime یا محصول واقعی نیست. شمار آزمون‌های مستقل اولیه، جایگزین این شواهد مشترک محسوب نمی‌شود.

## شواهد اتصال مشترک

| بخش | بررسی اجراشده و نتیجه | مرز پوشش |
| --- | --- | --- |
| قرارداد/سیاست | ۱۷۱ موفق؛ ۱۴۵ قبلی و ۲۶ تازه؛ بازتولید چهار fixture برابر با bytes ذخیره‌شده | projector/evaluator و schema داخلی، بدون اثر جانبی |
| API Gateway | ۶۵ آزمون موفق و candidate validation موفق؛ دو آزمون این مجموعه، ۱۴ حالت مشترک را بررسی می‌کنند | Lua واقعی PEP و limiter با Lupa/Fakeredis؛ transportها مصنوعی‌اند |
| Usage/Accounting | ۵۳ بررسی اتصال موفق؛ ۶۲ بررسی regression و ۲۶ آزمون ساختاری موفق؛ schema رویداد مشترک پذیرفته شد | Fabric/Accounting models موجود و نمونهٔ پیشنهادی append؛ backend و نرخ مصنوعی‌اند |

این تعدادها گروه‌های متفاوت و بعضاً تو‌در‌تو هستند و یک مجموع مستقل از آن‌ها گزارش نمی‌شود. فرمان‌ها و خروجی دقیق در شواهد هر مالک ثبت‌اند: [سیاست](workstreams/contract-policy/INTEGRATION-EVIDENCE.md)، [گیت‌وی](/E:/SJVS/Projects/NeoCluster/fanasa-platform-workstreams/api-gateway/INTEGRATION-REPORT.md) و [حسابداری](workstreams/usage-accounting/EVIDENCE.md).

Gateway قبل از admission، tenant/resource/scope، هر دو obligation، تنها یک policy reference، بازهٔ Int32 نسخهٔ قرارداد، revision مستقل و اعتبار زمانی را کنترل می‌کند. فقط نتیجهٔ قطعی allowed/OK شاهد موفق می‌سازد. تصمیم ردشده، tenant اشتباه، obligation ناقص، نسخه/دورهٔ نامعتبر و انقضا مانع عبورند. timeout یا admission مبهم شاهد و forwarding ایجاد نمی‌کند؛ سهمیه‌ای که شاید مصرف شده، کورکورانه پس داده نمی‌شود.

کلید allocation و scope مستقل از نسخهٔ قرارداد/سیاست می‌ماند. regressionهای allocation مشترک میان دو route و سه client شبیه‌ساز، تغییر revision، سهمیهٔ صفر، concurrency و عدم reset گذشتند. درخواست نمی‌تواند دورهٔ تازه را provision کند. این clientها، replicaهای APISIX مستقر نیستند.

در Accounting، Permit/admission به‌تنهایی debit نمی‌سازد. شاهد commit backend مصنوعی با business key و fingerprint همان operation/result/snapshot لازم است. نمونه با قیمت تاریخی مصنوعی **۱۲ IRR**، یک debit consumer، یک usage و یک outbox ثبت کرد؛ owner بدهکار نشد. همان event دوباره بی‌اثر بود و delivery/attempt تازه برای همان operation فقط receipt دوم ساخت. بازارسال رسید ثبت‌شده هنگام نبود resolver admission، نتیجهٔ همان رسید را حفظ کرد و تصمیم پشت شناسهٔ موجود عوض نشد. تمام حالت‌های منفی پیش از charge، دفتر/receipt/usage/outbox را بدون تغییر گذاشتند.

بازبینی مستقل کد چهار مرز را اصلاح کرد: دقت زیرثانیهٔ ساعت Gateway، بازسازی canonicalScope، سقف اعتبار بر اساس token expiry در Accounting و اتصال actor به subject UUID گیت‌وی. آزمون انقضای `.500` و پایان Redis در `.750`، forwarding و شاهد موفق را رد کرد. پس از اصلاح‌ها، ایراد اساسی باقی‌مانده‌ای در منطق بررسی‌شده گزارش نشد؛ این بازبینی گیت‌های runtime را کامل نمی‌کند.

## خروجی سیاست

[Decision تولیدشده](workstreams/contract-policy/fixtures/integration-decision.v2.json)، نسخهٔ schema برابر `2.0.0` و profile برابر `fanasa.contract-policy/2`، از projector/evaluator واقعی همین نمونه تولید شده است. این DTO داخلی، ادعای انطباق کامل OASIS XACML ندارد. هر دو obligation الزام تخصیص و tenant منبع حاضرند؛ `configRevision=1` مستقل از نسخهٔ قرارداد و سیاست است.

- بررسی‌های مرتبط: ۱۷۱ موفق، صفر شکست؛ ۱۴۵ بررسی قبلی بدون تغییر و ۲۶ بررسی تازه.
- بازتولید چهار fixture با فرمان `node sample/generate-integration-fixture.cjs --check`، byte-for-byte موفق بود.
- SHA-256 خروجی Decision: `17eebeb459c6c58d3ed5d82af64b15c55eb1cde8b8664e27e40c26fffca75ac3`.
- SHA-256 binding مشترک: `4db84427c46041d209b1f965ea694b907e1c24f02a7dcfbcb5301bd47978966c`.
- [شواهد سیاست](workstreams/contract-policy/INTEGRATION-EVIDENCE.md) و [manifest فعلی](workstreams/contract-policy/manifest.v2.json) فرمان‌ها، فایل‌های تغییرکرده و fingerprintها را نگه می‌دارند.

## خروجی‌های واقعی و اثر انگشت

| خروجی | SHA-256 |
| --- | --- |
| [binding مشترک](integration/fixture-binding.v1.json) | `4db84427c46041d209b1f965ea694b907e1c24f02a7dcfbcb5301bd47978966c` |
| [Decision واقعی evaluator](workstreams/contract-policy/fixtures/integration-decision.v2.json) | `17eebeb459c6c58d3ed5d82af64b15c55eb1cde8b8664e27e40c26fffca75ac3` |
| [شاهد قطعی Gateway](/E:/SJVS/Projects/NeoCluster/fanasa-platform-workstreams/api-gateway/fixtures/integration-admission.v1.json) | `ff9131c1bae7fbe211673e1151bfca73ab57194f61b2c6b4054001f22608ae15` |
| [UsageEvent مشترک](workstreams/usage-accounting/fixtures/integration-usage-event.v1.json) | `502757d9cd46998f5947a6f18086b2c862655b744d41788e1bdd8e8a6f025b0b` |
| [دفتر نمونه](workstreams/usage-accounting/fixtures/integration-ledger.v1.json) | `841359ba1c7e26670c4d32431467230056af707ba660e7ecf58c34799817fc2c` |

نسخهٔ schema سیاست `2.0.0` و UsageEvent همچنان `1.0` است. نسخه‌های policy=3، contract=7، allocation revision=1 و rate=7 معنا و چرخهٔ مستقل دارند. [شواهد جاری Gateway](/E:/SJVS/Projects/NeoCluster/fanasa-platform-workstreams/api-gateway/integration-local-verification.json) به source و predecessorهای نهایی اشاره می‌کند؛ `verification.json` و عدد تاریخی ۶۴، شواهد پیش از اصلاح‌اند. [manifest حسابداری](workstreams/usage-accounting/fixtures/integration-evidence.v1.json) همان Decision و شاهد قطعی را pin کرده است. فایل‌ها و فرمان‌های تغییرکرده نزد هر مالک فهرست شده‌اند؛ تغییر فایل مشترک runtime در این اصلاح انجام نشد.

هماهنگ‌کننده پس از ثابت‌شدن فایل‌های نهایی، manifestها را مستقل با `Get-FileHash -Algorithm SHA256` تطبیق داد: ۳۰ ورودی سیاست و binding، ۱۶ ورودی Gateway و سه predecessor و پنج ورودی شواهد boundary، و ۱۶ ورودی حسابداری همگی برابر بودند. پنج کپی منتشرشدهٔ حسابداری با خروجی‌های اصلی برابر بودند. مرجع مشترک Decision/admission/principal و دفتر نهایی نیز کنترل شد: یک debit، یک اجرای backend، یک usage/outbox، دو receipt و بدهی صفر owner. این کنترل فقط‌خواندنی، suiteها را دوباره اجرا نکرد.

کنترل نهایی اسناد و fixtureها: ۲۹ Markdown و ۲۱۶ پیوند محلی، صفر مقصد مفقود؛ ۳۵ فایل JSON با `ConvertFrom-Json` parse شدند. `git diff --check` محدود به این پوشه نیز موفق بود؛ چون اسناد untracked هستند، کنترل محتوا و پیوندها جداگانه انجام شد.

## گیت‌های باقی‌مانده

پذیرش runtime جداست: موجودی و نسخهٔ واقعی، استقرار APISIX/Redis/TLS، اصالت PDP/PIP و producer، provisioning معتبر allocation، PEP داده در backend، نرخ معتبر، اتصال store/outbox و جلوگیری از bypass باید در محیط واقعی آزموده شوند. هیچ VPS یا سرویس مستقر در این کار تغییر نکرده است.

## وضعیت چت‌ها

چهار چت معماری کاربر ایجاد شده‌اند؛ سه چت اصلی قبلاً بسته‌های محلی خود را تحویل دادند. ادامهٔ اصلاح compatibility اکنون توسط تیم داخلی هماهنگ‌کننده در همان directoryها انجام می‌شود، چون پیگیری چت‌ها اجرای ابزار تازه ایجاد نکرد. چت «Fanasa Architecture Integration» مقصد بازبینی و ادامهٔ بعدی است و اجرای تازهٔ آن تأیید نشده است.

دو چت «پنجره واحد» و «از گیت تا دپلوی» ادامهٔ واقعی و تحویل محلی داشته‌اند. شروع اجرایی تازهٔ پنج چت «پنل مدیریت کار»، «بررسی LSP در زیرساخت نئو»، «مدیریت قراردادها»، «فن آسا SSO» و «تهیه و به‌روزرسانی پاورپوینت معماری» تأیید نشده است؛ کار آخرین چت، صفحهٔ Architecture محصول است و PowerPoint لغوشده احیا نشده است. علت داخلی تحویل پیام‌های بدون اجرا اثبات نشده؛ مسیر CLI آزموده‌شده نیز به تعارض active writer رسید. شرح مأموریت و شواهد محدودیت در [هماهنگی اجرا](EXECUTION-COORDINATION.md) ثبت‌اند. مجوز کاربر برای این کار موجود بود؛ توقف این چت‌ها به نبود مجوز نسبت داده نمی‌شود. چت یا اتوماسیونی متوقف، آرشیو یا دوباره ایجاد نشده است.
