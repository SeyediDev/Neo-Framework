# reconciliation، crash و بازیابی مصرف

## تطبیق fixture

[reconciliation-v1.json](fixtures/reconciliation-v1.json) دو event مصنوعی دارد: operation اول با نرخ v7 و ۱۲ IRR، دوم با نرخ v8 و ۱۸ IRR. ترتیب تحویل دوم، اول، دوم، اول است. مصرف owner A در حساب consumer B ثبت می‌شود. نتیجهٔ دقیق: دو ledger usage entry، gross/outstanding برابر ۳۰، wallet صفر و available credit برابر ۹۹۷۰ از credit limit مصنوعی ۱۰۰۰۰.

فقط sum کافی نیست. reconciliation باید مجموعهٔ کلیدهای operation، snapshot، quantity، amount، receipt و outbox intent را تطبیق کند؛ «یک entry اضافه و یکی کم با total برابر» نیز خطاست. key گم‌شده، duplicated logical operation، orphan receipt، ledger بدون evidence، mismatch نرخ/مبلغ و event متعارض گزارش جدا دارند. معیار fixture اختلاف توضیح‌نداده‌شده صفر است. درصد سهم و settlement/payout تعیین نشده‌اند.

rate تاریخی v7 معتبر پس از دریافت v8 حفظ می‌شود. برای eventهای خارج از ترتیب، state tariff جاری عقب برده نمی‌شود؛ هر event با snapshot معتبر خودش ثبت می‌شود. contractVersion در اینجا ordering meter یا reset quota نیست.

## ماتریس پذیرش

| نقطهٔ شکست/تعارض | رفتار لازم | شاهد محلی مورد انتظار |
| --- | --- | --- |
| قبل از business commit | rollback تغییر، receipt و publish intent | الگوی source/test موجود؛ producer پیش از commit در harness جدید اجرا نشده است |
| بعد از business commit، پیش از response | یک effect؛ status با همان business key و event اولیه | timeout unknown و سپس inquiry، بدون اجرای تازه |
| قبل از Accounting commit | receipt، ledger و outbox همگی rollback | fault injection exception داخل Fabric.Transaction |
| بعد از Accounting commit، پیش از acknowledgment | reopen/retry همان receipt؛ بدون debit جدید | یک charge و یک outbox intent مالی |
| مقصد outbox commit، پیش از ack/cursor sender | redelivery به sink؛ effect مقصد یک بار | inbox مقصد پایدار + cursor sender |
| همان event، payload یکسان | پاسخ ثبت‌شده | revision/charge/outbox بدون تغییر |
| همان event، quantity/rate/actor تغییرکرده | conflict؛ evidence موفق حفظ می‌شود | zero new ledger/outbox |
| eventId تازه، operation همان و business payload یکسان | alias receipt به charge سابق | یک logical charge |
| eventId تازه، operation همان با business payload متفاوت | conflict/quarantine | zero additional charge |
| timeout upstream و status unavailable | unknown retained | execute count بدون افزایش حتی پس از deadline |
| tariff جاری عوض‌شده، event rate قدیمی | snapshot معتبر قبلی | نرخ/مبلغ قدیمی؛ regression API فعلی reprice را آشکار می‌کند |
| restore receipt/ledger/outbox به نقطهٔ backup | replay event اصلی idempotent | مجموعهٔ keys/amounts کامل و یک debit |
| event completed بعد از backup | reconcile از producer evidence | همان eventId و operation؛ هیچ backend replay |
| payload خراب، unit/currency/scale غلط | رد یا quarantine پیش از mutation | schema/semantic negatives |

exception rollback و dispose/reopen جای آزمون abrupt process kill، قطع برق، ازبین‌رفتن volume یا dispatcher واقعی نیستند. بخش‌هایی که sample فقط مدل می‌کند در EVIDENCE مشخص می‌شوند. معیار T-04 مرجع با ۱۰٬۰۰۰ event و crash injection روی محیط واقعی در این بسته تکمیل اعلام نمی‌شود.

## بازیابی با ابزار موجود

[FabricRecovery](../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/FabricRecovery.cs:14) و [runbook موجود](../../../../products/Fanasa.AccessManagement/docs/RECOVERY.md) reuse می‌شوند؛ store/broker یا backup framework تازه لازم نیست.

1. همهٔ writerهای PCC/Fabric/registry واقعاً متوقف شوند. `--offline` اعلام اپراتور است؛ ابزار stop یا تشخیص خودکار انجام نمی‌دهد.
2. از مسیرهای واقعی Fabric و registry بسته به مقصد تازه ساخته شود؛ از backup API SQLite استفاده شود، کپی ساده فایل اصلی با WAL کافی نیست. hash manifest، integrity و foreign-key checks ثبت شوند.
3. آخرین backup تأییدشده خارج از میزبان، زمان freeze/cut، producer evidence watermark، delivery cursor/epoch و eventهای unresolved ثبت شوند. دو DB مستقل فقط با توقف writerها restore point مشترک دارند.
4. restore فقط به مقصد تازه؛ overwrite پایگاه زنده ممنوع. قبل از بازکردن سرویس، receipt/event/ledger/outbox و membership/grant با مرجع مستقل تطبیق شوند.
5. تمام batchهای outbox پیمایش شوند؛ `Outbox()` تنها ۱۰۰ row نخست را نشان می‌دهد. تعداد کامل و digest مجموعهٔ کلیدها ثبت شود.
6. revoke/offboarding بعد از backup دوباره از مرجع هویت اعمال شوند. مصرف و پرداخت بیرونی بعد از backup با producer/provider reconcile شوند؛ سیستم بیرونی با restore محلی عقب نمی‌رود.
7. eventهای بعد از backup با همان هویت بازپخش شوند؛ عملیات backend دوباره اجرا نشود. اگر invoice/credit یا event outcome قطعی نیست، accounting-pending/needs-intervention حفظ شود.
8. cursor/ack ممکن است عقب رفته یا sink جلوتر باشد. دوباره‌تحویل فقط به sink idempotent با receipt پایدار؛ اعتماد به بیشترین outbox.Id یا reset keyها کافی نیست.
9. ledger fixture و real pilot evidence با zero unexplained difference و کنترل دسترسی پس از restore بررسی شوند؛ سپس بازگشایی با مسئول عملیات/مالی ثبت شود.

بسته موجود secret/config/certificate یا Keycloak database را شامل نمی‌شود. off-host custody، کلاینت/تنظیمات identity و artifact/config سازگار باید در عملیات مدیریت شوند. manifest SHA-256 خرابی را نشان می‌دهد و امضا یا اصالت تجاری ایجاد نمی‌کند.

## RPO و RTO

«صفر گم‌شدن پس از commit محلی» درباره interruption پردازش با volume سالم است؛ تضمین disaster recovery از backup نیست. restore نقطهٔ آخرین نسخهٔ سالم را برمی‌گرداند و بین cut backup تا incident شکاف دارد.

هدف پیشنهادی پلن، RPO حداکثر ۲۴ ساعت و RTO حداکثر ۴ ساعت است؛ این بسته آن را SLA یا نتیجهٔ اندازه‌گیری‌شده معرفی نمی‌کند. فاصلهٔ freeze/cut آخرین off-host backup تأییدشده تا incident، backup RPO مشاهده‌شده است. میزان usage در شکاف جدا با آخرین producer watermark و تعداد/amount unresolved ثبت می‌شود. RTO از incident تا بازگشایی امن پس از reconciliation اندازه‌گیری می‌شود، نه فقط مدت کپی فایل.

restore fixture نشان می‌دهد replay اصل event بعد از restore debit را دوبرابر نمی‌کند و event پس از backup قابل reconcile است. backup cadence، off-host انتقال، ظرفیت واقعی، payment settlement و امنیت محیط تولید در آن آزموده نمی‌شوند. نبود پاسخ چت دیگر مانع همین evidence محلی نیست.

رویدادی با occurredAt پیش از backup ممکن است پس از cut در Accounting پذیرفته شود و با restore از دست برود. fixture همین حالت را برای charge دیررس ۱۲ IRR نشان می‌دهد. watermarkهای producer و receipt/ack، مجموعهٔ هویت‌های پذیرفته‌شده و unresolvedها معیار تطبیق هستند؛ فیلتر صرف timestamp وقوع، شکاف را پنهان می‌کند.
