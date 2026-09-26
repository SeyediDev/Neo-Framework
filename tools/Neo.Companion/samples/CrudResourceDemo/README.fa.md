# نمونهٔ CRUD با همزمانی و تراکنش مشترک

پیش‌نیاز: .NET 10 و همین checkout نئو. از ریشهٔ ریپازیتوری اجرا کنید:

```powershell
$env:DemoToken = [guid]::NewGuid().ToString('N')
dotnet run --project tools/Neo.Companion/samples/CrudResourceDemo
```

برنامه روی `http://127.0.0.1:5092` اجرا می‌شود و SQLite محلی `neo-crud-demo.db` را می‌سازد. مقدار DemoToken را هنگام بازبودن همین ترمینال نگه دارید. خواندن عمومی است؛ ایجاد، ویرایش و حذف به `Authorization: Bearer <DemoToken>` نیاز دارند. نبود توکن، دسترسی نوشتن را باز نمی‌کند. این احراز هویت صرفاً آموزشی است؛ در برنامهٔ واقعی از ارائه‌دهندهٔ هویت خود استفاده کنید.

در ترمینال دیگری با همان مقدار توکن:

```powershell
$headers = @{ Authorization = "Bearer $env:DemoToken" }
$base = 'http://127.0.0.1:5092/products'
$item = Invoke-RestMethod $base -Method Post -Headers $headers -ContentType 'application/json' -Body '{"name":"Book","persianName":"کتاب"}'
Invoke-RestMethod "$base/$($item.id)"
Invoke-RestMethod "$base/?pageSize=10&sort=name&filters[name]=Book"
$body = @{ data = @{ name = 'New book'; persianName = 'کتاب جدید' }; expectedVersion = $item.version } | ConvertTo-Json
$updated = Invoke-RestMethod "$base/$($item.id)" -Method Put -Headers $headers -ContentType 'application/json' -Body $body
# تکرار PUT قبلی: 409 با code=stale_version
# حذف با آخرین نسخه:
Invoke-RestMethod "$base/$($item.id)?expectedVersion=$($updated.version)" -Method Delete -Headers $headers
```

ایجاد، envelope شامل `id`، `version` و `data` و هدر Location قابل خواندن برمی‌گرداند. تغییرات محصول و ترجمهٔ فارسی و Outbox در یک تراکنش‌اند. حذف، hard delete است و ترجمه با FK پاک می‌شود. `PersianName=null` در ویرایش، ترجمه را حذف می‌کند. read DTO عمداً تنها اطلاعات محصول را برمی‌گرداند؛ برای نمایش چندزبانه، projection مخصوص برنامه را اضافه کنید.

برای مشاهدهٔ rollback، برنامه را با `$env:DemonstrateRollback='true'` دوباره اجرا و POST کنید: پاسخ 400 است و هیچ‌کدام از سه رکورد ذخیره نمی‌شوند. پس از آزمایش این متغیر را پاک کنید. Outbox این نمونه فقط **ثبت** می‌شود؛ consumer یا worker ندارد. برای ارسال واقعی و handler پیام، نمونهٔ `HangfireOutboxDemo` یا `DurableMessagingDemo` را ببینید؛ `ProductChanged` ثبت‌شده قرارداد آموزشی این نمونه است و نباید بدون handler به worker معرفی شود.

## حالت بدبینانه

یک SQL Server آزمایشی آماده کنید. ConnectionStrings__Demo باید دیتابیسی با نام شروع‌شونده با `NeoCrudDemo` باشد؛ credentials را از محیط یا secret store بگیرید:

```powershell
$env:Concurrency = 'Pessimistic'
# $env:ConnectionStrings__Demo = connection string دیتابیس آزمایشی خودتان
dotnet run --project tools/Neo.Companion/samples/CrudResourceDemo
```

این حالت با SQLite اجرا نمی‌شود. برای بازگشت، Concurrency و ConnectionStrings__Demo را از محیط پاک کنید. این نمونه برای دیتابیس خالی از EnsureCreated استفاده می‌کند؛ برنامهٔ واقعی به migration و backfill ستون Version نیاز دارد. تراکنش فقط تا پایان درخواست قفل را نگه می‌دارد؛ فرم قدیمی در هر دو حالت رد می‌شود.

## Doctor بدون نوشتن در دیتابیس

```powershell
dotnet run --project tools/Neo.Companion/samples/CrudResourceDemo -- --doctor
dotnet run --project tools/Neo.Companion/samples/CrudResourceDemo -- --doctor --check-connectivity
```

Doctor از scope تازه DI استفاده و مدل EF، تنظیم CRUD و policyها را بررسی می‌کند؛ گزینهٔ دوم فقط اتصال را نیز می‌آزماید. خروجی JSON و exit code صفر برای نتیجهٔ سالم است. Doctor schema فیزیکی را با migration تطبیق نمی‌دهد، درخواست CRUD اجرا نمی‌کند و دیتابیس نمی‌سازد. سازنده‌های سرویس‌های برنامه هنگام resolve اجرا می‌شوند؛ مسئولیت side effect آن‌ها با برنامه است. گزارش به endpoint عمومی متصل نشده است. ابزار MCP `neo_diagnose_project` همچنان تحلیل استاتیک است و این کد را اجرا نمی‌کند.

جزئیات قرارداد و محدودیت‌ها: [راهنمای CRUD](../../docs/CRUD-RESOURCES.fa.md).
