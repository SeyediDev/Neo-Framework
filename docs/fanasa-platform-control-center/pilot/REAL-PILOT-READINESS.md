# ورودی‌های پذیرش یک محصول واقعی

این ادامه فقط یک محصول سازمان، یک consumer متفاوت و عملیات `organization.unit.save` را هدف می‌گیرد. تست native با fixture مصنوعی، ثبت یا پذیرش تجاری این محصول نیست. تا زمان ارائه و بررسی منابع زیر، فعال‌سازی مسیر واقعی پذیرفته نشده است.

## شاهدهای موجود

[ذخیرهٔ PCC](../workstreams/contract-policy/implementation/pcc/ContractPolicyStore.cs)، [ورودی producer و ارزیابی سیاست](../workstreams/contract-policy/implementation/pcc/ContractPolicyApi.cs) و [adapter پذیرش سازمان](../workstreams/contract-policy/implementation/pcc/ContractPolicyHost.cs) در همان میزبان موجودند. انتشار تغییرناپذیر limits، رسید durable مشاهده و receiver امضاشدهٔ Gateway پیاده شده‌اند؛ receiver با mTLS/HMAC و کنترل مجدد اختیار و deadline داخل تراکنش Fabric کار می‌کند. تنظیمات واقعی trust و metering پیش‌فرض فعال نیستند و observer پیش‌فرض unavailable است. [گزارش اتصال](CENTRAL-GATEWAY-CONNECTION.md) مرز این تحویل را مشخص می‌کند.

چت «مدیریت قراردادها» تحویل قابلیت ظرفیت و amendment را گزارش کرده است؛ [سند پذیرش قراردادها](../../../../NeoContract/docs/SERVICE-CAPACITY.md) تصریح می‌کند پنج پیشنهاد واقعی امضاشده تلقی نشده‌اند و grant، role binding یا quota واقعی برای آن‌ها ایجاد نشده است. در این snapshot، شناسه‌های authoritative همین پایلوت برای قرارداد امضاشده، نرخ، client/grant و allocation اثبات نشده‌اند.

## حداقل داده لازم

| ورودی | شاهد پذیرش |
| --- | --- |
| محصول، owner و مصرف‌کننده | ثبت مرکزی durable و فعال؛ owner و consumer متفاوت؛ product/audience/client به همان محصول تعلق داشته باشند. |
| tenant، actor و مجوز | عضویت فعال، grant صریح `organization.write`، تخصیص نقش `ApiInvoker`، policy منتشرشده و subscription/entitlement معتبر؛ header مشتری منشأ اختیار نیست. |
| قرارداد و amendment | شناسه و نسخه، منشأ producer مجاز، امضا و پذیرش طرفین، وضعیت و دورهٔ مؤثر، تطبیق طرفین و محصول. |
| allocation و limits | شناسه پایدار، canonical scope، revision و limits hash؛ تغییر قرارداد، suspend یا revoke مصرف قبلی را reset نکند. |
| نرخ | snapshot تغییرناپذیر و نسخه/منشأ معتبر که به همان قرارداد، عملیات و allocation متصل باشد؛ قیمت fixture یا تنظیم تنها، اثبات امضا نیست. |
| admission | شاهد معتبر و idempotent برای همین tenant/actor/request/payload؛ obligations لازم واقعاً تکمیل شده باشند. |

مرجع نرخ [OrganizationUsageMetering.cs](../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/OrganizationUsageMetering.cs) از تنظیمات مورد اعتماد می‌آید. receiver تطبیق contract/version/allocation/period/price version را پیاده کرده است؛ این تطبیق، امضای تجاری و منشأ authoritative نرخ واقعی را اثبات نمی‌کند. بازپخش رسید باید snapshot قیمت ثبت‌شده را حفظ کند.

## ترتیب کوچک‌ترین اتصال

۱. tuple واقعی جدول بالا را از منابع موجود تحویل و بررسی کنیم؛ fixture یا رکورد امضای فرضی ساخته نمی‌شود.

۲. انتشار limits و ثبت رسید durable به همان ingress/store وصل شده‌اند. تا مشاهدهٔ معتبر، پاسخ HTTP 202 با `phase=pending` و برای نسل منسوخ `phase=superseded` است. پاسخ HTTP 200 با `{eventId, aggregateVersion, phase: acknowledged, gateway: ...}` فقط پس از مشاهدهٔ معتبر و کنترل مجدد نسل و مجوز صادر می‌شود. endpoint مشاهدهٔ authenticated در native Gateway و adapter واقعی آن هنوز لازم‌اند؛ دریافت projection جای آن را نمی‌گیرد.

۳. receiver همان `IContractPolicyObligationExecutor` را مصرف می‌کند؛ trust واقعی گواهی، کلید امضا، client/delegation و tuple تجاری باید تحویل و پذیرفته شوند. native Redis هنوز dedup عملیات/payload ندارد؛ nonce backend تضمین idempotency سهمیه نیست. mutation، receipt، usage، snapshot، ledger و outbox در storeهای موجود می‌مانند و quota در backend دوباره فراخوانی نمی‌شود.

۴. یک درخواست مجاز و ردهای امضای خراب، tenant خارجی، grant لغوشده، قرارداد معلق، کمبود سهمیه، تصمیم منقضی و replay متناقض را از Gateway تا commit و مصرف بررسی کنیم. هر رد باید نبود mutation/debit را اثبات کند؛ replay معتبر نباید mutation یا debit دوم بسازد.

این مسیر به registry، broker، workflow engine یا runtime مالی تازه نیاز ندارد. آزمون ظرفیت و بازیابی چند replica، پس از عبور از گیت منابع، شاهد جداگانه می‌خواهد.

## مرز آزمون میزبان

۷۷ بررسی Bearer و ۱۱۴ بررسی PCC، شاهدهای مرحلهٔ قبلی‌اند. [آزمون میزبان PCC](../workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Pcc.Tests/Program.cs) همراه ۲۰ case جدید publication در این ادامه **۱۳۴ موفق، صفر خطا و exit صفر** داشت؛ ۱۶ hash منبع قبل/بعد اجرا ثابت بودند. [شاهد اتصال](central-gateway-verification.json) نتیجه و hashها را ثبت می‌کند. suite مستقل receiver **۷۸ موفق/صفر خطا با exit صفر** داشت؛ Kestrel HTTPS/mTLS واقعی و HMAC/JWT با هویت و قرارداد مصنوعی، replay و rollback را پوشش دادند. [شاهد مالک](../workstreams/contract-policy/implementation/EVIDENCE.v0.4.0.md) و manifest آن، با تأیید مستقل ۶۰ hash، ثبت‌اند. Lua/APISIX/Redis در این suite اجرا نشده‌اند. هیچ آزمون محلی جای امضا، provisioning یا استقرار واقعی را نمی‌گیرد.
