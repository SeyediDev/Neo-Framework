# از Hangfire به پیام‌رسانی بین سرویس‌ها

مرحلهٔ بعد: [Saga و Compensation با مثال رزرو و پرداخت](SAGA.fa.md).

## انتخاب ابزار

| نیاز | انتخاب |
|---|---|
| اجرای کار پس‌زمینه، زمان‌بندی گزارش و کار تکرارشونده | Hangfire و `IJobExecuter` فعلی Neo |
| اطلاع دادن یک اتفاق به چند سرویس مستقل | Publish با MassTransit روی RabbitMQ |
| سپردن دستور به یک صف مشخص | Send با MassTransit |

RabbitMQ پیام را مسیریابی و در صف نگهداری می‌کند. MassTransit کتابخانهٔ .NET است که قرارداد پیام، consumer، اتصال، retry و خطا را مدیریت می‌کند. `JobPublisher` فعلی Neo از MediatR استفاده می‌کند و پیام RabbitMQ منتشر نمی‌کند. این قابلیت به ثبت Hangfire دست نمی‌زند و تنها با `AddNeoRabbitMq` فعال می‌شود.

نسخهٔ این پیاده‌سازی MassTransit **8.4.1** مطابق مدیریت مرکزی پکیج‌های Neo است. نمونه به RabbitMQ 4.x وصل می‌شود؛ نیاز به پلاگین delayed-message ندارد. کپی کردن APIهای مستندات نسخه‌های دیگر بدون بررسی سازگاری توصیه نمی‌شود.

## اولین اجرا

پیش‌نیاز: .NET 10 SDK، Docker و Docker Compose. از ریشهٔ Neo:

```powershell
docker compose -f tools/Neo.Companion/samples/MessagingDemo/compose.yaml up -d --wait
dotnet run --project tools/Neo.Companion/samples/MessagingDemo
```

در ترمینال دوم:

```powershell
$event = @{ eventId = [guid]::NewGuid(); orderId = [guid]::NewGuid(); occurredAt = [DateTimeOffset]::UtcNow; failOnce = $true } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:5087/orders -Method Post -ContentType application/json -Body $event
Invoke-RestMethod http://127.0.0.1:5087/observations
```

پاسخ POST برابر 202 است: پیام به broker سپرده شده، پردازش تمام نشده است. چند لحظه بعد در observations دو کلید با EventId یکسان می‌بینید؛ `audit` با یک تلاش و `fulfillment` با دو تلاش completed می‌شوند. timeout ساختگی اول فقط در fulfillment است و آن consumer دوباره تلاش می‌کند. با ارسال مجدد همان `$event`، تعداد تلاش برای کار completed تغییر نمی‌کند؛ این جلوگیری از تکرار فقط در حافظهٔ همین پردازش است.

صفحهٔ مدیریت: `http://127.0.0.1:15672`، نام کاربری `neo_demo` و رمز `neo_demo_local_only`. این credential عمومی فقط برای نمونهٔ loopback است. نام صف‌ها `neo-demo-fulfillment` و `neo-demo-audit` خواهد بود. هر دو subscriber رویداد را دریافت می‌کنند؛ workerهای متعدد روی یک صف، کار را بین خود تقسیم می‌کنند.

## مشاهدهٔ خطا

پیام جدید با `eventId` جدید و `failPermanently = $true` بفرستید. fulfillment فقط یک تلاش می‌کند و completed نمی‌شود؛ audit مستقل است. در مدیریت RabbitMQ، صف `neo-demo-fulfillment_error` را ببینید. پس از رفع علت، پیام خطادار را با حفظ شناسهٔ منطقی رویداد و سیاست روشن replay کنید؛ نمونه عمداً با همان پرچم دوباره شکست خواهد خورد. صف `_skipped` معمولاً یعنی پیام به endpointی رسیده که consumer متناسب ندارد؛ با `_error` یکی نیست.

## دو پردازش مستقل

ابتدا worker را اجرا کنید تا صف و binding ساخته شود:

```powershell
dotnet run --project tools/Neo.Companion/samples/MessagingDemo -- --Role=worker --urls=http://127.0.0.1:5088
dotnet run --project tools/Neo.Companion/samples/MessagingDemo -- --Role=publisher --urls=http://127.0.0.1:5087
```

POST روی 5087 و observations روی 5088 است. بعد از اولین راه‌اندازی worker، آن را متوقف کنید و پیام بفرستید؛ صف durable پیام‌ها را تا برگشت worker نگه می‌دارد. **قبل از ایجاد binding اولیه، Publish تضمینی برای نگهداری پیام برای subscriber آینده ندارد.** برای تولید، topology را پیش از پذیرش ترافیک آماده کنید.

توقف broker بدون پاک‌کردن داده: `docker compose -f tools/Neo.Companion/samples/MessagingDemo/compose.yaml down`. volume باقی می‌ماند. پاک‌کردن volume دادهٔ صف‌ها را حذف می‌کند و جزو اجرای عادی این راهنما نیست.

## استفاده در برنامهٔ خودتان

```csharp
services.AddNeoRabbitMq(configuration, bus =>
{
    bus.AddConsumer<FulfillmentConsumer, FulfillmentDefinition>();
    bus.AddConsumer<AuditConsumer, AuditDefinition>();
});
// In a scoped application service:
await publishEndpoint.Publish(new OrderSubmitted(eventId, orderId, DateTimeOffset.UtcNow), cancellationToken);
// A command to a known queue instead of an event broadcast:
var endpoint = await sendEndpointProvider.GetSendEndpoint(new Uri("queue:neo-demo-fulfillment"));
await endpoint.Send(message, cancellationToken);
```

consumer/definitionها در `OrderConsumers.cs`، قرارداد در `OrderSubmitted.cs` و API/DI در `Program.cs` هستند. برای چند سرویس، قرارداد را در پروژه یا پکیج مشترک نگه دارید؛ Entity دیتابیس و secret در پیام نباشد. Send نمونه صرفاً تفاوت مقصد را نشان می‌دهد؛ در مدل واقعی نام دستور و رویداد را جدا کنید.

`RabbitMq` شامل Host، Port، VirtualHost، Username، Password، EndpointPrefix، PrefetchCount، ConcurrentMessageLimit و StartupTimeoutSeconds است. اعتبارسنجی تنظیمات پیش از اتصال انجام می‌شود؛ bus هنگام startup منتظر اتصال می‌ماند و timeout محدود دارد. `ConfigureEndpoints` بعد از تنظیم consumer و transport اجرا می‌شود. در برنامه‌ای که قبلاً `AddMassTransit` دارد دوباره آن را ثبت نکنید؛ این API برای یک bus است.

callback اختیاری `configureTransport` برای تنظیمات پیشرفته و TLS است؛ این helper به‌صورت پیش‌فرض TLS را فعال نمی‌کند. credential تولید را از secret store یا `RabbitMq__Username` و `RabbitMq__Password` بگیرید. prefix/vhost را برای محیط‌های dev و production جدا کنید. نمونه هیچ email، پرداخت یا تغییر سفارش واقعی انجام نمی‌دهد و API آن authentication ندارد؛ فقط روی loopback اجرا کنید.

## نکات اعتمادپذیری

- retry کوتاه فقط برای خطای موقتی است. timeoutهای طولانی داخل retry، ظرفیت consumer را نگه می‌دارند. برای اختلال طولانی redelivery با پشتیبانی transport لازم است؛ در این نسخه تنظیم نشده است.
- تحویل تکراری طبیعی است. `DemoObservations` با restart پاک می‌شود و بین replicaها مشترک نیست. در تولید شناسهٔ رویداد یا business key را با unique constraint و تغییرات کسب‌وکار در تراکنش مشترک ثبت کنید. برای سرویس خارجی از idempotency key آن سرویس استفاده کنید.
- `UseInMemoryOutbox` فقط پیام‌های خروجی را تا موفقیت پردازش نگه می‌دارد؛ durable outbox نیست و rollback دیتابیس یا HTTP نمی‌کند. Fulfillment پیام خروجی ندارد؛ Saga و consumer آزادسازی از بافر پیام خروجی استفاده می‌کنند.
- نوشتن دیتابیس و سپس Publish دو عملیات مستقل‌اند. `AddDomainEvent` و interceptor فعلی Neo، transactional outbox MassTransit نیستند. برای جلوگیری از شکاف commit/publish به outbox دیتابیسی سازگار با DbContext نیاز دارید؛ این مرحله در این نمونه پیاده نشده است.
- `/health/ready` health check ثبت‌شدهٔ MassTransit را گزارش می‌دهد. عمق صف، نرخ مصرف، پیام‌های error و قطع اتصال را پایش کنید. health check اثبات انجام عملیات کسب‌وکار نیست.
- برای ارسال دوره‌ای گزارش از Hangfire استفاده کنید؛ job می‌تواند از یک سرویس scoped برای Publish فراخوانی کند. retryهای Hangfire هم ممکن است publish تکراری بسازند، پس EventId پایدار و idempotency لازم است.

## تست و توسعهٔ زیرساخت‌های بعدی

`MessagingTests` بدون broker، pipeline واقعی MassTransit in-memory را اجرا می‌کند: fan-out، retry محدود، Fault و جلوگیری از تکرار در نمونه. تست مستقل RabbitMQ در workflow messaging، همان برنامه و Compose را اجرا می‌کند؛ این دو نوع آزمون جای یکدیگر را نمی‌گیرند.

برای هر زیرساخت جدید در Neo، کنار کد یک نمونهٔ runnable، فایل تنظیمات محلی، دستور اجرا، خروجی مورد انتظار، سناریوی خطا و توضیح محدودیت‌ها اضافه کنید. نمونهٔ صرفاً کامنت‌شده داخل کتابخانه برای یادگیری مصرف‌کننده کافی نیست. نمونه‌های فعلی را از README Companion پیدا کنید.

منابع رسمی: [RabbitMQ و topology در MassTransit](https://masstransit.io/documentation/configuration/transports/rabbitmq)، [خطا و retry](https://masstransit.io/documentation/concepts/exceptions)، [راه‌اندازی RabbitMQ](https://www.rabbitmq.com/docs/download). APIها در این ریپو با نسخهٔ 8.4.1 build می‌شوند؛ مستندات آنلاین ممکن است نسخهٔ جدیدتری را شرح دهند.
