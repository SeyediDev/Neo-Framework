# اتصال HR3 به میزبان واقعی Fanasa

این بسته، PDP و منبع دادهٔ HR3 را داخل همان اسمبلی `Fanasa.AccessManagement.Web` کامپایل می‌کند. `FabricDatabase`، ثبت مرکزی، سرویس دسترسی و policy بومی همان نمونه‌های موجود میزبان هستند. دیتابیس دوم، فروشگاه هویت، grant، قرارداد تجاری یا allocation جدیدی ساخته نمی‌شود.

## مسیر اجرا و هدف هر کنترل

`Gateway → TLS واقعی و گواهی client → دو JWT مستقل → introspection هر دو توکن در Keycloak → مصرف‌کنندهٔ مستقل و یکتا در ثبت دسترسی → snapshot تازهٔ Fabric → ارزیابی قرارداد/نقش/policy/allocation → serialization کامل → snapshot مستقل دوم → پاسخ بدون cache`

- توکن Gateway فقط هویت سرویس فراخوان را اثبات می‌کند؛ توکن اصلی HR با header جدا و scheme جدا بررسی می‌شود.
- introspection تنها از endpoint مشتق‌شده از issuer کانونی HTTPS انجام می‌شود. پاسخ subject باید `consumerTenantId` رشته‌ای، UUID کانونی و غیرصفر داشته باشد؛ مصرف‌کننده از route، تنظیمات یا claim توکن انتخاب نمی‌شود.
- adapter مرکزی همهٔ grantهای مستقیم `organization.read` و عضویت‌های فعال subject را می‌خواند. فقط یک مصرف‌کنندهٔ واجد شرایط مجاز است. اگر دو مصرف‌کننده وجود داشته باشد، درخواست به جای انتخاب اولین مورد رد می‌شود.
- هر دو client باید در محصول مرکزی جاری ثبت و فعال باشند و scopeهای لازم را داشته باشند. سپس نقش `HrReader`، مجوز بومی، assignment کامل، قرارداد، policy، allocation و veto بومی جداگانه بررسی می‌شوند.
- شاهد introspection برای همان درخواست و همان دو توکن حداکثر پنج ثانیه اعتبار دارد. تغییر محلی مجوز و revision در هر خواندن دوباره بررسی می‌شود. لغو در IdP ممکن است تا پایان این پنجره مشاهده نشود؛ دریافت آنی revocation پیاده نشده است.
- نتیجهٔ Permit همچنان دارای تعهدهای انجام‌نشده است؛ `admissionConfirmed=false` مجوز مصرف سهمیه یا تحویل دادهٔ HR نیست.

## مثال رفتاری قابل بررسی

در fixture محلی، یک subject دارای عضویت، grant مستقیم و نقش HR معتبر است و توکن introspection همان tenant مستقل را برمی‌گرداند: PDP پاسخ Permit با تعهدهای باقی‌مانده می‌دهد. غیرفعال‌کردن client، لغو grant، تغییر revision یا consumer متفاوت در introspection باعث رد می‌شود. افزودن grant معتبر برای tenant دوم نیز باعث رد ابهام می‌شود؛ route tenant اول راه فرار ایجاد نمی‌کند. این داده‌ها مصنوعی‌اند و پذیرش عملیاتی را ثابت نمی‌کنند.

## اتصال در build

بسته باید کنار `hr-read-profile-v3` و `hr-read-pdp-v3` در `deploy/fanasa-organization-hr` قرار بگیرد. کلید `EnableHr3Host=true` به همراه `CustomAfterMicrosoftCommonTargets=<absolute package>/integration/Hr3Host.targets` اتصال را فعال می‌کند. targets فقط پروژهٔ واقعی میزبان را تغییر می‌دهد و core قدیمی را با core ثابت HR3 جایگزین می‌کند؛ فایل `FabricDatabase.cs` بستهٔ candidate هرگز لینک نمی‌شود.

دو کپی سازگاری محدود وجود دارد: Program فقط دو فراخوان ثبت اختیاری می‌گیرد؛ `ContractPolicyStore.Find` همهٔ رکوردها را اعتبارسنجی و فقط profile دقیق v2 را انتخاب می‌کند. خواندن/نوشتن legacy همچنان v2 است. فایل‌های مشترک اصلی تغییر نمی‌کنند. build با تغییر hash مبنای Program یا Store متوقف می‌شود تا نسخهٔ جدید بازبینی شود.

```powershell
./test-host.ps1 -RepositoryRoot E:/SJVS/Projects/Neo -CachedRestore
```

در runner لینوکسی موجود می‌توان `sh deploy/fanasa-organization-hr/hr-read-host-v3/test-host.sh "$CI_PROJECT_DIR"` را پیش از بسته‌بندی اجرا کرد. این دستور اعتبارسنجی است و endpoint را deploy نمی‌کند. برای انتقال بسته توسط `ci.py` فعلی، پسوند `.ps1` نیز باید به allowlist فایل سورس افزوده شود؛ تا قبل از آن، تحویل این بسته با فیلتر فعلی کامل نیست. pipeline فعلی با این بسته خودکار تغییر داده نشده است.

`CachedRestore` فقط برای اجرای محلی با cache نصب‌شده است و NuGet audit را غیرفعال می‌کند؛ از نتیجهٔ آن ادعای بررسی آسیب‌پذیری بسته‌ها نکنید. در CI با دسترسی به feed، این سوییچ را حذف کنید. آزمون‌ها روی SQLite موقت و handler محلی اجرا می‌شوند و به VPS یا Keycloak زنده درخواست نمی‌فرستند.

## فعال‌سازی runtime

پیش‌فرض `appsettings.hr3.disabled.json` خاموش است. ساخت فعال به تنهایی endpoint را ثبت نمی‌کند. برای PDP باید `Fanasa:Hr3:Pdp:Enabled=true` و سه بخش `Gateway`، `Receiver` و `Introspection` کامل و سازگار شوند. شکل Gateway و Receiver همان قراردادهای ثابت بسته‌های قبلی است؛ شناسهٔ محصول، client، subject، tenant، policy و allocation باید از رکوردهای جاری واقعی گرفته شوند. نمونهٔ مصنوعی را روی دیتابیس اصلی publish نکنید.

credential مربوط به introspection باید از provider تنظیمات runtime/secret با نام‌های `Fanasa__Hr3__Pdp__IntrospectionClientId` و `Fanasa__Hr3__Pdp__IntrospectionClientSecret` تأمین شود. آنها را در فایل tracked، command line یا log ننویسید. client آن باید مجوز introspection لازم در realm مرکزی را داشته باشد؛ نام client به خودی خود دسترسی ایجاد نمی‌کند. mapper سمت IdP باید consumer مستقل را در پاسخ introspection subject برگرداند. نبود credential/mapper/اختیار مرکزی باعث رد می‌شود.

listener HTTPS موجود باید گواهی client واقعی بگیرد؛ pin و chain معتبر هر دو لازم‌اند. handler introspection redirect و cookie ندارد، TLS1.2/1.3 و trust store سیستم و revocation آنلاین را استفاده می‌کند. این بسته trust store یا تنظیم listener را تغییر نمی‌دهد. اعتماد CA باید از مسیر عملیاتی مصوب تأمین شود.

## مرز اتصال فعلی

این اتصال فقط `POST /internal/contract-policy/hr-read/v3/evaluate` را ثبت می‌کند. `MapHrReadPdp` مستقل را هم‌زمان ثبت نکنید. مسیرهای chart/history قدیمی و commandها دست‌نخورده‌اند. receiver HR3 هنوز به این مسیرهای قدیمی جایگزین نشده است؛ قبل از فعال‌سازی آن باید دو read action قدیمی صریحاً حذف/جایگزین شوند تا constrained route قبلی بر HR3 تقدم نگیرد. scheme موجود `OrganizationHrBearer` را دوباره ثبت نکنید.

`Hr3PublicationStore` یک API کتابخانه‌ای برای publication اتمیک CAS روی همان Fabric است؛ HTTP admin route ندارد. منبع اختیار publication به طور پیش‌فرض unavailable است و باید revision واقعی منبع کسب‌وکار را مستقل از payload/دسترسی ادمین فراهم کند. این بسته رکورد تجاری، اعتبار، سهمیه یا publication زنده تولید نمی‌کند.

پنج ثانیه بودجهٔ cancellation/deadline است؛ عملیات همگام SQLite را سخت‌افزاری قطع نمی‌کند. Fabric فعلی رشتهٔ JSON را پیش از اعمال سقف پاسخ می‌سازد، پس سقف پاسخ یک محدودیت کامل حافظه نیست. ثبت مرکزی دیتابیس جدا دارد و دوبار نمونه‌برداری می‌شود؛ تراکنش توزیع‌شده ادعا نمی‌شود.

## انتشار و پذیرش

بستهٔ سورس محلی و آزمون موفق، معادل اجرای زنده نیست. release قبلی سرور محفوظ می‌ماند. برای پذیرش زنده، علاوه بر مجوز انتقال سورس، باید build و pipeline جدید، تنظیم واقعی mTLS و IdP، publication از مرجع واقعی و مسیر کامل Gateway → PDP → admission → HR → Collector با شاهد مستقل بررسی شوند. گزارش آزمون و hashها داخل بسته نگهداری می‌شوند؛ وضعیت دقیق تحویل خارج از manifest ثابت ثبت می‌شود.
