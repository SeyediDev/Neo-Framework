# شروع کار با Neo Companion در ریپازیتوری نئو

نسخهٔ نگهداری‌شونده در همین ریپازیتوری است: `tools/Neo.Companion`. سه Skill در `.agents/skills/neo-feature`، `.agents/skills/neo-telemetry` و `.agents/skills/neo-doctor` قرار دارند. دیگر نیازی به کلون دوم نئو داخل Companion نیست.

## سرور لازم داریم؟

برای این نسخه خیر. MCP با stdio روی رایانهٔ کاربر اجرا می‌شود؛ GitHub سورس را نگه می‌دارد، Actions آن را می‌سازد و تست می‌کند، و ZIP حاصل از Actions یا GitHub Release قابل دانلود است. کاربر فقط برای اجرای MCP بسته‌بندی‌شده به runtime دات‌نت ۱۰ نیاز دارد. برای ساخت و اجرای نمونه‌ها SDK لازم است.

اگر بعداً بخواهیم یک MCP مشترک از راه اینترنت ارائه دهیم، باید یک سرویس HTTP و احراز هویت راه‌اندازی کنیم؛ آن سرویس می‌تواند روی سرویس میزبانی برنامه باشد و الزاماً نیازمند خرید سرور اختصاصی نیست. این مرحله برای نسخهٔ محلی لازم نیست.

## اولین اجرا

در پوشهٔ اصلی ریپازیتوری:

```powershell
dotnet build tools/Neo.Companion/Neo.Companion.slnx -c Release
dotnet test tools/Neo.Companion/tests/Neo.Companion.Tests/Neo.Companion.Tests.csproj -c Release --no-build
dotnet run --project tools/Neo.Companion/samples/TelemetryDemo -c Release --no-build -- attribute
```

باید span با نام `catalog.lookup`، وضعیت موفق و metricهای درخواست را ببینی. نمونهٔ `manual` همان کار را با فراخوانی مستقیم رفتار انجام می‌دهد. [شرح تله‌متری](TELEMETRY.fa.md) تفاوت این دو را توضیح می‌دهد.

نمونهٔ [ProductCatalog](../samples/ProductCatalog) نیز باقی مانده است: از [دامنه](../samples/ProductCatalog/Domain/Product.cs) شروع کن، سپس [handler](../samples/ProductCatalog/Application/CreateProduct.cs)، [ذخیره‌سازی](../samples/ProductCatalog/Infrastructure/CatalogDbContext.cs) و [API](../samples/ProductCatalog/Api/Program.cs) را بخوان.

## کار با Skill و MCP

در یک کلاینت دارای پشتیبانی Skill، `$neo-feature`، `$neo-telemetry` یا `$neo-doctor` را فراخوانی کن. برای پروژه‌های مصرف‌کنندهٔ دیگر، پوشهٔ Skill موردنیاز را داخل `.agents/skills` همان پروژه کپی کن. توضیح نسخه و قرارداد واقعی پروژه باید همیشه مبنای تصمیم دستیار باشد.

MCP پنج ابزار دارد: بررسی اعلان‌های پروژه، جست‌وجوی منابع، گرفتن نمونهٔ محصول، گرفتن دستورالعمل تله‌متری و تشخیص DI/Telemetry. ابزار دستورالعمل تله‌متری برای هر دو حالت manual و attribute نمونهٔ کد و قواعد استفاده را برمی‌گرداند. این MCP به لاگ زندهٔ برنامهٔ تو وصل نمی‌شود و خودش مدل هوش مصنوعی ندارد.

دستور publish و نمونهٔ تنظیم اتصال در [README](../README.md) آمده‌اند. پس از انتشار محلی می‌توانی سرور را از کلاینت با `dotnet` و مسیر مطلق `Neo.Companion.Mcp.dll` اجرا کنی. متغیر `NEO_PROJECT_ROOT` مسیر پروژه‌ای را تعیین می‌کند که ابزار اجازهٔ بررسی آن را دارد.

## نگهداری در GitHub

workflow اختصاصی `.github/workflows/companion.yml` build، تست، بررسی پروتکل MCP و ساخت ZIP را تعریف می‌کند. تا زمانی که تغییرات به GitHub ارسال نشده‌اند، اجرای این workflow در GitHub انجام نشده است. نسخهٔ محلی آمادهٔ اجرا و تست است.

هنگام تغییر قراردادهای نئو، اسکریپت `sync_knowledge.py` را اجرا کن و مستندات/نمونه‌ها را نیز بازبینی کن. CI با گزینهٔ `--check` اختلاف سورس و منابع MCP را پیدا می‌کند. جزئیات بررسی این نسخه در [گزارش اعتبارسنجی](VALIDATION.md) ثبت می‌شود.

راهنمای ابزار تشخیص و تفسیر شاهدها در [Neo Doctor](DOCTOR.fa.md) آمده است.
