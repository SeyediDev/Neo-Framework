# DDD، مدیریت رویداد و صف در Neo

## مدیریت اجرای این تغییر

مرجع عملیاتی وضعیت کارها دیتابیس **WorkManagement**، پروژه **NEO** است. درخواست اصلی با شناسه `NEO-EVT-AUDIT-001` ثبت شده و هر مرحله مالک، شاخه، زمان، شواهد تست و کامیت مستقل دارد. [مشخصات یافته‌ها و معیار پذیرش](event-delivery-backlog.json) نسخهٔ قابل‌مرور در ریپازیتوری است؛ وضعیت زندهٔ برد را تکرار نمی‌کند.

| مرحله | شناسه | مسئلهٔ اصلی |
|---|---|---|
| ۱ | NEO-EVT-101 | ثبت واقعی DomainEvent، cancellation، شکست handler و رویدادهای زنجیره‌ای |
| ۲ | NEO-EVT-102 | serialization کار Hangfire، حفظ رویدادهای صف‌نشده و تنظیم صف Outbox |
| ۳ | NEO-EVT-103 | رقابت Idempotency، بازماندن پیام تکراری و مالکیت قفل Redis |
| ۴ | NEO-EVT-104 | بازیابی dispatch قطع‌شده و ارتباط نتیجهٔ job با OutboxId |
| ۵ | NEO-EVT-105 | Outbox تراکنشی RabbitMQ و ذخیرهٔ پایدار Saga |
| ۶ | NEO-EVT-106 | نمونهٔ کامل، تست خرابی، راهنمای عملیات و Skills/MCP |

## مرزهای فعلی و تصمیم معماری

`BaseEntity.AddDomainEvent` رویداد را در حافظهٔ Entity نگه می‌دارد. `DispatchDomainEventsInterceptor` هنگام `SavingChanges` آن را به MediatR می‌دهد؛ این مرحله پیش از ثبت دیتابیس است. handler دامنه می‌تواند تغییرات محلی همان DbContext را آماده کند، اما ارسال مستقیم ایمیل، HTTP یا پیام broker در آن به معنی ارسال پس از commit نیست.

مسیر `IEventContainer` مربوط به خروجی command است و از رویداد Entity مستقل است. `PublishEventsOfCommandsBehaviour` در ثبت پیش‌فرض Application فعال نیست. `JobPublisher` نیز MediatR را فراخوانی می‌کند؛ broker ناشی از این نام نیست.

Outbox فعلی Neo، کار را از طریق `DefaultOutboxJobScheduler` به Hangfire می‌دهد. `AddNeoRabbitMq` یک bus جدا و صریح ثبت می‌کند. صرفاً فعال‌کردن هر دو، ذخیرهٔ دیتابیس و انتشار RabbitMQ را اتمیک نمی‌کند.

تصمیم اجرای این مراحل: **رویداد دامنه داخل برنامه باقی می‌ماند؛ handler آن یک قرارداد مستقل و دارای شناسهٔ پایدار برای یکپارچه‌سازی می‌سازد و در Outbox همان تراکنش قرار می‌دهد.** انتقال به broker پس از commit انجام می‌شود. rollback بیرونی پس از SaveChanges نیز باید ارسال را متوقف کند؛ تغییر سادهٔ interceptor از SavingChanges به SavedChanges این تضمین را ایجاد نمی‌کند.

هنگام شکست handler یا دیتابیس، تغییرات حافظهٔ DbContext و اثر خارجی خودکار rollback نمی‌شوند. retry واحد کار باید سیاست روشنی داشته باشد؛ برای handler دارای اثر خارجی از کلید idempotency و ثبت پایدار نتیجه استفاده کنید. هیچ‌یک از این تغییرات به تنهایی «دقیقاً یک بار» بودن اثر خارجی را تضمین نمی‌کند.

## معیارهای قابل مشاهده

### مسیر پیشنهادی یادگیری و انتخاب

| نیاز | مسیر | نمونهٔ اجرایی |
|---|---|---|
| واکنش داخلی به تغییر Aggregate | DomainEvent و handler محلی MediatR پیش از save | `DurableContext.cs` و `OrderPlacedHandler` |
| کار پس‌زمینهٔ همین برنامه | Hangfire و Outbox دیتابیسی Neo | [HangfireOutboxDemo](../samples/HangfireOutboxDemo/README.fa.md) |
| پیام بین سرویس‌ها | قرارداد مستقل، RabbitMQ و MassTransit | [MessagingDemo](MESSAGING.fa.md) |
| حفظ پیام و Saga پس از restart | Bus/Consumer Outbox و Saga در SQL Server | [DurableMessagingDemo](../samples/DurableMessagingDemo/README.md) |

در نمونهٔ پایدار، ساخت سفارش `OrderPlaced` را در Aggregate ثبت می‌کند. interceptor پیش از save، handler را اجرا می‌کند. handler با `IPublishEndpoint` همان scope، پیام `StartOrder` را در Bus Outbox همان DbContext قرار می‌دهد. commit بیرونی، سفارش و پیام را با هم پایدار می‌کند. worker پیام را به RabbitMQ می‌دهد و Saga مرحلهٔ رزرو، پرداخت و در صورت رد قطعی پرداخت، آزادسازی رزرو را هدایت می‌کند. consumerها اثر دیتابیسی و پیام پاسخ را با Consumer Outbox ثبت می‌کنند. شکست مداوم آزادسازی به ManualReview می‌رسد؛ اپراتور پس از بررسی می‌تواند compensation را ادامه دهد. نتیجهٔ نامعلوم پرداخت مجوز آزادسازی خودکار نیست.

### مرحلهٔ ۴: اجرای قابل‌بازیابی Hangfire

`AddNeoEfOutbox<TContext>()` قرارداد `IOutboxDeliveryStore` را فعال می‌کند. در مسیر جدید `Requested → Dispatching → Queued → Processing → Processed` است. worker سریع ممکن است پیش از ثبت Queued اجرا شود؛ update شرطی dispatcher اجازهٔ بازنویسی Processing/Processed را نمی‌دهد. JobId صرفاً نشان‌دهندهٔ پذیرش در Hangfire است؛ موفقیت کسب‌وکار با Processed مشخص می‌شود.

خطای enqueue به Retrying می‌رود؛ خطای handler به ExecutionRetrying. حداقل فاصلهٔ retry سی ثانیه، lease پنج دقیقه، timeout اجرایی چهار دقیقه و سقف اجرای handler سه بار است. lease منقضی‌شده قابل بازیابی است؛ قطع روی آخرین تلاش به Failed می‌رود. owner token از ثبت نتیجه توسط worker قدیمی جلوگیری می‌کند. ارسال ممکن است تکرار شود؛ اثر خارجی به idempotency مقصد نیاز دارد. جزئیات migration سه ستون جدید، ثبت DI، اجرای cron و محدودیت context در راهنمای نمونه آمده است.

### پایش و عملیات روزانه

| نشانه | چه چیزی بررسی شود | اقدام مناسب |
|---|---|---|
| Requested قدیمی | اجرای cron، صف outbox، اتصال SQL/Hangfire | بازگرداندن worker و بررسی لاگ dispatch |
| Queued بدون پیشرفت | JobId و وضعیت worker Hangfire | رفع خطای DI یا مصرف‌نشدن صف؛ JobId را موفقیت تلقی نکنید |
| Dispatching/Processing با lease منقضی | تاریخ انقضا، شمارنده، سلامت worker | worker جدید recovery را انجام می‌دهد؛ پس از سقف تلاش، بررسی Failed |
| PendingIdempotency قدیمی | رکورد cache و OutboxId مالک | تطبیق دستی؛ ارسال کورکورانه ممکن است عملیات تکراری بسازد |
| Failed یا ProcessError | آخرین خطا، تعداد تلاش، اثر واقعی کسب‌وکار | رفع علت؛ replay کنترل‌شده با شناسهٔ عملیات قبلی و ثبت دلیل |
| Outbox پیام RabbitMQ رو به رشد | broker، readiness، delivery service و transactionهای SQL | رفع اتصال/قفل؛ پیام‌های commit‌شده را حذف نکنید |
| Saga در ManualReview | وضعیت رزرو، نتیجهٔ قطعی/نامعلوم پرداخت و خطای compensation | ابتدا reconciliation؛ سپس retry تنها وقتی compensation لازم است |

در نمونهٔ Hangfire از `/outbox/{id}` و `/receipts/{id}`، و در نمونهٔ RabbitMQ از `/outbox` و `/state/{id}` استفاده کنید. readiness اتصال حمل‌ونقل با موفقیت یک عملیات کسب‌وکار متفاوت است. برای سیستم واقعی تعداد و سن پیام‌های pending، تعداد Failed، retry و ManualReview را پایش کنید؛ در لاگ‌های dispatch شناسهٔ Outbox و خطا موجود است. شناسهٔ سفارش/عملیات و پیام را در trace/log نگه دارید، نه به‌صورت برچسب دارای تنوع زیاد برای metric.

برای SQL، یک query صرفاً خواندنی روی جدول نگاشت‌شدهٔ `OutboxMessage` با گروه‌بندی `OutboxState` و `MIN(CreateDate)`، تعداد و سن پیام‌ها را نشان می‌دهد. قبل از replay، payload/version قرارداد، JobId، وضعیت اثر مقصد و مالکیت idempotency را بررسی کنید. پاک‌سازی Inbox/Outbox باید بازهٔ replay و سیاست نگهداری کسب‌وکار را رعایت کند؛ پیام خطادار را برای خالی‌کردن داشبورد حذف نکنید.

### MCP، Skill و سناریوهای قابل تکرار

پس از `neo_search_docs` و دریافت baseline، از `neo_get_example` با `durable-messaging-demo` یا `hangfire-outbox-demo` استفاده کنید. نمونهٔ Saga منبع وابستگی `MessagingDemo` را نیز برمی‌گرداند؛ دو پوشه باید کنار هم در checkout بمانند. MCP فقط راهنما و کد را می‌خواند و پیام واقعی منتشر نمی‌کند. Skill `neo-feature` همین انتخاب مسیر و مرز تراکنش را رعایت می‌کند.

آزمون‌های `DomainEventTests`, `QueueDeliveryTests`, `OutboxCoordinationTests`, `OutboxDeliveryTests` رگرسیون‌ها را پوشش می‌دهند. `smoke_durable_messaging.py` و `smoke_hangfire_outbox.py` با کانتینرهای واقعی اجرا می‌شوند؛ آزمون Redis در workflow پیام‌رسانی از Redis واقعی استفاده می‌کند. گزارش نسخه و محدودیت‌های آزمون در [VALIDATION.md](VALIDATION.md) است. این سناریوها جای سیاست deadline برای Saga، مهاجرت قراردادها یا اتصال واقعی بانک را نمی‌گیرند.

### مرحلهٔ ۵: نمونهٔ تراکنشی SQL Server و RabbitMQ

[DurableMessagingDemo](../samples/DurableMessagingDemo/README.md) مسیر واقعی DomainEvent → handler محلی → Bus Outbox همان DbContext → RabbitMQ → Saga دیتابیسی → Consumer Outbox را نشان می‌دهد. این نمونه از نمونهٔ حافظه‌ای قبلی جداست. rollback تراکنش بیرونی باید سفارش و پیام را با هم حذف کند؛ خاموش‌بودن delivery و restart باید پیام commit‌شده را حفظ کند. جزئیات اجرا، دو worker و بازیابی کامپنسیشن در README نمونه آمده است.

### مرحلهٔ ۳: کلید تکرار و lease

پیام دارای کلید idempotency ابتدا با وضعیت `PendingIdempotency` ذخیره می‌شود. تنها پس از گرفتن کلید به `Requested` می‌رود؛ درخواست بازنده قبل از برگرداندن نتیجهٔ برنده، رکورد خودش را از dispatch خارج می‌کند. قطع فرایند بین ذخیرهٔ پیام و گرفتن کلید ممکن است رکورد pending بسازد؛ این وضعیت برای بررسی و reconciliation حفظ می‌شود و خودکار ارسال نمی‌شود. برای ثبت اتمیک کسب‌وکار و پیام، از مسیر تراکنشی همان دیتابیس استفاده کنید.

`AddNeoOutboxWithRedis` یک store مبتنی بر `SET NX` و قفل توکن‌دار ثبت می‌کند. اتصال از `ConnectionStrings:Redis` خوانده می‌شود. `AddNeoOutboxWithMongo` نیز برای قفل توزیع‌شده به این اتصال نیاز دارد. سازندهٔ هر دو کلاس `RedisDistributedLock` اکنون `IConnectionMultiplexer` می‌گیرد؛ `IDistributedCache` عملیات لازم برای قفل اتمیک را ندارد.

provider حافظه‌ای فقط بین سرویس‌های متصل به همان `IMemoryCache` و همان پردازش اتمیک است. `IdempotencyStoreWithCacheService` یک cache فاقد `IAtomicCacheService` را رد می‌کند. نام `AddNeoOutboxWithCatch` برای سازگاری باقی مانده؛ آن را فقط با MemoryCacheService یا provider واقعاً اتمیک استفاده کنید.

پارامتر `timeout` قفل، مدت اعتبار lease است. عملیات باید در این بازه پایان یابد؛ توقف طولانی پردازش، قطع شبکه و پایان lease به کنترل idempotency/fencing عملیات کسب‌وکار نیاز دارد. آزادسازی فقط با توکن مالک انجام می‌شود؛ مالک قدیمی قفل جدید را حذف نمی‌کند. providerها خطای اتصال را به caller منتقل می‌کنند.

کلیدهای Redis/cache جدید با namespace مستقل، نام کامل قرارداد و هش کامل ورودی تفکیک‌شده ساخته می‌شوند. هنگام مهاجرت از cache قدیمی، تا پایان بازهٔ replay پیام‌های قبلی را reconcile کنید؛ تغییر فرمت کلید نباید باعث فرض نادرست دربارهٔ سوابق جلوگیری از تکرار شود. فرمت قدیمی Mongo برای سازگاری این مرحله تغییر نکرده است.

### مرحلهٔ ۲: صف Hangfire و خروجی command

متدهای `IJobExecuter` اکنون از client تزریق‌شده استفاده می‌کنند و متد async واقعی را با آرگومان‌های قابل‌سریال‌سازی ذخیره می‌کنند. token زمان enqueue در آرگومان job به `CancellationToken.None` تبدیل می‌شود؛ Hangfire هنگام اجرا token مربوط به worker را جایگزین می‌کند. نام صف و continuation حفظ می‌شوند.

صف‌های پیش‌فرض Hangfire شامل `default` و `outbox` هستند. اگر `Hangfire:Queues` را صریح تنظیم کرده‌اید، worker مسئول Outbox باید صف `outbox` را مصرف کند. اتصال، نوع storage، تعداد worker و نام صف هنگام ثبت اعتبارسنجی می‌شوند.

برای خروجی command دارای `IEventContainer`، ثبت اختیاری `services.AddNeoCommandEventPublishing()` را اضافه کنید. هر رویداد فقط پس از دریافت JobId از فهرست pending حذف می‌شود؛ خطا یا executor بدون خروجی به caller برمی‌گردد. این مسیر تراکنشی نیست؛ فاصلهٔ بین enqueue و حذف از حافظه همچنان امکان تحویل تکراری دارد. رویداد بیرونی مهم را از Outbox همان تراکنش بفرستید.

### مرحلهٔ ۱: قرارداد رویداد دامنه

`IDomainEventEntity` اکنون متدهای `AddDomainEvent` و `RemoveDomainEvent` را الزام می‌کند؛ `BaseEntity` هر دو را دارد. اگر Entity سفارشی مستقیماً این interface را پیاده کرده است، این دو متد را روی collection واقعی خود پیاده کنید. این تغییر قرارداد در زمان کامپایل مشخص می‌شود و جایگزین رفتار بی‌اثر قبلی `AddDomainEvents` است. در command ویرایش نیز می‌توان `DomainEvents` را هنگام ساخت command مقداردهی کرد.

interceptor رویدادهای زنجیره‌ای را تا سقف ۱۰۲۴ رویداد در یک save اجرا می‌کند و cancellation را به handler می‌رساند. رویدادهای ارسال‌شده فقط پس از موفقیت SaveChanges از Entity حذف می‌شوند؛ خطای handler یا دیتابیس آن‌ها را حذف نمی‌کند. نگه‌داشتن رویداد به معنی rollback تغییرات حافظه یا امن‌بودن retry همان DbContext نیست. با تراکنش بیرونی، موفقیت SaveChanges همچنان به معنی commit نیست؛ پیام بیرونی باید در Outbox همان تراکنش بماند.

- رویدادهای ورودی UpdateGenericEntity واقعاً به Entity اضافه شوند.
- cancellation و exception به caller برسد و رویداد اجرا‌نشده بی‌صدا حذف نشود.
- پاسخ HTTP یا JobId به معنی انجام کسب‌وکار نباشد؛ وضعیت enqueue و execution جدا دیده شود.
- دو worker نتوانند هم‌زمان مالک یک lease معتبر باشند؛ مالک قدیمی نتواند قفل جدید را آزاد کند.
- rollback هیچ integration event قابل مصرف تولید نکند؛ پیام commit‌شده با restart ناشر گم نشود.
- نتیجهٔ نامعلوم پرداخت همچنان نیازمند reconciliation باشد؛ کامپنسیشن فقط با تأیید پایان یابد.

مرجع مفهومی: [Domain events در Microsoft](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/domain-events-design-implementation)، [Transactional Outbox در MassTransit](https://masstransit.massient.com/concepts/outbox)، [قفل Redis و مالکیت lease](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/). سازگاری API با نسخه‌های نصب‌شدهٔ همین ریپازیتوری در build و تست کنترل می‌شود.
