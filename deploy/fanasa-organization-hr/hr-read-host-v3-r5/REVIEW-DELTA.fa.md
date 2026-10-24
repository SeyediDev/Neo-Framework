# بازبینی سازگاری HR3 r5، ۲۳ اکتبر ۲۰۲۶

این successor جای بسته‌های تاریخی r3/r4 را بازنویسی نمی‌کند. manifest بستهٔ r4 با SHA-256
`ea64dc9b358d4526456bb30873d26363031155cc97da263fc6820b9dc20eb32d` ثابت مانده است.
فهرست تازه از سورس واقعی جمع‌آوری شده و نتیجهٔ تاریخی کپی نشده است. وضعیت
`reviewed-local-ready-for-ci` یعنی بازبینی و اجرای واقعی محلی تکمیل شده، ۱۳۸ ورودی
میزبان freeze هستند و تغییر آن‌ها پیش از build رد می‌شود؛ اجرای GitLab همان commit
و پذیرش عملیاتی هنوز ادعا نمی‌شود.

تغییر رفتاری محدود به `src/Hr3CentralHostRecords.cs` است: Permit بومی وابسته به PIP
نمی‌تواند بیش از `Pip.FreshUntil` اعتبار داشته باشد. `AccessDecision.Pip` فقط یک
provenance را حمل می‌کند؛ اگر سیاست‌های attribute منتشرشده برای همان permission بیش
از یک منبع داشته باشند، HR3 به جای ساختن summary ناقص رد می‌کند. این محدودیت ممکن
است برای یک منبع با سیاست آینده نیز محافظه‌کارانه رد ایجاد کند؛ راه جدیدی برای Allow
یا اعطای grant اضافه نمی‌شود. تعهد audit بومی همچنان انجام‌نشده است و Permit دارای
آن به شاهد مجاز HR3 تبدیل نمی‌شود.

`PipCoexistenceTests` از PipStore واقعی، AccessPolicyStore واقعی، همان FabricDatabase
fixture، source واقعی HR3 و serialization/post-read واقعی HrReadPdpService استفاده
می‌کند. داده مصنوعی است و HTTP خارجی ندارد. کنترل‌ها شامل lease کوتاه، مرز انقضای
انحصاری، deny منطبق، audit انجام‌نشده، source غیرفعال، snapshot گم‌شده، subject/tenant/
generation ناسازگار، نبود ثبت PIP، منبع چندگانه و revoke/expiry/deny/shorter lease بین
دو snapshot مستقل است. بررسی fingerprint شامل رجیستری، تمام Fabric و outbox است.

اختلاف معنایی حفظ‌شده برای تصمیم مالک PIP: `EvaluateForReadOnlyHost` فعلی سیاست‌های
attribute با `Enforce=false` را مانند `Enforce=true` در نظر می‌گیرد. فیلتر simulation-only
تقویم‌ها به attribute تعمیم داده نشده است. r5 کد اصلی را تغییر نمی‌دهد و آزمون هر دو
مقدار را صریحاً ثبت می‌کند؛ بنابراین ادعای simulation-only بودن PIP در HR3 مجاز نیست.

هستهٔ contract-policy مورد استفاده HR3 همان dependency ثابت `hr-read-profile-v3` است.
linkهای PCC فعلی جز Store، Web فعلی و هشت فایل Pip جدید کامپایل می‌شوند؛ Program و Store
کپی‌شده همچنان دقیقاً با r4 برابرند. فهرست اولیه ۱۳۸ ورودی میزبان داشت: ۱۲ ورودی
جدید نسبت به r4 شامل هشت فایل Pip، MobileApplicationCatalog و سه asset ناوبری/معماری.
۱۳ فایل قبلی نیز drift داشتند. این موجودی صرفاً یک snapshot است؛ manifest نهایی باید
همهٔ سورس جاری و تغییرات هم‌زمان را دوباره مقایسه کند، نه اینکه hash مغایر را کورکورانه بپذیرد.

آزمون‌های لازم برای انتشار: inventory verifier و آزمون‌های filesystem آن، full HR3
actual-host suite بدون فیلتر، مجموعهٔ کامل Organization.Tests (از جمله PIP)، temporal،
bearer، HR، readerها و آزمون‌های تغییرات CI. restore/audit آنلاین باید فعال باشد.
publish باید حضور `System.Security.Cryptography.Pkcs 10.0.13` و حذف تنظیمات محلی/محیطی
را در artifact ثابت کند. قبل/بعد آزمون، manifest، ورودی‌ها، فرمان، SDK، لاگ، نتیجه و
hash لاگ‌ها ثبت شوند. اجرای local با artifact تازه هنوز نتیجهٔ GitLab همان commit یا
پذیرش عملیاتی نیست. فعال‌سازی runtime و اتصال HR/IdP از scope این بسته بیرون است.

## رسید اجرای محلی تازه

full actual-host با SDK 10.0.401 و restore/audit فعال، ۹۰۸ assertion در ۱۳ suite را
گذراند؛ ۶۱ assertion مربوط به PIP است. build صفر warning/error داشت. manifest آزمون‌شده
869c397defa681a90a7ad1c5040ebf3c75faa021057480ea9886925e935e53c1 و inventory
قبل/بعد یکسان بودند. آزمون filesystem: ۳۱ موفق و ۳ skip مجوز symlink واقعی Windows.
رسید بیرون از بسته در ../hr3-r5-local-validation.json، hash لاگ و تمایز metadata نهایی
از byteهای آزمون‌شده را ثبت می‌کند. C#/build و dependencyهای کامپایل‌شده تغییر نکرده‌اند.
