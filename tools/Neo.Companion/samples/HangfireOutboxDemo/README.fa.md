# Outbox پایدار با Hangfire

این نمونه برای کسی است که با Hangfire کار می‌کند و می‌خواهد ثبت درخواست و اجرای job را از هم جدا کند. RabbitMQ برای اجرای این نمونه لازم نیست. SQL Server نمونهٔ کناری را اجرا کنید:

```powershell
docker compose -f tools/Neo.Companion/samples/DurableMessagingDemo/compose.yaml up -d sqlserver --wait
dotnet run --project tools/Neo.Companion/samples/HangfireOutboxDemo
```

فقط برای آموزش محلی است؛ روی `127.0.0.1:5091` گوش می‌دهد و احراز هویت ندارد. رمز عمومی فایل تنظیمات فقط متعلق به کانتینر آموزشی است. برنامه اجازهٔ ساخت خودکار دیتابیس را فقط برای نام‌های شروع‌شونده با `NeoHangfireDemo` می‌دهد؛ دیتابیس موجود را حذف نمی‌کند. در برنامهٔ واقعی migration بازبینی‌شده و احراز هویت لازم است.

```powershell
$id = [guid]::NewGuid().ToString()
$body = @{ operationId = $id; fail = $false } | ConvertTo-Json
$result = Invoke-RestMethod http://127.0.0.1:5091/receipts -Method Post -ContentType application/json -Body $body
Invoke-RestMethod http://127.0.0.1:5091/dispatch -Method Post
Invoke-RestMethod "http://127.0.0.1:5091/outbox/$($result.outboxId)"
Invoke-RestMethod "http://127.0.0.1:5091/receipts/$id"
```

ثبت `ReceiptRequest` و پیام `Requested` در یک تراکنش انجام می‌شود. worker دوره‌ای هر دقیقه پیام را به صف `outbox` می‌دهد؛ `/dispatch` همان کار را فوری انجام می‌دهد. job فقط `OutboxId` دارد و handler را داخل تراکنش همان `DemoContext` اجرا می‌کند. پس از موفقیت `outboxState=4` و `effects=1` می‌شود؛ `Queued=1` صرفاً پذیرش در صف است.

تمرین‌ها:

- با `?rollback=true` درخواست را بفرستید؛ نه درخواست و نه پیام باقی می‌ماند.
- برنامه را پس از ثبت درخواست ببندید و دوباره اجرا کنید؛ رکوردهای ثبت‌شده از SQL بازیابی می‌شوند.
- با `fail=true` درخواست تازه بفرستید. handler حتی SaveChanges می‌کند، ولی خطا باعث rollback اثر می‌شود؛ `effects=0` و `processError` قابل مشاهده است. بعد از ۳۰ ثانیه retry مجاز است و حداکثر سه اجرای handler انجام می‌شود.
- آزمون `OutboxDeliveryTests` قطع اجرا بین claim و enqueue، انقضای lease، مالک قدیمی و رسیدن worker سریع‌تر از dispatcher را با دیتابیس رابطه‌ای بررسی می‌کند.

## استفاده در برنامهٔ خودتان

بعد از ثبت سرویس‌های Application از `AddNeoEfOutbox<YourDbContext>()` استفاده کنید. `OutboxMessage` را در مدل، با تبدیل‌های شناسهٔ کاربر متناسب با برنامه، نگاشت کنید؛ نمونه صرفاً دو فیلد audit کاربر را نادیده می‌گیرد. `IProcessOutboxRecurringJob` و `IOutboxJobScheduler` را ثبت و worker دوره‌ای را فعال کنید. تنظیمات Hangfire باید صف `outbox` را مصرف کند. ثبت دقیق سرویس‌ها در `Program.cs` همین نمونه آمده است.

برای تراکنش مشترک، `OutboxMessage` را مستقیماً به context دارای تغییرات کسب‌وکار اضافه و یک‌بار commit کنید. مسیر عمومی `OutboxMessageProcessor` همچنان چند save و ثبت idempotency در cache دارد؛ تراکنش مشترک بین Redis و SQL ایجاد نمی‌کند. رکورد `PendingIdempotency` پس از قطع اجرا نیازمند تطبیق با کلید cache است و خودکار قابل ارسال نیست.

در ارتقای دیتابیس، سه ستون nullable `DeliveryLeaseId`, `DeliveryLeaseUntilUtc`, `NextAttemptAtUtc` و ایندکس وضعیت/زمان retry/lease را migration کنید. اعداد قبلی enum تغییر نکرده‌اند. رکوردهای قدیمی `Processing` بدون lease را پس از بررسی اثر واقعی و JobId تطبیق دهید؛ آن‌ها را گروهی به Requested برنگردانید.

lease ارسال و اجرا ۵ دقیقه است؛ job پس از ۴ دقیقه cancellation می‌گیرد. handler باید cancellation را رعایت کند. برای کارهای طولانی وظیفه را خرد کنید. claim با UPDATE شرطی و ساعت دیتابیس انجام می‌شود. سه dispatch قطع‌شده یا سه اجرای ناموفق به `Failed` می‌رسد. شکست پاکسازی، خطای اصلی را پنهان نمی‌کند و بازیابی بعدی از lease استفاده می‌کند.

فقط تغییرات همان context با وضعیت Processed اتمیک هستند. handler نباید تراکنش مستقل باز کند و worker به scope تازه نیاز دارد. این مسیر برای SQL Server/SQLite تست می‌شود؛ provider دیگر باید ExecuteUpdate و تبدیل زمان را پشتیبانی کند. روی context کارگر EF retry خودکار فعال نکنید؛ retry واحد کار از Outbox انجام می‌شود. درخواست HTTP، پرداخت یا ایمیل rollback دیتابیس ندارد؛ شناسهٔ عملیات ثابت را به سرویس مقصد بدهید و نتیجهٔ مبهم را تطبیق دهید. پس از احتمال ارسال موفق و قطع dispatcher، job تکراری ممکن است ایجاد شود؛ ادعای exactly-once برای اثر خارجی وجود ندارد.

برای replay دستی، ابتدا jobها و اثر مقصد را بررسی کنید؛ بعد رکورد Failed را در عملیات مدیریتی کنترل‌شده با ثبت علت و شناسهٔ عملیات اصلی بازنشانی کنید. این نمونه endpoint عمومی برای reset رکوردها ندارد. retry خود Hangfire ممکن است job را دوباره تحویل دهد، اما شمارنده و claim دیتابیس اجازهٔ بیش از سه اجرای handler را نمی‌دهند.

`AddNeoHangfire` executor دارای telemetry ثبت می‌کند؛ در نمونه، `ITelementryBehaviour`, `ITelementryObject`, تنظیمات telemetry و `IRequesterUser` صریح ثبت شده‌اند. هویت DemoRequester فقط برای اجرای آموزشی است؛ در برنامهٔ واقعی هویت مناسب job را تأمین کنید. startup نمونه قبل از اعلام آمادگی، worker و وابستگی‌هایش را resolve می‌کند.
