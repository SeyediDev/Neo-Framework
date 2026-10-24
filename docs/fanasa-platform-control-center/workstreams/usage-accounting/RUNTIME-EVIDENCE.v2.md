# شواهد runtime مصرف — مرحلهٔ دوم

تاریخ: ۱۰ اکتبر ۲۰۲۶. اتصال اختیاری به ContractPolicy موجود، قیمت‌های نسخه‌دار
تغییرناپذیر و تحویل outbox در همان محصول Access Management و Fabric پیاده‌سازی شد.
فعال‌سازی metering و worker پیش‌فرض خاموش است؛ دادهٔ واقعی یا مقصد بیرونی تغییر نکرد.

## نتیجهٔ اجرای نهایی

- build محصول و runner: موفق، صفر warning و error.
- runner کامل: **۳۵۶ check با exit 0**؛ ۱۰۸ check تازه و ۷۰ check مصرف مرحلهٔ اول.
- ورودی‌های منتخب runtime پیش و پس از build/test همسان بودند؛ baseline فقط پس
  از موفقیت build، آزمون‌ها و بررسی همسانی نوشته شد.
- بررسی whitespace تغییرهای tracked مربوط به wiring، database، runner و README:
  موفق. هشدارهای Git صرفاً تبدیل LF به CRLF بودند.

فرمان اجرا از root مخزن:

```powershell
& ./docs/fanasa-platform-control-center/workstreams/usage-accounting/verify-runtime.ps1
```

[log ساخت](fixtures/runtime-build.v2.txt)، [log آزمون](fixtures/runtime-checks.v2.txt)
و [baseline سورس/باینری](fixtures/runtime-source-baseline.v2.json) شواهد این اجرا هستند.
اسکریپت build ترتیبی بدون build server و بدون restore انجام می‌دهد؛
نسخهٔ جاری اسکریپت شواهد مرحلهٔ سوم را می‌نویسد؛ logها و manifest نسخهٔ ۲
بالا، نتیجهٔ تاریخی همین مرحله‌اند و بازنویسی نمی‌شوند.
`NuGetAudit=false` فقط به فرمان محلی محدود است. اجرای filesystem با دسترسی لازم
برای cacheهای محلی انجام شد. اولین اجرای کامل ۳۵۶ check گذراند ولی تغییر هم‌زمان
سورس را تشخیص داد و baseline ننوشت؛ اجرای نهایی روی وضعیت ثابت موفق بود.

baseline فایل‌های منتخب Web، Organization.Tests، ContractPolicy، schemaهای آن و
تنظیمات build را با SHA-256 ثبت می‌کند. سه باینری ثبت‌شده، اسمبلی runner، Web و
ContractPolicy در خروجی واقعی runner هستند. این manifest کامل dependency، SDK،
محیط عملیاتی یا deployment نیست. [شواهد و baseline نسخهٔ ۱](RUNTIME-EVIDENCE.md)
و شواهد prototype تاریخی حفظ شدند.

## رفتار بررسی‌شده

`OrganizationUsagePriceStore` مالک و meter را از منبع مرکزی/پروفایل سرور می‌گیرد.
قیمت دقیق ID/version با snapshot کامل، دلیل و actor پایدار می‌شود. retry همسان
audit تازه ندارد و تغییر همان نسخه تعارض است. انتخاب «آخرین قیمت» وجود ندارد.
آزمون‌ها بازهٔ UTC، ارز و scale، مبلغ decimal، مالک، rollback، restart، restore
واقعی و سند ذخیره‌شدهٔ خراب را بررسی کردند.

حالت `contract-policy` از `ContractPolicyStore.Apply/Find/ReadAllocation` واقعی
استفاده می‌کند. fixture با producer احراز‌شده، projection معتبر v2 و اشتراک
مرکزی پایدار ساخته شد. شناسه‌های تجاری opaque با `ContractLinks` صریح به GUIDهای
موجود متصل می‌شوند. نبودن projection/قیمت، ناسازگاری allocation/link، تعلیق،
انقضای قیمت برای عملیات تازه و نسخهٔ long خارج از محدودهٔ UsageEvent 1.0 رد شدند.
قیمت نسخهٔ ۱ و ۲ به‌ترتیب charge خود را ساختند؛ replay تاریخی مبلغ قبلی را حفظ کرد.
meter سهمیهٔ درخواست با meter مالی تغییر موفق یکی فرض نشده است.

`UsageOutboxDispatcher` تلاش اولیه و cursor را پیش از await در Fabric ذخیره
می‌کند. آزمایش‌ها پاسخ گم‌شده پس از commit مقصد، retry پس از restart، ack اشتباه،
lease/fence، worker قدیمی، scope مصرف‌کننده، ردیف خراب، charge ناسازگار و restore
واقعی source با مقصد جلوتر را بررسی کردند. ارسال دوباره همان هویت/رویداد را
حفظ کرد و دریافت‌کنندهٔ آزمایشی اثر دوم نساخت. شکست یا poison باعث skip مالی نشد.
اجرای dispatcher در تراکنش محیطی پیش از شبکه رد شد.

`HttpUsageOutboxTransport` با handler مصنوعی و بدون شبکه آزموده شد: wire اصلی،
Bearer سرور، کلید idempotency، هویت ack، disposition، اندازهٔ محدود، JSON سخت‌گیر،
خطا/redirect، cancellation و تنظیمات نامعتبر. endpoint/consumer نامعتبر پیش از
ارسال رد شد. wiring واقعی HttpClient redirect را غیرفعال می‌کند؛ timeout مشترک
headers/body از lease کوتاه‌تر است.

## حدود شواهد

این مرحله اعتبارسنجی امضای قرارداد تجاری، PDP/Quota admission عملیاتی، settlement،
مالیات، DLQ خودکار یا مقصد HTTP واقعی را ثابت نمی‌کند. ورودی projection احراز‌شده
با قرارداد تجاری امضاشده یکی نیست. قیمت و هویت‌های fixture مصنوعی‌اند. آزمون‌های
مستقل Gateway/ContractPolicy متعلق به شواهد همان workstream هستند.

restore یک backup قدیمی رویدادهای پس از backup را بازنمی‌گرداند. مقصد باید receipt
تکرار را حفظ کند؛ رویدادهای حذف‌شده به replay معتبر یا reconciliation نیاز دارند.
anchor cursor باید حفظ شود. آزمون‌ها اثبات abrupt process kill، قطع برق، RPO صفر
یا exactly-once شبکه نیستند.

تنظیمات، مرزهای API و قرارداد ack در
[راهنمای محصول](../../../../products/Fanasa.AccessManagement/docs/USAGE-ACCOUNTING.md)
آمده‌اند. Skill/MCPهای موجود Neo این API محصول را مصرف نمی‌کنند؛ قراردادهای آن‌ها
در این مرحله تغییر نکرد و integration تازه‌ای ساخته نشد.
