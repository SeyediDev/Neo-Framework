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
