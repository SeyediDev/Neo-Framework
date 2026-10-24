# اتصال HR3 به میزبان واقعی Fanasa

این نسخهٔ مستقل `r4` یک بستهٔ سورس با موجودی ثابت است. علت بازبینی، ارتقای dependency میزبان `System.Security.Cryptography.Pkcs` به نسخهٔ `10.0.13` پس از شکست NuGet audit در CI است؛ audit فعال می‌ماند. انتشار نیز باید وجود اسمبلی اصلاح‌شدهٔ همین نسخه را در artifact ثابت کند؛ نتیجهٔ restore به‌تنهایی شاهد نسخهٔ بارگذاری‌شده در runtime نیست. هویت سورس در `SOURCE-PACKAGE.json` ثبت می‌شود. کد runtime و تمام آزمون‌های C# همان سورس r3 هستند، اما نتیجهٔ موفق گذشته برای این dependency جدید اعتبار ندارد. اجرای تازهٔ کامل شامل ۸۴۷ assertion باید در CI انجام و رسید آن بیرون از بسته ثبت شود. `verification.log` و `test-evidence.json` قبلی در این بسته کپی نشده‌اند؛ manifest سورس شاهد موفقیت build، audit یا اجرای عملیاتی نیست. بسته‌های اولیه و شواهد تاریخی آنها ثابت می‌مانند.

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

در runner لینوکسی دستور `sh deploy/fanasa-organization-hr/hr-read-host-v3-r4/test-host.sh "$CI_PROJECT_DIR"` آزمون میزبان را اجرا می‌کند. گیت محلی تعریف‌شده در `../HR3-CI-VALIDATION.fa.md` ابتدا تطبیق manifest را بررسی می‌کند و سپس این آزمون را در job مستقل پیش از بسته‌بندی اجرا می‌کند. پسوند `.ps1` نیز در فهرست فایل‌های سورس قرار دارد. این تغییر pipeline تا زمان انتقال سورس و اجرای GitLab، شاهد اجرای CI یا deploy محسوب نمی‌شود.

هر اجرای آزمون یک پوشهٔ تازه زیر `.build/hr3-host-r4-artifacts` می‌سازد. restore و build هر دو همان `--artifacts-path` را دارند، به‌صورت سری اجرا می‌شوند و build server مشترک ندارند. پوشهٔ موجود پذیرفته نمی‌شود؛ خروجی در Web یا بسته‌های ثابت نوشته نمی‌شود. اسکریپت دقیقاً یک DLL تازه در `bin/Fanasa.HrReadHost.Tests/<pivot>` پیدا می‌کند و تمام suiteها را بدون فیلتر اجرا می‌کند. مسیر artifact و DLL با `HR3_ARTIFACTS_PATH` و `HR3_TEST_DLL` برای اتصال شواهد چاپ می‌شوند.

verifier موجودی خود بسته و هر dependency را بسته نگه می‌دارد: فایل افزوده، حذف، hash متفاوت، مسیر غیرکانونی، symlink یا junction پذیرفته نمی‌شود. فقط `bin`، `obj`، `__pycache__` و manifest ریشه از موجودی سورس خود بسته کنار می‌روند. هویت دو dependency به manifestهای اصلی profile/PDP ثابت است. موجودی میزبان از scope ثابت verifier تولید می‌شود و manifest حق محدودکردن آن را ندارد: C#/Razor/resource/project/build metadata در Web، فایل‌های linked مستقیم PCC، metadata اجداد، تمام assetهای `wwwroot` و فقط تنظیم رسمی پایهٔ `appsettings.json`. فایل‌های محلی/محیطی `appsettings.*.json` خوانده یا hash نمی‌شوند و HR3 targets آنها را از خروجی build و publish حذف می‌کند؛ تنظیم محیط استقرار از environment موجود می‌آید. فقط مسیر و SHA-256 ثبت می‌شود؛ فایل credential محلی، `.users` یا secret کپی نمی‌شود.

دستور `python -B verify-package.py --repository <repository> --print-host-inventory` فقط JSON موجودی فعلی را با نشان `unsealed-host-inventory-candidate` چاپ می‌کند؛ manifest یا pin ایجاد نمی‌کند و نتیجهٔ پذیرش نیست. حالت عادی وجود manifest، مطابقت تمام byteها و کامل‌بودن موجودی را الزام می‌کند.

`CachedRestore` فقط برای اجرای محلی با cache نصب‌شده است و NuGet audit را غیرفعال می‌کند؛ از نتیجهٔ آن ادعای بررسی آسیب‌پذیری بسته‌ها نکنید. در CI با دسترسی به feed، این سوییچ را حذف کنید. آزمون‌ها روی SQLite موقت و handler محلی اجرا می‌شوند و به VPS یا Keycloak زنده درخواست نمی‌فرستند.

## فعال‌سازی runtime

پیش‌فرض `appsettings.hr3.disabled.json` خاموش است. ساخت فعال به تنهایی endpoint را ثبت نمی‌کند. برای PDP باید `Fanasa:Hr3:Pdp:Enabled=true` و سه بخش `Gateway`، `Receiver` و `Introspection` کامل و سازگار شوند. شکل Gateway و Receiver همان قراردادهای ثابت بسته‌های قبلی است؛ شناسهٔ محصول، client، subject، tenant، policy و allocation باید از رکوردهای جاری واقعی گرفته شوند. نمونهٔ مصنوعی را روی دیتابیس اصلی publish نکنید.

credential مربوط به introspection باید از provider تنظیمات runtime/secret با نام‌های `Fanasa__Hr3__Pdp__IntrospectionClientId` و `Fanasa__Hr3__Pdp__IntrospectionClientSecret` تأمین شود. آنها را در فایل tracked، command line یا log ننویسید. client آن باید مجوز introspection لازم در realm مرکزی را داشته باشد؛ نام client به خودی خود دسترسی ایجاد نمی‌کند. mapper سمت IdP باید consumer مستقل را در پاسخ introspection subject برگرداند. نبود credential/mapper/اختیار مرکزی باعث رد می‌شود.

listener HTTPS موجود باید گواهی client واقعی بگیرد؛ pin و chain معتبر هر دو لازم‌اند. handler introspection redirect و cookie ندارد، TLS1.2/1.3 و trust store سیستم و revocation آنلاین را استفاده می‌کند. این بسته trust store یا تنظیم listener را تغییر نمی‌دهد. اعتماد CA باید از مسیر عملیاتی مصوب تأمین شود.

## مرز اتصال فعلی

تقویم‌های جدید به‌صورت پیش‌فرض اثری بر HR3 ندارند. فقط در tenant صریحاً فعال‌شده در `Organization:TemporalAccess`، قواعد تقویمی منتشرشده با `Enforce=true` وارد ارزیابی HR3 می‌شوند؛ قواعد غیرتقویمی قدیمی با همان رفتار قبلی باقی می‌مانند. قواعد غیرفعال یا صرفاً شبیه‌سازی، شاهد مجوز یا مهلت اعتبار آن را تغییر نمی‌دهند. adapter HR3 فقط‌خواندنی است و نمی‌تواند تعهد حسابرسی بومی را انجام دهد؛ بنابراین حتی در ساعت باز، نتیجهٔ Permit دارای `audit` به شاهد مجاز HR3 تبدیل نمی‌شود. این محدودیت عمداً بسته می‌ماند. مسیرهای Organization و HR قدیمی حسابرسی را در کنترل زمانی خود انجام می‌دهند. suite مستقل `calendar coexistence` این مرز و نبود تغییر دیتابیس را بررسی می‌کند.

این اتصال فقط `POST /internal/contract-policy/hr-read/v3/evaluate` را ثبت می‌کند. `MapHrReadPdp` مستقل را هم‌زمان ثبت نکنید. مسیرهای chart/history قدیمی و commandها دست‌نخورده‌اند. receiver HR3 هنوز به این مسیرهای قدیمی جایگزین نشده است؛ قبل از فعال‌سازی آن باید دو read action قدیمی صریحاً حذف/جایگزین شوند تا constrained route قبلی بر HR3 تقدم نگیرد. scheme موجود `OrganizationHrBearer` را دوباره ثبت نکنید.

`Hr3PublicationStore` یک API کتابخانه‌ای برای publication اتمیک CAS روی همان Fabric است؛ HTTP admin route ندارد. منبع اختیار publication به طور پیش‌فرض unavailable است و باید revision واقعی منبع کسب‌وکار را مستقل از payload/دسترسی ادمین فراهم کند. این بسته رکورد تجاری، اعتبار، سهمیه یا publication زنده تولید نمی‌کند.

پنج ثانیه بودجهٔ cancellation/deadline است؛ عملیات همگام SQLite را سخت‌افزاری قطع نمی‌کند. Fabric فعلی رشتهٔ JSON را پیش از اعمال سقف پاسخ می‌سازد، پس سقف پاسخ یک محدودیت کامل حافظه نیست. ثبت مرکزی دیتابیس جدا دارد و دوبار نمونه‌برداری می‌شود؛ تراکنش توزیع‌شده ادعا نمی‌شود.

## انتشار و پذیرش

این بسته فقط هویت سورس را ثابت می‌کند و هیچ نتیجهٔ موفق جدیدی برای build، audit یا آزمون اعلام نمی‌کند. release قبلی سرور محفوظ می‌ماند. برای پذیرش، CI باید build و audit فعال و اجرای کامل همان ۸۴۷ assertion را با رسید hash-bound تازه ثبت کند. برای پذیرش زنده، تنظیم واقعی mTLS و IdP، publication از مرجع واقعی و مسیر کامل Gateway → PDP → admission → HR → Collector نیز به شاهد مستقل نیاز دارند. شواهد اجرای آینده و وضعیت تحویل بیرون از این manifest ثابت ثبت می‌شوند؛ شواهد تاریخی r3 فقط در بستهٔ ثابت خود باقی می‌مانند.
