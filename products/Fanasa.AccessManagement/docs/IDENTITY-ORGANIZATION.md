# راهبری هویت و سازمان فن‌آسا

تاریخ تصمیم: ۲۰۲۶-۱۰-۰۶. نام نمایشی محصول به «سامانه راهبری هویت و سازمان
فن‌آسا» گسترش یافت؛ شناسه مخزن، کلاینت OIDC و host فعلی برای سازگاری حفظ شد.

## مرز معماری

در این مرحله یک modular monolith با ماژول Organization Fabric انتخاب شده است.
مدیریت سازمان، واحد، نقش سازمانی، سمت، انتصاب و تاریخچه یک مرز مستقل دارد؛
کاتالوگ محصول و عضویت در Access باقی می‌ماند و احراز هویت در Keycloak است.
تقسیم زودهنگام سرویس‌ها تراکنش تغییر ساختار و انتصاب را توزیع‌شده می‌کند؛
استخراج سرویس مستقل پس از نیاز واقعی مقیاس و مالکیت تیم انجام می‌شود.

نقش سازمانی تعریف قابل استفاده مجدد است؛ سمت آن نقش را در یک واحد با ظرفیت
مشخص قرار می‌دهد؛ انتصاب، عضو دارای `sub` را در بازه نیمه‌باز `[from,to)`
به سمت متصل می‌کند. این رابطه به‌تنهایی هیچ مجوز نرم‌افزاری تولید نمی‌کند.
برای grant مشتق‌شده، سیاست صریح، تأیید، انقضا و مصرف رویداد lifecycle لازم است.
تغییر والد، چرخه، کد تکراری، ظرفیت هم‌پوشان و عضو tenant دیگر کنترل می‌شوند.
واحد/سمت دارای فرزند یا انتصاب معتبر بدون خاتمه روابط بایگانی نمی‌شود.

## API و authorization

- `GET /api/organization/tenants/{tenantId}`: ساختار جاری.
- همان مسیر با `?revision=N`: snapshot تاریخی، فقط خواندنی.
- `POST` همان مسیر: `OrgCommand` با `expectedRevision` و `reason`.
- عملیات: `unit.save`, `unit.archive`, `role.save`, `position.save`,
  `position.archive`, `appointment.add`, `appointment.end`.

سازمان از claim دقیق `tenant_id` و مجوز از claim `permission` خوانده می‌شود.
خواندن نیازمند `organization.read`، تغییر نیازمند `organization.write` است.
این claimها باید server-side از مرجع قابل اعتماد وارد session شوند؛ کاربر حق
انتخاب claim ندارد. OIDC ممکن است `sub` را به NameIdentifier نگاشت کند.
POST با cookie نشست و antiforgery header `X-CSRF-TOKEN` انجام می‌شود.
خطای ورودی ۴۰۰، نبود رکورد ۴۰۴، مجوز/اشتراک نامعتبر ۴۰۳ و تعارض نسخه ۴۰۹ است.

`Organization:RequireSubscription` در production پیش‌فرض true دارد؛ اشتراک فعال
محصول `organization-fabric` با تاریخ معتبر لازم است. در توسعه پیش‌فرض false است.
ثبت محصول باید در همین registry مرکزی، با مرکز رسمی مناسب انجام شود؛ برای
ساختار سازمانی آیین پیشنهاد معماری است، تأیید جایگاه باید با برندبوک انجام شود.
هیچ catalog دوم یا ثبت خودکار در محیط بیرونی ایجاد نشده است.

## اکانتینگ

`/Accounting` رابط زنده حساب، دفتر تراکنش و صورتحساب است. `/Billing` صفحه
معماری قبلی است؛ مسیر جدید به آن مدل آزمایشی Charge وابسته نیست.

- `GET/POST /api/accounting/tenants/{tenantId}`.
- `configure`: `mode=payg|postpaid`, `currency=IRR|USD|EUR`, `creditLimit`.
- `tariff`: تعرفه محصول ثبت‌شده و `metric` با `unitPrice`.
- `usage`: ثبت مقدار مثبت؛ مبلغ توسط سرور از تعرفه محاسبه می‌شود.
- `credit`: ثبت شارژ تأییدشده حساب payg همراه مرجع؛ درگاه پرداخت نیست.
- `invoice`: صدور صورتحساب ماه میلادی پایان‌یافته با `period=yyyy-MM`.
- `payment`: تسویه کامل/جزئی صورتحساب postpaid با `invoiceId` و مرجع.

خواندن به `billing.read`، تغییر حساب/تعرفه/تسویه به `billing.write`، ثبت مصرف
به `billing.meter` نیاز دارد؛ تمام عملیات scoped به tenant claim هستند.
هر دستور `expectedRevision`, `key`, `reference` دارد. کلید مشابه با payload
مشابه دوباره اثر نمی‌گذارد؛ payload متفاوت با همان کلید ۴۰۹ می‌دهد. تعرفه
جدید مبلغ مصرف ثبت‌شده را تغییر نمی‌دهد. پس از اولین تراکنش ارز/مدل ثابت است.

Pay as you go محور قیمت‌گذاری مصرفی است؛ در این MVP مسیر payg با کیف اعتبار
پیش‌پرداخت و مسیر postpaid با همان قیمت‌گذاری مصرفی و تسویه دوره‌ای اجرا می‌شود.
بدهی جاری شامل مصرف صورتحساب‌نشده است و سقف اعتبار پیش از ثبت مصرف کنترل می‌شود.
پایان دوره به UTC و ماه میلادی است؛ مصرف به زمان ثبت سرور منظور می‌شود.
مصرف دیررس، backdating، مالیات، تخفیف، credit note، تعرفه پلکانی، هزینه ثابت،
تبدیل ارز، دفترکل دوبل، درگاه بانکی و صورتحساب قانونی در این نسخه وجود ندارند.
این دفتر «اکانتینگ مصرف سرویس» است؛ دفترکل قانونی باید با سرویس مالی یکپارچه شود.

شاخص‌های پیشنهادی سازمان: `organization.unit-month`, `organization.position-month`,
`organization.appointment-month`, `organization.change`؛ worker اندازه‌گیری و
outbox هنوز پیاده نشده‌اند. ثبت تغییر سازمانی و billing اکنون دو تراکنش مستقل‌اند؛
وصل‌کردن مستقیم آن‌ها بدون outbox ممکن است ثبت مالی ناقص ایجاد کند.

## persistence و حدود استقرار

آداپتورهای جدید تک‌میزبان هستند: JSON هر tenant، lock درون پردازش، flush فایل
موقت و rename اتمیک؛ snapshot هر نسخه سازمانی حفظ می‌شود. مسیرهای
`Organization:DataDirectory` و `Accounting:DataDirectory` باید روی volume پایدار
با ACL محدود و backup قرار گیرند. `App_Data` در git ignore است.
history سازمان فقط تا revision منتشرشده خوانده می‌شود؛ فایل آماده‌شده پیش از
شکست commit به کاربر نشان داده نمی‌شود. audit عامل، دلیل، زمان و snapshot دارد؛
tamper-proof نیست. چند پردازش روی یک مسیر فایل پشتیبانی نمی‌شود.

مرجع فعلی tenant/user/product هنوز در Program از InMemoryAccessManagement است؛
تغییرات SQL قبلی موجود در workspace در این کار بازنویسی یا فعال نشده‌اند.
بنابراین persistence جدید به معنی production-ready بودن کل سامانه نیست.
پیش از SaaS عملیاتی: adapter پایدار عضویت/اشتراک، migration، کنترل tenant در
APIهای قدیمی Access، outbox/inbox، billing worker، policy claims، backup/restore
و آزمون واقعی SSO/دیتابیس ضروری‌اند. APIهای قدیمی فعلاً فقط authenticated هستند
و باید جداگانه سخت‌سازی شوند؛ endpointهای جدید مجوز صریح دارند.

## تجربه بصری و برند

صفحه `/Organization` شامل چارت، انتخاب کارت، جست‌وجو همراه حفظ والدها، zoom،
جزئیات نقش و سمت، انتصاب زمان‌دار، ویرایش و انتقال، بایگانی و timeline است.
RTL، responsive، focus قابل مشاهده و prefers-reduced-motion رعایت شده است.
برندبوک https://fanasa.net/fa/brand در زمان کار از ابزار وب و HTTP محلی در دسترس
نبود؛ تطبیق رسمی رنگ، لوگو و جایگاه مرکز تأیید نشده است. هویت جدید اختراع نشده.
فایل SVG موجود در UnifiedPortal بدون تغییر محتوا به محصول منتقل شد؛ نشان متنی
«ف» با همین لوگوی ذخیره‌شده جایگزین شد. دریافت نسخه جاری از مرجع رسمی تأیید نشده.

## اعتبارسنجی

نتیجه این اجرا: ۳۱ بررسی رفتاری پاس شد؛ build بدون warning/error و syntax check
هر دو فایل JavaScript موفق بود. در میزبان fixture، ثبت واحد از رابط با CSRF
معتبر انجام شد و POST بدون token با ۴۰۰ رد شد. نسخه تاریخی دکمه ثبت را غیرفعال
کرد. صفحات Organization و Accounting در مرورگر بررسی شدند؛ سرریز کل صفحه در
نمای ۴۷۲px اصلاح و نمای ۱۴۴۰px نیز بررسی شد. این شواهد جایگزین آزمون SSO واقعی نیست.

`dotnet run --project products/Fanasa.AccessManagement/tests/Organization.Tests`
آزمون invariants، cross-tenant، نسخه هم‌زمان، تاریخچه، اشتراک SaaS، idempotency،
اعتبار، صدور و تسویه صورتحساب را اجرا می‌کند. `--preview` میزبان fixture روی
127.0.0.1:5198 برای بررسی صفحات واقعی Razor می‌سازد؛ هویت آن فقط در assembly
آزمون است، به host محصول اضافه نشده و هیچ داده مشتری را مصرف نمی‌کند.

## منابع معماری بررسی‌شده

- [Keycloak Server Administration](https://www.keycloak.org/docs/latest/server_admin/):
  احراز هویت و مدیریت realm در مرجع مرکزی باقی می‌ماند.
- [Microsoft multitenant lifecycle](https://learn.microsoft.com/en-us/entra/architecture/multi-tenant-user-management-introduction):
  تفکیک هویت مرجع، tenant منبع و lifecycle عضویت.
- [Stripe usage-based billing](https://docs.stripe.com/billing/subscriptions/usage-based):
  تفکیک اندازه‌گیری، قیمت‌گذاری و تسویه؛ صرفاً مرجع الگو، بدون اتصال Stripe.
- [Stripe metering idempotency](https://docs.stripe.com/billing/subscriptions/usage-based/recording-usage-api):
  جلوگیری از ثبت تکراری رویداد مصرف.

مدیریت کار: API تنظیم‌شده محلی localhost:5180 پاسخ نداد و ابزار عملیاتی Neo
در این نشست موجود نبود؛ claim/task/time/evidence در برد ثبت نشده است. این سند
تصمیم معماری است و برد عملیاتی دوم نیست. Git فقط تغییرات همین درخواست را ثبت
می‌کند؛ تغییرات قبلی EF/SQL و publish از commit این کار کنار گذاشته می‌شوند.
