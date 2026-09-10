# تله‌متری نئو: از رفتار تا attribute

`ITelementryBehaviour` مرز اجرای یک عملیات را می‌سازد: زمان شروع، Activity، اجرای کد اصلی، وضعیت نهایی و metricها. `TelemetryAttribute` نام و نوع این مرز را روی متد مشخص می‌کند. پروکسی فعال‌شده با `AddScopedWithTelemetry` این metadata را می‌خواند و عملیات را از رفتار عبور می‌دهد.

## روش attribute

در نمونهٔ [ProductLookup.cs](../samples/TelemetryDemo/ProductLookup.cs)، روی متد interface نوشته‌ایم:

```csharp
[Telemetry("catalog", "lookup", ActivityKind.Internal)]
Task<LookupResult?> LookupAsync(LookupRequest request, CancellationToken cancellationToken);
```

ثبت سرویس:

```csharp
services.AddScopedWithTelemetry<IProductLookup, ProductLookup>();
```

فراخوانی باید از interface دریافت‌شده از DI انجام شود. ساخت مستقیم کلاس با new یا فراخوانی داخلی یک متد توسط متد دیگری روی همان target از پروکسی عبور نمی‌کند. اگر attribute روی implementation و interface هر دو باشد، مقدار implementation اولویت دارد.

## روش مستقیم

`ManualProductLookup` در همان فایل، delegate اصلی را به `HandleRequestResponse` می‌دهد. برای عملیات بدون خروجی از `HandleRequest` استفاده می‌کنیم. این روش وقتی مفید است که بخواهی یک بخش مشخص از کد را اندازه‌گیری کنی یا در پروژه‌ای با نسخهٔ قدیمی نئو کار کنی.

هر دو روش نتیجهٔ سرویس را حفظ می‌کنند. موفقیت به نبود خطا وابسته است؛ برگشت null به‌تنهایی شکست نیست. خطا و لغو به فراخواننده می‌رسند؛ در رفتار فعلی، لغو با status خطا و metric شکست ثبت می‌شود.

## چه باگ‌هایی اصلاح شد؟

- ثبت سرویس دوباره پروکسی را فعال می‌کند.
- delegateها با نوع خروجی واقعی متد هماهنگ‌اند؛ Task و Task<T> به object یا Task اشتباه تبدیل نمی‌شوند.
- ValueTask، متدهای همگام، generic، بدون ورودی و ورودی null پشتیبانی می‌شوند.
- توکن لغو از جایگاه واقعی پارامتر پیدا می‌شود و آرگومان دیگری را بازنویسی نمی‌کند.
- پروکسی‌های قبلی از یک مسیر اجرای مشترک استفاده می‌کنند و برای انتظار عملیات async نخ فراخواننده را مسدود نمی‌کنند.
- Stopwatch از ابتدا اجرا می‌شود؛ زمان صفر ناشی از شروع‌نشدن آن رفع شده است.
- اطلاعات تکمیل هر عملیات در context منطقی همان فراخوانی نگهداری می‌شود؛ فراخوانی‌های هم‌زمان اطلاعات یکدیگر را تغییر نمی‌دهند.
- ActivitySource و Meter برنامه در تنظیم OpenTelemetry مشترک می‌شوند و options نیز به DI وصل می‌شود.

تست‌ها نتیجه، خطا، لغو، زمان و metadata را بررسی می‌کنند. نمونه‌ها به پروکسی اصلاح‌شدهٔ نئو متصل‌اند و decorator جایگزین خارج از فریم‌ورک ندارند.

## داده‌ها کجا دیده می‌شوند؟

نمونه با ActivityListener و MeterListener در کنسول کار می‌کند و سرور نمی‌خواهد. در برنامهٔ واقعی، `AddNeoOpenTelementry` را با بخش تنظیمات مرتبط فراخوانی کن؛ ApplicationName باید همان نام منبع و Meter باشد. مقصد OTLP را فقط در صورت استفاده از collector خارجی تنظیم کن.

metricها شامل total، success، failure، duration و inflights هستند. پیاده‌سازی فریم‌ورک request/response را نیز log می‌کند؛ نمونه دادهٔ آزمایشی دارد. اطلاعات محرمانه را در payload یا tagهایی که به لاگ می‌روند قرار نده. شناسهٔ کاربر و correlation می‌توانند تعداد ترکیب‌های tag در metricها را زیاد کنند.

Skill به دستیار روش اضافه‌کردن و بررسی این قابلیت را آموزش می‌دهد. MCP دستورالعمل و نمونهٔ معتبر را برمی‌گرداند. هیچ‌کدام جای collector تله‌متری برنامه را نمی‌گیرند.
