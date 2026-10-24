# خوانندهٔ مستقل گزارش مصرف Portal

`ConsumerUsageHttpReader.cs` بستهٔ سمت سرور و مستقل از اسمبلی مرکزی است. ورودی آن tenant و دورهٔ UTC نیمه‌باز کوتاه‌تر از ۳۶۶ روز است. `IValidatedHumanUsageCredentialProvider` باید توسط مالک Portal پیاده‌سازی شود؛ اعتبارنامه باید به نشست انسانی جاری، client معتبر و audience API مرکزی متصل باشد. این فایل provider واقعی، token exchange، provisioning یا wiring Portal ایجاد نمی‌کند.

توکن فقط روی یک درخواست GET به origin تنظیم‌شدهٔ HTTPS443 ارسال می‌شود. redirect، cookie، retry و fallback سرویس خاموش‌اند. پاسخ حداکثر ۸ MiB و ۱۰٬۰۰۰ ردیف/charge است. خطا اطلاعات مالی را پاک می‌کند و پیام داخلی سرور را بازتاب نمی‌دهد. subject داخل envelope فقط برای binding سمت سرور است؛ قبل از ارسال به مرورگر به مدل نمایش محدود map شود.

آزمون مستقل در `tests/` قرار دارد؛ ۵۷ سناریو و ۳۴۲ بررسی موفق در تحویل ۱۵ اکتبر ثبت شدند. این آزمون‌ها network واقعی یا دادهٔ عملیاتی ندارند.

```powershell
dotnet restore docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/portal-transport/tests/PortalTransport.Tests.csproj --configfile docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/portal-transport/tests/offline-nuget.config --artifacts-path .build/portal-transport-20261015
dotnet build docs/fanasa-platform-control-center/workstreams/usage-accounting/consumer-report/portal-transport/tests/PortalTransport.Tests.csproj -c Release --no-restore --artifacts-path .build/portal-transport-20261015 -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet .build/portal-transport-20261015/bin/PortalTransport.Tests/release/PortalTransport.Tests.dll
```

[تحویل مرکزی و حدود پذیرش](../../../portal-central-readers-2026-10-15.md).
