# گیت سازگاری HR3 در CI توسعه

بستهٔ فعال در `ci.py:HR3_PACKAGE` اکنون `hr-read-host-v3-r7` است. بسته‌های اولیه تا r6 و pinهای تاریخی دست‌نخورده‌اند. r7 ورودی‌های میزبان پس از ادغام مالی را با predecessor ثابت r6 ثبت می‌کند؛ کد C#/project/targets بسته با r6 یکسان است. محدودیت PIP بومی حفظ می‌شود: lease شاهد از `Pip.FreshUntil` عبور نمی‌کند و provenance چندمنبعی که در `AccessDecision.Pip` قابل نمایش کامل نیست، بسته رد می‌شود. وضعیت انتخاب محلی **reviewed-local-ready-for-ci** است؛ اجرای موفق GitLab برای این سورس هنوز ادعا نمی‌شود. نتیجهٔ تازهٔ actual-host و Linux filesystem، hash رسیدها و محدودیت audit محلی در [گزارش r7](HR3-R7-LOCAL-INTEGRATION.fa.md) ثبت شده‌اند؛ بخش r5 پایین صرفاً شاهد تاریخی است.

## ترتیب اجرای اجباری

Job به نام `hr3_validate_host` در مرحلهٔ `validate-host`، پیش از بسته‌بندی توسعه و روی شاخهٔ محافظت‌شدهٔ `develop`، فرمان زیر را اجرا می‌کند:

```sh
python3 deploy/fanasa-organization-hr/ci.py validate-hr3 \
  --source-commit "$CI_COMMIT_SHA" --output .artifacts/hr3-validation
```

گیت‌ها ترتیبی‌اند:

```sh
python3 deploy/fanasa-organization-hr/hr-read-host-v3-r7/verify-package.py --repository "$CI_PROJECT_DIR"
sh deploy/fanasa-organization-hr/hr-read-host-v3-r7/test-host.sh "$CI_PROJECT_DIR"
```

گیت اول، ۳۲ فایل بسته، predecessor ثابت r6، ۱۱۱ فایل دو dependency ثابت و تمام **۱۴۸ ورودی میزبان** را byte به byte مقایسه می‌کند. افزودن، حذف یا تغییر هر ورودی Web/UI/PCC در scope ثابت، اجرای build را پیش از شروع متوقف می‌کند؛ manifest خودکار refresh نمی‌شود. گیت دوم ۱۳ suite کامل را در artifact تازه و ایزوله اجرا می‌کند. هویت کامل سورس پیش و پس از آزمون باید یکسان بماند. نبود بسته یا شکست فرمان، job را ناموفق می‌کند. `tests/test_ci_hr3.py` انتخاب دقیق r7، pin ر6 و حضور تمام فایل‌های predecessor در هویت سورس را نیز کنترل می‌کند؛ fallback تاریخی ندارد.

تنظیمات محلی/محیطی از هویت سورس خارج‌اند؛ `appsettings.json` پایه داخل آن است. targets میزبان، تنظیمات محیطی را از خروجی build/publish نیز حذف می‌کند. مرحلهٔ `verify-package` پس از موفقیت `validate-host` آغاز می‌شود؛ مسیر عبور با `needs` یا `allow_failure` وجود ندارد. هر job با `GIT_STRATEGY: clone` checkout تازه دریافت می‌کند.

## شاهد محلی r5 و حدود آن

در ۲۳ اکتبر ۲۰۲۶، full actual-host روی Windows و SDK `10.0.401` با restore از feedهای تنظیم‌شده و **NuGet audit فعال** اجرا شد. نتیجه **۹۰۸ assertion موفق در ۱۳ suite** و build با صفر warning/error بود؛ ۶۱ assertion جدید مربوط به PIP است. آزمون‌ها PipStore و AccessPolicyStore واقعی، همان Fabric موقت، serialization و post-read واقعی را استفاده می‌کنند؛ درخواست خارجی PIP/Keycloak یا VPS ندارند.

inventory قبل/بعد شامل همان ۱۳۸ ورودی و یکسان بود. manifest اجراشده نیز پیش/پس ثابت ماند:

```text
tested manifest: 869c397defa681a90a7ad1c5040ebf3c75faa021057480ea9886925e935e53c1
actual-host-tests.log: 4f27fdfdf42ad7f95afe7e7654fb77046e6b92c30331f9c82ef619f5c9d606c4
host-inventory-before/after.json: afc7ffd5d4b8ac440dac8d31a411ca773ad8877849b0d6f2a3850ed5dc6e4f96
```

فایل‌ها در `.build/hr3-r5-pip-20261023-r2/` نگهداری می‌شوند. metadata و راهنمای نهایی پس از آن اجرا تغییر کرده‌اند؛ hash بالا هویت manifest **هنگام آزمون** است. ورودی‌های C#/build آزمون‌شده و ۱۳۸ ورودی میزبان ثابت مانده‌اند. رسید `hr3-r5-local-validation.json` این تمایز، فرمان‌ها و hash لاگ‌ها را ثبت می‌کند. آزمون filesystem بسته ۳۴ مورد دارد: ۳۱ موفق و ۳ skip برای symlink واقعی به علت مجوز Windows؛ guardهای شبیه‌سازی‌شده موفق‌اند.

هر دو مقدار `Enforce=false/true` در PIP با رفتار evaluator فعلی آزموده شده‌اند. source فعلی attribute را با `Enforce=false` نیز ارزیابی می‌کند؛ simulation-only بودن PIP برای HR3 ادعا نمی‌شود. Permit دارای تعهد audit انجام‌نشده و provenance چندمنبعی ناقص همچنان رد می‌شوند. r5، AccessPolicies اصلی را تغییر نمی‌دهد.

## آزمون Python و OCI

```powershell
python -B -m unittest discover -s deploy/fanasa-organization-hr/tests -p 'test_*.py' -v
python -B -m unittest discover -s deploy/fanasa-organization-hr/oci/tests -p 'test_*.py' -v
python -B -m unittest discover -s deploy/fanasa-organization-hr/hr-read-host-v3-r7/tests -p 'test_*.py' -v
```

آزمون CI ترتیب گیت‌ها، توقف پیش از build هنگام drift، source تغییرکرده حین آزمون، enumeration کامل بستهٔ فعال و ترتیب stageها را بررسی می‌کند. فرایند .NET در آزمون Python شبیه‌سازی می‌شود؛ نتیجهٔ ۹۰۸ از اجرای مستقل واقعی بالا است. نتایج اجرای نهایی Python/OCI و hash لاگ‌ها در رسید محلی ثبت می‌شوند.

## خروجی CI و مرز انتشار

Job فقط لاگ‌ها و `hr3-validation.json` را ۳۰ روز نگه می‌دارد. JSON منشأ CI، هویت سورس، hash manifest، نتیجه و hash لاگ موفق/شکست را ثبت می‌کند. `runtimeActivation=false` و `operationalAcceptance=false` باقی‌اند. artifact آزمون opt-in، DLL میزبان HR3 را به بسته‌بندی عادی منتقل نمی‌کند.

مانع تاریخی pipeline ۱۹، وابستگی گذرای `System.Security.Cryptography.Pkcs 9.0.4` بود؛ وب و نسخهٔ مرکزی محصول اکنون `10.0.13` را مستقیم pin می‌کنند و audit/warnings-as-errors روشن‌اند. حضور اسمبلی اصلاح‌شده و حذف تنظیمات محلی در publish همان commit هنوز باید در CI بررسی شود. شواهد تاریخی r3/r4 به عنوان موفقیت r5 کپی نمی‌شوند.

اجرای موفق GitLab تنها با receipt قابل انتساب به همان commit، پروژهٔ ثابت، job/pipeline و ref محافظت‌شده پذیرفته می‌شود. گراف تعریف‌شده دقیقاً دو stage دارد: `validate-host → verify-package`. job دوم فرمان‌های verify، package و verify_bundle را اجرا می‌کند؛ stage انتشار OCI در این فایل تعریف نشده است. این گراف باید روی checkout نهایی اجرا شود؛ تست محلی، اجرای trusted CI، انتشار OCI یا پذیرش عملیاتی نیست. برای پذیرش زنده، mTLS/IdP واقعی، publication از مرجع واقعی و مسیر کامل Gateway → PDP → admission → HR → Collector شاهد مستقل لازم دارند.

نتیجهٔ اجرای نهایی پس از pointer r5: deploy Python برابر ۵۸/۵۸، OCI برابر ۲۰/۲۰ و filesystem بسته برابر ۳۱ موفق از ۳۴ با ۳ skip symlink است. لاگ‌ها در .build/hr3-r5-ci-finalization-20261023-r1 و hash دقیق در receipt محلی‌اند. آزمون اضافهٔ Helm در این محیط به دلیل نبود PyYAML/Helm/OpenSSL و timeout fixture تکمیل نشد؛ برای Helm ادعای موفقیت یا پذیرش نداریم.
