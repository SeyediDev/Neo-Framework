# مسیر توسعه API منابع انسانی فن‌آسا

## ساخت ایزوله و پذیرش محلی

گیت مستقل میزبان در `ci.py` به `hr-read-host-v3-r8` متصل است؛ نسخه‌های قبلی و شواهدشان حفظ شده‌اند. این گیت سازگاری PIP بومی و محدودیت تازگی شاهد را می‌آزماید؛ اصلاح PKCS نسخهٔ r4 با reference مستقیم `System.Security.Cryptography.Pkcs 10.0.13` حفظ می‌شود. audit در CI فعال است و پذیرش اجرای تازه از رسید همان job خوانده می‌شود. نتیجهٔ محلی ۹۰۸ assertion، اجرای موفق GitLab برای commit تازه محسوب نمی‌شود. شرح و مرز پذیرش در [گیت HR3](HR3-CI-VALIDATION.fa.md) آمده است.

بستهٔ انتخاب‌شدهٔ r8، رد UUID تمام‌صفر را برای دو GET مصرف و حساب از commit `2fcb183` در همان موجودی ۱۴۸ ورودی میزبان ثبت می‌کند؛ فقط دو hash میزبان تغییر کرده‌اند و مهر r7 ثابت است. کد C#/project/targets بسته با r7 یکسان است. رسید build/actual-host باید به manifest همین r8 با SHA256 `a6e22c79fc1e8c99c0606a24bb89fe4ca4be3e8e7b4f18cd7c31537076440ba4` متصل باشد؛ موفقیت نسخهٔ قبلی به این نسخه منتقل نمی‌شود. انتخاب محلی، اجرای trusted CI یا پذیرش عملیاتی نیست. رسیدهای همین نسخه را از [گزارش ادغام محلی r8](HR3-R8-LOCAL-INTEGRATION.fa.md) بخوانید.

شاهد تاریخی r7: بستهٔ مستقل r7، delta مالیِ بررسی‌شده شامل ۱۳ فایل تغییریافته و ۶ افزوده را در موجودی بستهٔ ۱۴۸ ورودی میزبان ثبت می‌کند؛ مهر r6 و فایل‌های ثابت آن تغییر نکرده‌اند. اجرای تازهٔ محلی actual-host برابر ۱۳ suite و ۹۰۸ assertion، و اجرای واقعی Linux filesystem برابر ۳۸ آزمون با صفر failure/error/skip است. اجرای محلی .NET از cache آفلاین و با audit غیرفعال استفاده کرده است؛ پذیرش تازهٔ CI با audit فعال همچنان لازم است. رسیدها و مرز شواهد مالی در [گزارش ادغام محلی r7](HR3-R7-LOCAL-INTEGRATION.fa.md) آمده‌اند. هیچ نتیجهٔ محلی به رسید trusted CI یا پذیرش عملیاتی تبدیل نشده است.

`ci.py verify` فقط فایل‌های ثبت‌شده در هویت سورس را با بررسی SHA256 در پوشهٔ تازهٔ `<output>/build-source` کپی می‌کند. restore، build، آزمون‌ها و publish از همین کپی اجرا می‌شوند؛ مسیرهای نسبی موتور و workbench حفظ می‌شوند و `bin/obj` مشترک workspace استفاده نمی‌شود. تنظیمات محیطی/محلی برنامه، دادهٔ runtime و credentialها وارد این کپی نمی‌شوند؛ تنظیم پایه و مجوز فونت `OFL.txt` در هویت سورس باقی می‌مانند. پیش و پس از آزمون و بسته‌بندی، تغییر بایت، حذف، فایل افزودهٔ خارج از خروجی‌های build و لینک در کپی باعث توقف می‌شود.

هر suite دات‌نت با `dotnet <assembly.dll>` از خروجی Release همین کپی اجرا می‌شود؛ این مسیر دقیقاً اسمبلی ساخته‌شده را اجرا می‌کند و به apphost سیستم‌عامل یا launch profile وابسته نیست.

بسته‌بندی به رسید موفق همین سورس، همهٔ gateهای لازم با نام یکتا و لاگ‌های دارای هش معتبر نیاز دارد. native/UI باید صفر failure و skip داشته باشند. تولید نتیجه، پذیرش رسید برای بسته‌بندی و verifier مستقل همگی برای UI سازمان، تقویم و PIP حداقل ۴۴ آزمون موفق با شمارندهٔ صحیح می‌خواهند؛ رسیدهای قدیمی با کمتر از ۴۴ موفق برای انتشار تازه کافی نیستند. حداقل native Workbench همچنان ۱۵ است. پوشهٔ build قبلی پذیرفته نمی‌شود. فیلد `buildIsolation=verified-source-copy` مرز این رسید را ثبت می‌کند. اجرای محلی مجاز است:

```powershell
python -B deploy/fanasa-organization-hr/ci.py verify --allow-working-tree --cached-restore --output .build/hr-release-local
python -B deploy/fanasa-organization-hr/ci.py package --allow-working-tree --output .build/hr-release-local
```

گزینهٔ `--cached-restore` فقط برای working-tree محلی است؛ NuGet audit را خاموش می‌کند و در GitLab CI پذیرفته نمی‌شود. نتیجهٔ محلی با `local-operator` ثبت می‌شود و کنترل‌کنندهٔ CD آن را به‌جای job موفق GitLab قبول نمی‌کند. ساخت archive محلی، مجوز انتقال سورس یا انتشار نیست. آماده‌بودن Linux native SQLite داخل archive نیز جای اجرای برنامه در محیط Linux/Gateway واقعی را نمی‌گیرد.

مبدأ انتخاب‌شدهٔ کاربر مخزن خصوصی داخلی `https://gitlab.fanasa.net.local/javadsayedi/organization-hr-api`، پروژهٔ۲ و برنچ محافظت‌شدهٔ `develop` است. مسیر CI پروژه `.gitlab-ci.hr-development.yml` و موتور آن **GitLab CI** است. نتیجهٔ اجرای زنده با URL و شمارهٔ pipeline و commit دقیق ثبت می‌شود؛ وجود این فایل‌ها به معنی اجرای CI یا فعال‌شدن API نیست. `Jenkinsfile.hr-development` فقط مسیر قدیمی مبدأ خارجی را نگه می‌دارد و در پروژهٔ داخلی اجرا نمی‌شود.

Job ثابت `hr_verify_package` روی runner اختصاصی همین پروژه با tag `fanasa-hr-development` اجرا می‌شود. runner فقط برنچ محافظت‌شده را می‌پذیرد؛ اجرای untagged/shared runner و اجرای source از merge request یا fork به مسیر استقرار راه ندارد. تصویر محلی `fanasa/hr-build:10-node24` شامل SDK دات‌نت۱۰، Node۲۴، Git و Python۳ از base image رسمی با digest ثبت‌شده است. Job container privileged نیست، هیچ host socket، مسیر credential یا دادهٔ runtime در آن mount نمی‌شود و credential استقرار/SSO/cluster/signing ندارد. سقف runner یک job، یک CPU،۷۶۸MiB حافظه و۱۵۳۶MiB مجموع حافظه و swap است؛ سقف job۶۰دقیقه است. commit دقیق و fingerprint تمام ورودی‌های ساخت و CI در evidence ثبت می‌شوند.

سیزده suite واقعی `Organization.Hr.Tests`، `Organization.Bearer.Tests`، `Organization.Tests`، `Organization.Calendar.Tests`، `Organization.Temporal.Tests`، `ConsumerApplicationCatalog.Tests`، `PublishedCatalog.Tests`، `ConsumerUsage.Api.Tests`، `PaygMetering.Tests`، `BillingLifecycle.Tests`، `Billing.Api.Tests`، `Workbench.Boundary.Tests` و `ProductRegistration.Tests` به ترتیب restore/build/run می‌شوند. هر سه suite مالی جدید باید رسید اجرای واقعی و غیرخالی داشته باشند؛ نبود هرکدام در پذیرش شواهد و verifier آرشیو رد می‌شود. سپس موتور، workbench و probe دیتابیس ساخته می‌شوند؛ آزمون‌های بومی workbench و آزمون UI تقویم/سیاست اجرا می‌شوند. خروج غیرصفر، suite خالی، تست native/UI ردشده یا skipشده، نبود ماژول HR یا تغییر source بین تست و بسته‌بندی جلوی انتشار را می‌گیرد. `allow_failure` یا تست ساختگی جایگزین نداریم. لاگ و SHA256 هر گیت در `verification.json` ثبت می‌شود؛ این تست‌ها با هویت/کلید/دادهٔ مصنوعی مستقل اجرا می‌شوند و credential اجرای واقعی را مصرف نمی‌کنند.

```sh
python3 deploy/fanasa-organization-hr/ci.py verify --source-commit "$(git rev-parse HEAD)"
python3 deploy/fanasa-organization-hr/ci.py package --source-commit "$(git rev-parse HEAD)"
python3 deploy/fanasa-organization-hr/verify_bundle.py \
  --archive .artifacts/hr-development/hr-development-*.tar.gz \
  --receipt .artifacts/hr-development/release.json
```

Checkout تمیز پیش‌فرض است. برای نخستین نسخهٔ محلیِ هنوز commitنشده، فقط اپراتور می‌تواند `--allow-working-tree` را صریحاً به هر دو دستور بدهد؛ evidence در این حالت `working-tree-snapshot` و `gitBaseCommit` را ثبت می‌کند و ادعای ساخته‌شدن از commit تمیز ندارد. همان SHA256 snapshot باید در پایان تست و publish ثابت بماند. Pipeline معمول SCM این گزینه را استفاده نمی‌کند.

بستهٔ ثابت tar.gz شامل Web، workbench، UI محافظت‌شده، ده schema، SQLite بومی Linux، CI evidence و لاگ‌ها است. Web و workbench باید DLL دقیقاً یکسان موتور سیاست را حمل کنند. shell آزمایشگاه فقط `web/Workbench/index.html` است و فایل عمومی `wwwroot/workbench/index.html` رد می‌شود. secret، certificate private key، دادهٔ runtime و DB بسته‌بندی نمی‌شوند؛ تنظیم واقعی SSO/HR/Gateway در فایل محدود میزبان باقی می‌ماند. manifest کل فایل‌ها، اندازه/SHA256، source snapshot، environment و mode استقرار را ثبت می‌کند؛ بازنویسی artifact قبلی ممنوع است.

`verify_bundle.py` قبل از هر فعال‌سازی، hash آرشیوِ دریافت‌شده از منبع معتبر، hash تک‌تک فایل‌ها، تمام گیت‌های لازم و نسخهٔ manifest را کنترل می‌کند. مسیر خروجی، symlink/hardlink، فایل ویژه، mode دارای privilege، نام تکراری و فایل بدون manifest رد می‌شوند. تعداد/اندازهٔ TAR هنگام خواندن هر header محدود می‌شود و JSON evidence سقف۴MiB دارد. گزینهٔ `--extract-to` فقط مقصد تازه را می‌پذیرد و فایل‌ها را بعد از بررسی دستی کپی می‌کند؛ metadata و استخراج خام tar اعتماد نمی‌شوند. hash به‌تنهایی امضا یا اثبات هویت CI نیست؛ کنترل‌کنندهٔ trusted artifact را از job ثبت‌شدهٔ همین پروژه با API احرازهویت‌شدهٔ GitLab دریافت می‌کند.

استقرار خودکار با timer مستقل `fanasa-organization-hr-gitlab-cd.timer` و نسخهٔ root-owned `gitlab-cd.py` انجام می‌شود. کنترل‌کننده بدون caller parameter فقط پروژهٔ۲، origin ثابت، برنچ محافظت‌شدهٔ develop، commit فعلی و job موفق `hr_verify_package` از runner ثبت‌شده را می‌پذیرد. project token تازه با scope فقط `read_api` و نقش Reporter در فایل root-only `/etc/fanasa-organization-hr/gitlab-cd.token` خارج از CI قرار می‌گیرد. Artifactهای تک‌فایل از API همان job در `/var/lib/fanasa-organization-hr/artifacts/<job-id>/` به‌صورت atomic دریافت می‌شوند؛ SHA commit و pipeline/job/project و evidence داخل bundle باید دقیقاً یکسان باشند. پس از دانلود/verify، HEAD محافظت‌شده بلافاصله پیش از شروع activation دوباره خوانده می‌شود؛ اگر در حین استقرار commit جدید برسد، گزارش `newerCommitPending` و poll بعدی آن را دنبال می‌کنند. verifier در `/usr/local/lib/fanasa-organization-hr/verify_bundle.py` نصب می‌شود. interface helper محدود میزبان:

```text
/usr/local/sbin/fanasa-organization-hr-deploy development <release> <archive-path> <sha256>
```

helper باید root-owned باشد؛ فقط محیط development و سرویس‌های `fanasa-access-management@development.service` و `fanasa-policy-workbench.service`، مقصد release تازه، قفل انتشار، verify مجدد، backup سازگار SQLite، حفظ state و برگشت نرم‌افزار هنگام شکست readiness را می‌پذیرد. هیچ source job دسترسی sudo/SSH/root یا فرمان آزاد این helper ندارد. گزارش حقیقی CD در کنار artifact و metadata صفحهٔ محافظت‌شده با `lastSuccessfulRun` و `pipelineVerifiedAt` فقط پس از موفقیت activation ثبت می‌شود. `trusted-deploy.sh` قرارداد adapter قدیمی Jenkins است و timer داخلی از آن استفاده نمی‌کند.

CD این slice برای میزبان systemd موجود است؛ manifest صریحاً `bounded-systemd-development` و `gitOpsPromotion=adapter-pending` دارد. انتقال artifact ثابت به مسیر Argo/Helm، OCI registry/SBOM/signing و trusted CI evidence/approval مرکزی هنوز adapter مستقل می‌خواهند. فایل حاضر این مسیر را GitOps کامل معرفی نمی‌کند. scannerهای secrets/SAST/SCA/image/DAST نیز اجرا نشده‌اند و `not-configured` ثبت می‌شوند؛ نتیجهٔ تست integration جای گزارش scanner نیست. محیط استیج/عملیاتی از این pipeline فعال نمی‌شود.

API باید از Gateway مصوب در host اصلی عرضه شود؛ upstream HR، HTTPS اختصاصی و گواهی واقعی peer Gateway را به همراه service JWT ثبت‌شده و مجوز tenant/scope بررسی می‌کند. قرارداد سه endpoint در [OpenAPI منابع انسانی](../../docs/fanasa-platform-control-center/pilot/hr-api.openapi.yaml) موجود است. انتشار route یا ارائهٔ لینک باید پس از آزمون مثبت و رد دسترسی مستقیم/توکن نامعتبر ثبت شود؛ تعریف pipeline به‌تنهایی route زنده نمی‌سازد.

تغییر در این بسته، schema یا recipe موجود Skills/MCP را عوض نمی‌کند؛ راهنمای همین pipeline و قرارداد نصب در این README نگهداری می‌شود.

توکن اختصاصی read_api کنترل‌کنندهٔ CD عمر۳۰روز دارد؛ شناسه و تاریخ انقضا در گزارش نصب root-controlled ثبت می‌شود. پیش از انقضا، اپراتور باید همان توکن محدود همین پروژه را rotate کند و فایل root-only را به‌صورت atomic جایگزین کند. توکن و کلید runner در repository، CI variable یا log قرار نمی‌گیرند؛ انقضای credential اجرای CD را fail-closed متوقف می‌کند و وضعیت آخرین نسخهٔ موفق را تغییر نمی‌دهد.

## آداپتور آماده‌سازی OCI و Helm

مسیر [OCI](oci/README.md) بستهٔ موفق همین CI را دوباره verify می‌کند و بدون بازسازی source، image وب و workbench را می‌سازد. receipt مستقل، commit و pipeline/job اصلی، SHA256 آرشیو، digest پایهٔ رسمی، recipe و شناسهٔ واقعی image محلی را ثبت می‌کند. شناسهٔ image محلی، digest manifest رجیستری نیست؛ push و دریافت digest رجیستری مرحلهٔ جداست. این آداپتور credential استقرار، runtime یا Keycloak مصرف نمی‌کند.

قالب [Helm نامزد توسعه](helm/organization-hr/README.md) دو کانتینر در یک Pod، یک replica با Recreate، PVC از قبل آماده و Secretهای موجود را می‌خواهد. تنها Service آن پورت TLS منابع انسانی 5193 است؛ پورت وب 5191 و workbench 4187 خصوصی باقی می‌مانند. رابط انسانی و SSO راهنمای فعلی از میزبان systemd عرضه می‌شوند؛ انتقال آن‌ها به ingress جزو این قالب نیست.

کنترل [runtime_config.py](runtime_config.py) پیش از شروع هر دو برنامه، تنظیمات محدود هویت، TLS و trust bundle، سه دیتابیس موجود و کلیدهای حفظ‌شدهٔ Data Protection را بررسی می‌کند. دیتابیس مفقود یا خراب باعث توقف است؛ این قالب داده، membership یا grant جدید ایجاد نمی‌کند. پس از شروع، readiness وب علاوه بر دیتابیس، listener واقعی TLS و گواهی upstream را با نام و CA صحیح بررسی می‌کند؛ probe بومی workbench صحت ذخیره‌سازی و تعریف‌ها را کنترل می‌کند. آماده بودن ذخیره‌سازی با مجاز بودن یک سیاست خاص یکسان نیست.

قالب فعلیِ عمومی `fanasa-application` و admission پروژهٔ `developer-internal` این topology و منابع را نمی‌پذیرند. نصب Argo به پروژه و مرز اختصاصی development، imageهای منتشرشده با digest، PVC مهاجرت‌داده‌شده، Secretهای محدود، آزمون server dry-run و کنترل Gateway نیاز دارد. فایل‌ها و تست‌های adapter هیچ ادعایی دربارهٔ `Synced/Healthy`، نصب کلاستر یا approval مرکزی ندارند. receipt و manifest موفق systemd بازنویسی نمی‌شوند.

قراردادهای پایه با مستندات رسمی [NetworkPolicy در Kubernetes](https://kubernetes.io/docs/concepts/services-networking/network-policies/)، [schema مقادیر Helm](https://helm.sh/docs/topics/charts/) و [تصاویر دات‌نت](https://learn.microsoft.com/en-us/dotnet/core/docker/container-images) بررسی شده‌اند. اجرای NetworkPolicy به CNI پشتیبان آن نیاز دارد و باید در کلاستر مقصد آزمون شود.

پیش‌بررسی کلیدها به قالب استاندارد فعلی Linux با AES_256_CBC/HMACSHA256 محدود است؛ تاریخ، شناسه، descriptor و مادهٔ کلید را بدون چاپ محتوا بررسی می‌کند. این کنترل جای آزمون واقعی decrypt/unprotect، revocation و SSO انسانی را نمی‌گیرد. keyring رمزگذاری‌شده یا الگوریتم دیگر به آداپتور و آزمون اختصاصی مهاجرت نیاز دارد. برای SQLite در WAL، init از snapshot خاموش و سازگار، به‌همراه sidecarها، نسخهٔ محدود موقت می‌سازد؛ دادهٔ اصلی read-only می‌ماند و immutable فرض نمی‌شود.
