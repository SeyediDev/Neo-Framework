# اتصال مرکزی Gateway و PCC

این ادامه در ۱۰ اکتبر ۲۰۲۶، همان میزبان Access Management/PCC و Fabric موجود را گسترش می‌دهد. registry، broker، workflow یا runtime مالی تازه اضافه نشده است. شواهد این مرحله محلی و مصنوعی‌اند؛ فعال‌سازی تجاری و rollout واقعی Gateway پذیرفته نشده‌اند.

## انتشار و رسید مشاهده

[ContractPolicyStore](../workstreams/contract-policy/implementation/pcc/ContractPolicyStore.cs) هنگام پذیرش رویداد، در همان تراکنش projection/allocation/inbox/outbox، snapshot مستقل و تغییرناپذیر publication را ذخیره می‌کند. snapshot شامل event/aggregate version، fingerprint رویداد، client تولیدکننده، projection کامل و hash دقیق limits است. کلید publication از contract و event تشکیل می‌شود؛ publisher دیرهنگام محتوای قرارداد جدیدتر را جای رویداد قدیمی نمی‌گذارد.

[ContractGatewayPublication](../workstreams/contract-policy/implementation/pcc/ContractGatewayPublication.cs) hash سازگار با `serviceLimitHash` در NeoContract را محاسبه می‌کند: کلیدهای object مرتب، ترتیب array حفظ، JSON فشرده و SHA-256 از UTF-8. دریافت projection، پاسخ provision و context پذیرش درخواست، مشاهدهٔ rollout را اثبات نمی‌کنند.

ingress بدون مشاهدهٔ معتبر، HTTP 202 و `phase=pending` برمی‌گرداند. ورودی stale، `phase=superseded` دارد و رسید نسل جاری به آن نسبت داده نمی‌شود. `IContractGatewayObserver` پیش‌فرض unavailable است؛ native Gateway فعلی endpoint مشاهدهٔ authenticated ندارد. تنها adapter مورد اعتماد که همان snapshot را واقعاً مشاهده کرده باشد می‌تواند رسید را به Fabric بسپارد. API عمومی برای ثبت observation از body مشتری ایجاد نشده است.

پس از تطبیق دقیق contractVersion/allocation/scope/configRevision/limitsSha256 و زمان مشاهده، و کنترل مجدد مجوز و نسل جاری projection و allocation، رسید پایدار زیر صادر می‌شود:

```json
{
  "eventId": "<accepted-event>",
  "aggregateVersion": 0,
  "phase": "acknowledged",
  "gateway": {
    "contractVersion": 0,
    "allocationId": "<allocation>",
    "canonicalScope": "<scope>",
    "configRevision": 0,
    "limitsSha256": "<sha256>",
    "observedAt": "<UTC-milliseconds>"
  }
}
```

اعداد صفر در شکل بالا صرفاً نمایش ساختارند؛ رکورد معتبر نسخه‌های مثبت دارد. رسید مشاهده پس از اولین ثبت تغییر نمی‌کند. تکرار دقیق outbox دوم نمی‌سازد؛ تغییر محتوا، نسل منسوخ، allocation مشترک تغییرکرده و مجوز لغوشده ACK نمی‌گیرند. revoke دیرهنگام، observation و outbox آن را با هم rollback می‌کند.

## context واقعی و پذیرش عملیات

receiver و PDP اختصاصی مالک قرارداد در فایل‌های `ContractPolicyGatewayContext.cs`، `ContractPolicyGatewaySecurity.cs`، `ContractPolicyGatewayDecision.cs` و `ContractPolicyGatewayApi.cs` به‌کار گرفته می‌شوند. پیاده‌سازی تکراری ساخته‌شده در همین چت حذف شد؛ دو receiver موازی وارد runtime نشده‌اند.

مسیر `/api/gateway/decision`، credential سرویس Gateway را با `GatewayDecisionBearer`، ثبت مرکزی client/product و scope فعال اعتبارسنجی می‌کند. این credential با هویت انسان یکی نیست؛ assertion هویت مصرف‌کننده فقط در پروفایل delegation صریح Gateway مجاز است. PIP همچنان tenant، عضویت، grant مستقیم، role، policy و allocation را از منابع مرکزی می‌خواند. پیش‌فرض تنظیمات، مسیر را بسته نگه می‌دارد.

receiver context امضاشدهٔ native Gateway را با گواهی واقعی mTLS و pin SHA-256 همان peer بررسی می‌کند. HMAC به method، مسیر دقیق، Authorization اصلی، بایت‌های body، Idempotency-Key و envelope متصل است. snapshot body پیش از MVC model binding گرفته می‌شود؛ هدر مشتری یا گواهی forwarded منشأ اختیار نیست. شناسه `g-...` nonce تلاش Gateway و UUID داخل body شناسه عملیات کسب‌وکار است؛ برابری این دو ادعا نمی‌شود.

executor، nonce را یک بار در Fabric ثبت می‌کند، deadline و binding قرارداد/policy/allocation/rate را کنترل می‌کند و فقط completionهای پشتیبانی‌شده را به wrapper موجود می‌دهد. wrapper مجموعهٔ دقیق completion، تصمیم تازه و deadline تصمیم اصلی را حفظ می‌کند. quota دوباره فراخوانی نمی‌شود. mutation/receipt/usage/ledger/outbox عملیات در تراکنش موجود باقی می‌مانند؛ یک witness تازه برای replay همان عملیات، charge کسب‌وکار دوم ایجاد نمی‌کند.

`IContractPolicyGatewayCommitGuard` پس از گرفتن قفل writer در همان تراکنش Fabric، پیش و پس از اجرای عملیات، هویت، اختیار، نسل binding، گواهی و deadline تصمیم اصلی را دوباره بررسی می‌کند. veto پیش از commit، mutation و receipt و مصرف کسب‌وکار را با هم rollback می‌کند؛ nonce پذیرفته‌شدهٔ تلاش Gateway جداگانه مصرف‌شده باقی می‌ماند و refund سهمیه فرض نمی‌شود. بازپخش رسید با نرخ بازنشسته در suite مستقل receiver گذشت: همان actor و fingerprint و history و اختیار جاری کنترل می‌شوند، اما نرخ قبلی دوباره قیمت‌گذاری نمی‌شود. flag تعیین‌شده پیش از mutation نیز اجازه نمی‌دهد receipt تازه کنترل نهایی نرخ را دور بزند.

## مرزهای باقی‌مانده

- native limiter به ازای تلاش پذیرفته‌شده سهمیه کم می‌کند و هنوز request/payload dedup در همان تراکنش Redis ندارد. nonce backend و receipt عملیات، این فاصله را به admission idempotent تبدیل نمی‌کنند. retry پس از نتیجهٔ نامعلوم پذیرفته‌شده فرض نمی‌شود و refund ساخته نمی‌شود.
- endpoint مشاهدهٔ نسخهٔ Gateway و adapter authenticated آن هنوز تحویل نشده‌اند؛ commercial outbox با projection قبول‌شده acknowledge نمی‌شود.
- قرارداد/نرخ امضاشده، client/grant/delegation و allocation واقعی همین محصول هنوز شاهد authoritative ندارند. پیکربندی مصنوعی آزمون، provisioning واقعی نیست.
- ۱۲ بررسی native قبلی متعلق به single-Gateway smoke محدود است؛ این تغییرات receiver در آن اجرای VPS حضور نداشته‌اند. آزمون چند replica/ظرفیت/بازیابی همچنان گیت مستقل دارد.

آزمون PCC این اتصال **۱۳۴ موفق، صفر خطا و exit صفر** داشت؛ ۱۶ hash منبع در طول اجرا ثابت بودند. ۲۰ case جدید publication، hash اصلی NeoContract شامل فارسی/emoji، انتظار HTTP 202، tuple/time mismatch، لغو مجوز و rollback، تغییرناپذیری و replay رسید، reopen و allocation drift را پوشش دادند. [شاهد اتصال](central-gateway-verification.json) hash منبع، لاگ و DLLها را ثبت می‌کند؛ suite مستقل receiver نیز **۷۸ موفق، صفر خطا و exit صفر** داشت؛ HTTPS/mTLS واقعی Kestrel، JWT/HMAC، nonce و رقابت هم‌زمان، replay، نرخ بازنشسته و rollback انقضا را با هویت و قرارداد مصنوعی آزمود. [شاهد مالک نسخهٔ ۰٫۴](../workstreams/contract-policy/implementation/EVIDENCE.v0.4.0.md) لاگ و hash DLLها را ثبت می‌کند؛ هماهنگ‌کننده لاگ ۷۸/۰، دو DLL و ۵۵ input و ۵ hook manifest را بدون mismatch تأیید کرد. Lua/APISIX/Redis در این suite اجرا نشده‌اند و ۱۲ smoke قبلی شاهد native جدید نیستند.

پاک‌سازی فایل‌های تولیدی در [شاهد محلی](storage-cleanup.json) و حذف صوت‌های VPS در [شاهد VPS](vps-audio-cleanup.json)، مستقل از شواهد اجرا ثبت شده‌اند. هر شش اصل ضبط، قطعه‌های موقت و مدل‌های مختص رونویسی با مجوز صریح کاربر حذف شدند. متن‌ها و گزارش‌های موجود حفظ شدند؛ نبود متن خام اصلی ضبط‌های ۱ و ۲ پیش از حذف، همراه متن جایگزین ۲ ثبت شده است.
