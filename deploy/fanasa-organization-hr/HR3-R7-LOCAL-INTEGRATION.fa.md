# ادغام محلی میزبان مالی در HR3 r7

در ۲۴ اکتبر ۲۰۲۶، successor مستقل `hr-read-host-v3-r7` برای بررسی CI محلی انتخاب شد. این گزارش بیرون از بستهٔ ثابت قرار دارد؛ `README.fa.md`، manifest و همهٔ فایل‌های مهرشدهٔ r7/r6 بازنویسی نشده‌اند. انتخاب `ci.py:HR3_PACKAGE` به معنای اجرای job روی GitLab، انتشار artifact یا فعال‌شدن runtime نیست.

## هویت سورس

| مورد | SHA256 یا شمارش |
| --- | --- |
| manifest بستهٔ r7 | `1e6dfad8a2d5626e555b42c1a402f60cbcf9a001d7156fcb5e537ade660a16a0` |
| manifest predecessor ثابت r6 | `4cdfe260b6706bd33f857288b76f58867ce0d227df7dd5d29c2b49b1ec8af585` |
| موجودی r7 / dependency / میزبان | ۳۲ / ۱۱۱ / ۱۴۸ فایل |
| delta مالی میزبان نسبت به r6 | ۱۳ تغییر، ۶ افزوده، صفر حذف |

هر ۱۹ فایل مالی با منبع پذیرفته‌شدهٔ ادغام مرکزی تطبیق داده شده است. C#، پروژه‌ها، props، targets و نسخه‌های سازگاری Program/Store بسته با r6 byte به byte یکسان‌اند. manifest جدید فقط هویت سورس candidate را ثبت می‌کند؛ موفقیت r6 یا بسته‌های قدیمی را به ارث نمی‌برد. runtime به‌صورت پیش‌فرض غیرفعال است.

## اجرای تازهٔ actual-host

اجرای opt-in واقعی r7 روی Windows، **۱۳ suite و ۹۰۸ assertion موفق**، صفر warning/error و DLL یکسان در خروجی میزبان و آزمون ثبت کرده است. manifest و موجودی ۳۲/۱۱۱/۱۴۸ پیش و پس از اجرا ثابت‌اند. restore از cache و config آفلاین بدون feed خارجی انجام شده و NuGet audit در همین اجرای محلی غیرفعال بوده است. بنابراین این نتیجه جای build/restore/audit فعال روی commit نهایی CI را نمی‌گیرد.

شواهد در `.build/hr-financial-successor-20261024/` نگهداری می‌شوند:

| فایل | SHA256 |
| --- | --- |
| `r7-local-verification.json` | `7f2567feca09ad725058ab3431139a4e3dad94deb7633006f43ad9a77f096288` |
| `r7-actual-host.log` | `a22293aa88498ac6e325be8cea9900cc54220c3b7014c3fdc6cbb013a8175950` |
| `BINARY-MANIFEST.json` | `2986398b8cf84f48815b783079447006ab1e3b120b22e0b6fe624218a7e5cda7` |

مسیر خروجی اجرا `.build/hr3-host-r7-artifacts/financial-successor-20261024/` است و نسخه‌های پذیرفته‌شده در `accepted-binaries/` کنار رسید محلی حفظ شده‌اند. مقدار `selectedInCi=false` در رسید تاریخی، وضعیت هنگام همان اجرا و پیش از تغییر selector را ثبت می‌کند و بازنویسی نشده است.

## اجرای واقعی Linux filesystem

اجرای اولیهٔ Windows از ۳۸ آزمون، ۳۵ موفق و ۳ skip به علت نداشتن مجوز ساخت symlink واقعی داشت. اجرای جداگانه روی Linux، همان payload سورس با **۳۲۹ عضو regular و ۳۲۷ فایل سورس** را بدون .NET، CI، identity یا runtime اجرا کرد: verifier برابر **۳۲/۱۱۱/۱۴۸** و آزمون‌ها **۳۸ اجرا، صفر failure، صفر error، صفر skip** بودند. source و pin ر6 در میزبان محلی و snapshot لینوکسی پیش و پس از اجرا ثابت ماندند. پوشهٔ موقت اختصاصی پس از بررسی هویت حذف شد؛ `cleanup=removed-own-verified-root` است.

شواهد Linux در `.build/hr-financial-successor-20261024/r7-linux-fs-run-5db7e93c76d04ef48e1401a6fc8b66d0/` قرار دارند:

| مورد | SHA256 |
| --- | --- |
| `summary.json` | `68ebc61b7d987a076e557c5242786af499c5f476dd0bae911a1413c3af279ffc` |
| `verification.log` | `035e06e51a3873127fdcfd24a109032123ecf7cade84d1036d16c157003d1a99` |
| ZIP سورس `r7-linux-filesystem-payload.zip` در پوشهٔ شواهد اصلی | `300311e44c2489907edd4df7b7b153716d20f7f8df5452b6721ece21902c0093` |
| manifest مستقل payload | `a090842db7a2c7ccf8168ea46bc828313d116cea8650e8161c72c329904c4bc0` |
| runner محلی بررسی‌شده | `d5f2c1e3b1bccadb34f40bfe04593bb1f9b85dc40ce7afee2a261e93793ee8d6` |

## آزمون محلی انتخاب CI

پس از تغییر selector، ۳۰ آزمون Python مربوط به orchestration/source identity و ۱۵ آزمون verifier آرشیو، همگی با صفر failure/error/skip موفق شدند. این آزمون‌ها .NET را اجرا نمی‌کنند؛ ترتیب restore/build/run و مرزهای بسته‌بندی را با fixture مستقل و فرایند Python واقعیِ verifier می‌آزمایند. موجودی r7 و predecessor ر6 پیش و پس از آزمون ثابت بود. بایت‌های ثبت مالی موجود در `verify_bundle.py`، `tests/test_ci_workspace.py` و `tests/test_verify_bundle.py` تغییر نکردند.

شواهد در `.build/hr-financial-successor-20261024/r7-ci-selection-20261024/` قرار دارند:

| فایل | SHA256 |
| --- | --- |
| `receipt.json` | `a5646f9914a40fff99a71fe6c4b6051a7508018a580223b1684c8580cd4bbc14` |
| `orchestration.log` | `e5aefd04288046ea1801f6339a5b6651f7b0f659c25302747c4668ff277ff52d` |
| `archive.log` | `f9c0d43b1f8fc6e5fe90c2275f4d26ffedf778e47a47b82aa8880a436982b99b` |

## مرز پوشش و گام CI

شواهد قبلی مالی روی میزبان استاندارد شامل PAYG برابر ۴۳، lifecycle برابر ۸۱، Billing API برابر ۶۱، Consumer Usage API برابر ۱۰۶ و regression مالی برابر ۳۸۶ بررسی است. این اعداد، اجرای تازهٔ مجموعه‌های مالی روی DLL opt-in HR3 نیستند؛ اجرای تازهٔ ۹۰۸ فقط baseline واقعی HR3 را روی سورس میزبان مالی پوشش می‌دهد.

ثبت موجود CI، سه suite مالی `PaygMetering.Tests`، `BillingLifecycle.Tests` و `Billing.Api.Tests` را در verify و پذیرش آرشیو اجباری می‌کند. verifier آرشیو selector مستقل HR3 ندارد؛ ترتیب اجباری `hr3_validate_host → hr_verify_package` و source identity همان commit حفظ می‌شود. اتصال GitLab، workflow، Runner، مجوزها و مسیر deploy در این انتخاب محلی تغییر نکرده‌اند.

اجرای trusted CI روی commit نهایی، audit فعال، artifact/OCI verification و پذیرش عملیاتی هنوز باقی‌اند. رسیدهای actual-host و filesystem همگی `trustedCi=false` و `operationalAcceptance=false` دارند. این گزارش ادعای نصب VPS، موفقیت pipeline، ثبت billing client واقعی یا اتصال عملیاتی HR ندارد.
