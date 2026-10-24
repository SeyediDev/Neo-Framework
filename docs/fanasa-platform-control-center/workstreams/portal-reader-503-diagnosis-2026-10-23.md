# تشخیص 503 readerهای مرکزی — ۲۳ اکتبر ۲۰۲۶

بررسی خواندنی VPS در `2026-10-23T12:49:25Z`، برابر ۱۶:۱۹:۲۵ تهران، علت مستقیم هر دو 503 را تأیید کرد: تنظیمات reader در پردازش مرکزی وجود ندارد. [رسید پالایش‌شده](portal-readers-evidence-2026-10-23/runtime-503-readonly.json) شامل وضعیت سرویس، مسیر release، SHA-256 اسمبلی، حضور تنظیمات، متن خطای HTTP و شمارش داده‌هاست. [اسکریپت بررسی](portal-readers-evidence-2026-10-23/runtime-503-readonly.sh) credential یا token ذخیره نمی‌کند.

سرویس مرکزی `fanasa-access-management@development.service` فعال بود و از release `hr-development-958e9abd658b-55f9cb82f454` اجرا می‌شد. سرویس پنجرهٔ واحد نیز فعال و release آن `b2e36cf183737ab52aa6a37a82b285294cdd9fcb` بود. نشانی مرکزی Portal برابر `http://127.0.0.1:5191` است؛ بنابراین این مشاهده مربوط به میزبان systemd مرکزی است. وضعیت پیکربندی reader در Kubernetes در این بررسی مشاهده نشده است.

| درخواست با credential موجود Portal | پاسخ زنده |
| --- | --- |
| `GET /api/platform/published-services` | 503؛ `Published service catalog is not configured.` |
| `GET /api/platform/application-tenants?subject=ffffffff-ffff-4fff-bfff-ffffffffffff` | 503؛ `Consumer application catalog is not configured.` |

پیکربندی پایه، فایل مخصوص نام محیط فعال `development`، environment پردازش و overrideهای command line برای کلیدهای مشخص بررسی شدند. سه کلید زیر در پیکربندی مشاهده‌شده غایب‌اند:

- `Platform:ServiceCatalog:ClientId`
- `Platform:ServiceCatalog:ClientProductKey`
- `Platform:ConsumerCatalog:Enabled`؛ مقدار پیش‌فرض false است.

در سورس [published-services](../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/PublishedServiceCatalog.cs)، نبود یکی از دو کلید اول همین پاسخ را پیش از بررسی اختیار reader صادر می‌کند. [مسیر consumer](../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/ConsumerApplicationCatalog.cs) علاوه بر آن‌ها فعال‌بودن کلید سوم را می‌خواهد. متن پاسخ‌های زنده شاخهٔ «not configured» را از خطای ذخیره‌سازی «unavailable» متمایز می‌کند.

دریافت توکن از Keycloak با credential موجود و scope موجود `platform.catalog` پاسخ 200 داشت. سه تشخیص claim نیز موفق بودند: `azp` برابر client موجود، scope موردنظر حاضر و audience مرکزی حاضر بود. TLS با CA سیستم و کنترل نام میزبان اعتبارسنجی شد. token فقط در حافظه ماند. این مشاهده عبور از کنترل‌های ثبت محلی reader و همهٔ کنترل‌های claim را اثبات نمی‌کند؛ درخواست‌ها در guard تنظیمات متوقف شدند.

| وضعیت دادهٔ مشاهده‌شده با SQLite در mode=ro | نتیجه و اثر |
| --- | --- |
| محصول مرکزی | ۵ رکورد؛ JSON آن‌ها قابل‌خواندن بود. |
| client در Fabric | ۱ رکورد؛ تعداد تطبیق دقیق با `fanasa-unified-portal-service` صفر است. اتصال این client به محصول و مالک در Fabric هنوز برقرار نیست. |
| tenant و membership در Fabric | به‌ترتیب ۴ و ۸ رکورد؛ درستی عضویت کاربر واقعی در این بررسی ارزیابی نشد. |
| `platform.service-publication.v1` | صفر رکورد؛ reader آماده باید برای نبود publication پاسخ `200 []` بدهد. |
| `platform_application_offers` و `platform_application_grants` | هر دو جدول موجود و هر دو خالی‌اند؛ برای نمایش محصول مجاز مصرف‌کننده داده‌ای ندارند. ستون‌ها و صحت معنایی schema در این بررسی ممیزی نشد. |

پیش‌نیازهای آماده‌سازی به ترتیب وابستگی:

1. نسخهٔ مرکزی مورد انتشار باید با قرارداد تحویل محلی تطبیق داده شود و ساخت یکپارچهٔ آن موفق باشد. [تحویل محلی](portal-central-readers-2026-10-23.md) شکست ساخت مشترک PIP را جداگانه ثبت کرده؛ رفع بعدی آن در بررسی خواندنی VPS ارزیابی نشد؛ [ساخت و آزمون محلی مستقل پس از PIP](portal-post-pip-integration-2026-10-23.md) سپس رفع مانع را با ساخت Release و آزمون‌های موفق تأیید کرد. SHA-256 اسمبلی زنده به‌تنهایی برابری release زنده با سورس محلی را اثبات نمی‌کند.
2. `ClientId` مرکزی باید به همان client موجود Portal اشاره کند؛ `ClientProductKey` باید کلید دقیق محصول ثبت‌شدهٔ آن باشد. زیر همان محصول دقیقاً یک client فعال، با نوع `service` و `AllowedScopes` شامل `platform.catalog` لازم است؛ محصول نیز باید دقیقاً یک مالک tenant فعال داشته باشد. صفر بودن تطبیق فعلی client پیش‌نیاز مستقلِ برآورده‌نشده است. کلید محصول از ایمیل یا نام client حدس زده نشده است.
3. برای consumer، `Platform:ConsumerCatalog:Enabled=true` در تنظیمات مرکزی لازم است. برای publication دارای لینک، میزبان‌های HTTPS مجاز در `Platform:ServiceCatalog:OfficialLinkHosts` و metadata معتبر publication لازم‌اند. این کلید میزبان در وضعیت فعلی غایب است، ولی با صفر publication علت مستقل 503 فعلی نیست.
4. `application-tenants` فقط tenant فعال و عضویت فعال subject را فهرست می‌کند؛ offer/grant تاریخی را نمی‌خواند. پس از آماده‌شدن reader، subject کانونیِ ناموجود باید `200 []` بدهد. برای `products?subject=...&tenant=...`، عضویت فعال در tenant منتخب، offer فعال و grant معتبر همان مصرف‌کننده و metadata معتبر محصول لازم است. subscription تنها هنگام وجود `subscriptionId` در offer الزام می‌شود.
5. پذیرش اتصال باید با پاسخ‌های سالم reader، سازمان و محصول مجاز یک کاربر واقعی، رد دسترسی نامجاز و مسیر واقعی Gateway انجام شود. آزمون loopback حاضر این پذیرش را جایگزین نمی‌کند. `PlatformControlCenter:DiscoveryPath` در Portal فعلاً خالی است؛ اتصال کشف کاتالوگ پس از آماده‌شدن مرکز بررسی می‌شود.

این نوبت فقط تشخیص و ثبت پیش‌نیازها را انجام داد. پیکربندی، SSO، ثبت client، grant، publication، دادهٔ HR/مالی، Gateway و release عملیاتی تغییر نکردند. شمارش و اعتبار JSON صحت معنایی همهٔ داده‌ها را تضمین نمی‌کند؛ دو بانک نیز snapshot مشترک ندارند. آزمون مرورگر، کاربر واقعی و Gateway در دامنهٔ این probe نبود.

رسید و اسکریپت در پوشهٔ شواهد نگه داشته شدند؛ فایل موقت اجرایی همین بررسی از `.build` جمع شد. فایل‌های دیگر و صوت‌ها در این نوبت موضوع حذف نبودند.
