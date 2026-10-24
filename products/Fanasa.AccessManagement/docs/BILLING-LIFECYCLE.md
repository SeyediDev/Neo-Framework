# چرخهٔ حساب و صورتحساب

نسخهٔ محلی ۲۴ اکتبر ۲۰۲۶؛ شروع از 98ecb7582862f724d3ba1e57d12505feb983fc6b و ادغام با 4b3cfbccedac2facb356e3742f60387a5f28bad2. [پذیرش نهایی](../../../docs/fanasa-platform-control-center/workstreams/usage-accounting/ACCEPTANCE-2026-10-24.md) نتایج و بستهٔ تحویل را ثبت می‌کند.
این ماژول از Fabric و ledger موجود استفاده می‌کند. بانک، entitlement، درگاه پرداخت و دفتر مالی دیگری ایجاد نشده است.

## مرز ثبت مصرف

در unit.save، تنظیم UsageMetering:Organization:Enabled همچنان پیش‌فرض false است. فعال‌سازی، محصول/مالک، consumer، عضویت و grant زنده، اشتراک یا projection قرارداد، نرخ نسخه‌دار و admission معتبر را می‌خواهد. مدل حساب و پلن باید با snapshot نرخ برابر باشند: payg یا postpaid.

تغییر سازمان، receipt کسب‌وکار، مصرف، charge و outbox در همان تراکنش Fabric ثبت می‌شوند. PAYG پیش از commit موجودی کافی می‌خواهد؛ postpaid از سقف اعتبار عبور نمی‌کند. چون مقدار این عملیات ثابت و اثر محلی اتمی است، رزرو مالی جدا لازم نیست. این برش، رزرو عمومی عملیات بلندمدت/ارائه‌دهندهٔ بیرونی را پیاده نمی‌کند.

HTTP 200 از Gateway، پذیرش workflow یا JSON کاربر، نتیجهٔ معتبر backend نیست. API مرورگر Accounting دیگر operation=usage را نمی‌پذیرد؛ ثبت جدید فقط از typed intake معتبر backend است. مسیر legacy داخلی برای سازگاری موجود می‌ماند و فعال‌سازی هم‌زمان دو نوع metering رد می‌شود.

شناسهٔ operation با تغییر نسخهٔ نرخ یا قرارداد عوض نمی‌شود. retry همسان یک اثر دارد؛ اختلاف actor یا payload تعارض است. validator و شاهد backend از فرم یا credential کاتالوگ ساخته نمی‌شوند.

## مبلغ و تعرفه

IRR بدون رقم خرد و USD/EUR با دو رقم خرد هستند؛ گردکردن AwayFromZero است. ضرب و جمع مالی، ضریب صحیح BigInteger را کنترل می‌کنند تا گردشدن خاموش decimal قبل از رسیدن به واحد خرد رخ ندهد. ورودی API نیز مقدار غیرقابل نمایش دقیق یا exponent را پیش از domain رد می‌کند.

نرخ مؤثر با زمان پذیرش و مرز نیمه‌باز انتخاب و در UsageEvent و ledger حفظ می‌شود. replay قیمت امروز را انتخاب نمی‌کند. API قیمت نسخه‌دار، همان نسخه با محتوای متفاوت را تعارض می‌داند؛ نسخهٔ تازه باید از مرجع مجاز قرارداد ارجاع داده شود.

## دوره و سند ثابت

دوره، ماه میلادی UTC با مرز [from,to) است. زمان وقوع معتبر مصرف مبنای دوره است؛ مصرف قدیمی فقط At دارد. تنها ماه پایان‌یافته قابل صدور است. هر consumer/period یک سند دارد؛ retry همان key/actor/payload اثر تازه ندارد و key متفاوت نیز سند دوم ایجاد نمی‌کند.

سند تازه Currency، From/To، Lines، SourceRevision، Actor، Reference، ContentHash و basis=posted-ledger دارد. هر خط به شناسهٔ charge، محصول، شاخص، quantity، UnitPrice تاریخی، مبلغ ناخالص، اصلاح پیش از صدور و مبلغ خالص متصل است. مبلغ و خطوط سند پس از صدور عوض نمی‌شوند. اسناد چهارفیلدی قدیمی قابل خواندن‌اند؛ جزئیات مفقود برای آن‌ها اختراع نمی‌شود.

پیش از صدور، retained usage همان دوره باید committed و با charge سازگار باشد. pending، شاهد خراب یا عبور از حد scan، صدور را متوقف می‌کند. حد scan حساب و عملیات هرکدام 10000 و اندازهٔ سند حساب حداکثر 8 MiB است؛ ظرفیت نتیجه پیش از commit نیز بررسی می‌شود.

مصرف دیررسِ متعلق به دورهٔ صادرشده خودکار اضافه نمی‌شود. در intake معتبر بیرونی، evidence به accounting-pending می‌رود و سند ثابت می‌ماند؛ local unit.save در چنین خطایی کامل rollback می‌شود. recovery فقط حسابداری همان operation را retry می‌کند و backend را دوباره اجرا نمی‌کند. این نسخه override دورهٔ بسته یا تعرفهٔ جریمه/انتقال خودکار بدهی دیررس به ماه دیگر ندارد؛ این استثنا نیازمند تصمیم مالی مورد تأیید مالک است.

## اصلاح اعتباری

Credit note به یک charge موجود همان consumer و، در دورهٔ بسته، InvoiceId دقیق وصل می‌شود. Amount مثبت، Reason، Reference، actor نشست و key لازم‌اند. اصلاح نمی‌تواند از مبلغ اصلاح‌نشدهٔ charge بیشتر شود. در postpaid، باقیماندهٔ پرداخت‌نشده و رزرو‌نشدهٔ invoice نیز حد است.

اصلاح پیش از صدور در خط خالص سند آینده دیده می‌شود. اصلاح پس از صدور، ledger و outbox جدا دارد و ContentHash سند قبلی را تغییر نمی‌دهد. PAYG فقط اعتبار محلی را برمی‌گرداند. اصلاح invoice تسویه‌شده که به refund/انتقال وجه نیاز داشته باشد، رد می‌شود؛ این کد refund بانکی ایجاد نمی‌کند.

پرداخت‌های باز زرین‌پال همچنان رزرو خود را دارند. پرداخت دستی یا credit note نمی‌تواند همان ماندهٔ رزروشده را دوباره مصرف کند؛ ثبت نتیجهٔ خود درگاه فقط رزرو همان intent را مستثنا می‌کند. وضعیت نامشخص درگاه آزادسازی یا درخواست بانکی تازه ایجاد نمی‌کند.

## API نسخهٔ ۱

| روش و مسیر | مجوز و رفتار |
| --- | --- |
| GET /api/accounting/v1/consumers/{consumer}/usage-report?from=...&to=... | Bearer انسانی Portal، client/scope/audience مصوب، نشست sub/sid، عضویت فعال و billing.read زنده |
| GET /api/accounting/v1/consumers/{consumer}/account | همان مرز انسانی، بدون query یا subject ورودی؛ مبلغ‌های محدود رشته‌ای، بدون ledger/raw evidence |
| GET /api/accounting/v1/tenants/{tenant}/account | Cookie انسانی و billing.read زنده؛ بررسی مجدد پس از snapshot |
| POST /api/accounting/v1/tenants/{tenant}/invoices | Cookie، CSRF و billing.write زنده در همان writer |
| POST /api/accounting/v1/tenants/{tenant}/credit-notes | همان مرز مالی؛ اصلاح محدود و قابل حسابرسی |

در دو GET مصرف‌کننده، شناسهٔ تمام‌صفر با تنظیمات معتبر HTTP 400 و no-store می‌دهد؛ دادهٔ مالی خوانده یا تغییر نمی‌شود. احراز Bearer پیش از controller برقرار است و درخواست ناشناس همچنان 401 می‌گیرد. feature خاموش یا تنظیمات ناقص، پیش از بررسی ورودی 503 می‌دهد.

بدنهٔ صدور: ExpectedRevision، Key، Period به شکل yyyy-MM و Reference.
بدنهٔ اصلاح: ExpectedRevision، Key، ChargeId، InvoiceId یا null، Amount دقیق، Reason و Reference.
CSRF در X-CSRF-TOKEN ارسال می‌شود. PlatformAdmin به‌تنهایی مجوز مالی tenant نمی‌دهد.

گزارش مصرف، مجموع ناخالص usage ثبت‌شده است؛ credit-note را به مصرف منفی یا سهمیه تبدیل نمی‌کند. گزارش حساب schemaVersion=1 و status برابر ready یا notRecorded دارد. در خطای منبع، HTTP 503 بدون اطلاعات مالی جزئی بازمی‌گردد. در notRecorded، ارز/مانده/ردیف مالی null یا خالی‌اند؛ صفر واقعی فقط برای حساب ثبت‌شده است.

در صورت‌حساب postpaid، amount ثابت، creditedAmount اصلاح پس از صدور، paidAmount پرداخت و remainingAmount باقیمانده‌اند؛ status برابر issued، partiallyPaid، paid یا credited است. در PAYG، status برابر statement و remainingAmount مبلغ اصلاح‌نشدهٔ صورت‌مصرف است؛ مطالبهٔ پرداخت دوم نیست و Outstanding صفر می‌ماند.

همهٔ خواندن‌های مالی no-store هستند. subject انتقالی فقط envelope سمت سرور است؛ توکن/subject به Bootstrap مرورگر صادر نمی‌شود. بخش نمایشی، سازمان و دورهٔ انتخابی را با پاسخ تطبیق می‌دهد و دادهٔ انتخاب قبلی را پاک می‌کند.

## Portal و صفحات مالی

Accounting مانده/بدهی، تعرفه، سند ثابت و دفتر را نشان می‌دهد؛ Billing به همان صفحه هدایت می‌شود. مبلغ و revision پیش از JSON.parse به رشتهٔ دقیق محافظت می‌شوند؛ تفریق مبلغ با BigInt انجام می‌شود. نتیجهٔ ثبت نامشخص، key و بدنهٔ همان دستور را در sessionStorage با scope مشتق از نشست/سازمان نگه می‌دارد. بارگذاری مجدد، دستور تازه نمی‌سازد؛ همان دستور قابل پیگیری است.

Portal برای خواندن مالی فقط https://api.fanasa.net.local روی 443 را می‌پذیرد؛ دسترسی مستقیم به PCC یا fallback کاتالوگ ندارد. ConsumerBilling.Enabled پیش‌فرض false است. Audience و Scope باید از قبل برای client انسانی مصوب باشند؛ این بسته scope/grant/client تازه ایجاد نمی‌کند.

Access token معتبر OIDC حداکثر پنج دقیقه و تا expiry خودش، فقط در حافظهٔ سرور می‌ماند. در ticket محافظت‌شده فقط handle تصادفی است؛ SaveTokens=false، refresh token و browser storage توکن وجود ندارند. sub/sid/issuer/audience/client/scope/timing و امضا بررسی می‌شوند؛ logout handle را پاک می‌کند. restart یا expiry، گزارش را اتصال‌نیافته می‌کند و ورود مجدد می‌خواهد.

## اجرای محلی و پذیرش

ساخت و آزمون‌ها در ورک‌تری مجزا انجام می‌شوند. وابستگی‌ها از cache و با NuGetAudit=false فقط برای ساخت آفلاین این نوبت بازیابی شدند؛ این نتیجه جای CI و audit آنلاینِ commit منتشرشده نیست.

سناریوی unit.save در PaygMetering.Tests از خود اجرای مصنوعی، evidence JSON تولید می‌کند: نرخ، operation، یک charge، گزارش مبلغ، invoice ثابت و account summary. BillingLifecycle.Tests، Billing.Api.Tests و اجرای --financial-regression پروژه Organization.Tests، ریسک‌های مالی/مجوز/قطع و recovery را پوشش می‌دهند. راهنمای بازتولید و شمار نهایی هر suite در تحویل پذیرش این نوبت ثبت می‌شود.

## گام‌های عملیاتی برای مالک انتشار

1. ادغام بسته بر پایهٔ دقیق و بررسی hash فایل‌های مشترک؛ develop/master انتشار در این مأموریت تغییر نمی‌کنند.
2. CI و artifact معتبر روی commit نهایی و بازبینی امنیت وابستگی‌ها.
3. provisioning مجاز consumer/owner، قرارداد/نرخ/پلن، client و scope انسانی، billing.read/write و trusted admission واقعی؛ دادهٔ fixture جای آن نیست.
4. انتشار دو GET مالی از Gateway موجود با Bearer اصلی، no-store، عدم log کردن Authorization و منع bypass؛ نصب route/feature به مالک انتشار و Gateway تعلق دارد.
5. backup همان Fabric و آزمون restore، سپس فعال‌سازی محدود feature و پذیرش SSO، لغو grant، tenant دیگر، قطع و reconciliation.
6. درگاه زرین‌پال، callback، پذیرندگی و شارژ واقعی جداگانه پذیرفته شوند؛ هیچ پرداخت یا انتقال وجه واقعی در این نوبت انجام نشده است.

صورتحساب این ماژول صورت مصرف خدمت است؛ صورتحساب قانونی، مالیات، ERP، payout، نسبت درآمد و سیاست تجاری تسویه از آن نتیجه نمی‌شود.
