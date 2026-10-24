# شواهد مسیر runtime مصرف

این سند و manifest نسخهٔ ۱ شواهد تاریخی مرحلهٔ اول هستند. وضعیت جاری اتصال
قرارداد/قیمت و تحویل outbox در [شواهد مرحلهٔ دوم](RUNTIME-EVIDENCE.v2.md) و تطبیق و
retry حسابداری در [شواهد مرحلهٔ سوم](RUNTIME-EVIDENCE.v3.md) ثبت می‌شود.

تاریخ: ۱۰ اکتبر ۲۰۲۶. این مرحله به درخواست شروع توسعه، مسیر محلی opt-in در محصول
Access Management را پیاده‌سازی می‌کند. sourceهای اصلی جدید `UsageContracts.cs`،
`OrganizationUsageMetering.cs` و `MeasuredUsageStore.cs` هستند؛ اتصال در
`Accounting.cs`، `AccountingApi.cs`، `OrganizationFabric.cs`، `OrganizationApi.cs`
و `FabricServices.cs` است. تنها تغییر Program، یک فراخوانی در runner آزمون‌هاست؛
Program محصول و persistence/registry/migration در این مرحله تغییر نکردند.

## اجرای واقعی

- build اولیهٔ محصول: صفر warning و error.
- runner کامل محصول: ۲۴۸ check با exit 0، شامل ۷۰ check جدید مصرف.
- schema validator تاریخی: سه UsageEvent instance پذیرفته شدند؛ suite ساختاری ۲۶ test با exit 0.
- sample تاریخی پس از تغییرات runtime: ۶۲ check با exit 0؛ output تازه در
  `sample/results/run-f66384550eb24f21996677a796eed7d2`.
- بررسی whitespace روی diff فایل‌های محصول و runner: exit 0.

فرمان runner محصول:

```powershell
dotnet run --project products/Fanasa.AccessManagement/tests/Organization.Tests -c Release -p:NuGetAudit=false --no-restore
```

log نهایی در [runtime-checks.txt](fixtures/runtime-checks.txt) و راهنمای رفتار و
تنظیمات در [USAGE-ACCOUNTING](../../../../products/Fanasa.AccessManagement/docs/USAGE-ACCOUNTING.md) است.
[manifest سورس runtime](fixtures/runtime-source-baseline.v1.json) ده فایل منتخب و
SHA-256 آن‌ها را پس از اجرای نهایی pin می‌کند؛ manifest کامل dependency/deployment نیست.
package جدیدی برای مصرف اضافه نشده است. `NuGetAudit=false` فقط برای اجرای محلی
و محدودیت شبکه محیط استفاده شد؛ تنظیم audit محصول تغییر نکرد.

اجراهای معمول build به ACL روی cache static-web-assets برخورد کردند؛ اجرای
همان فرمان با دسترسی filesystem برای خروجی‌های محلی موفق شد. بازاجرای sample
ابتدا به اسمبلی‌های تولیدشدهٔ runs قبلی برخورد کرد؛ `sample/.artifacts` و
`sample/results` از ورودی پیش‌فرض compile خارج شدند و اجرای بعدی ۶۲ check گذراند.
نمونه همچنان append پیشنهادیِ تاریخی خودش را آزمایش می‌کند؛ رفتار runtime را
`UsageAccountingTests` با AccountingStore و Fabric واقعی می‌آزماید.

## پوشش و محدودیت

آزمون‌ها strict parser، نرخ تاریخی و تعرفه امروز، event/operation dedup و alias،
تغییر actor/evidence/rate، دو اتصال SQLite و رقابت اعتبار، rollback داخلی و
outer transaction واقعی سازمان، lost acknowledgement و reopen، pending و alias
پس از funding، دوره invoice صادرشده، consumer غیرفعال، و backup/restore دو DB را
بررسی می‌کنند. نگاشت محلی با principal، grant، عضویت، اشتراک، entitlement، مالک
مرکزی و دو نسخه نرخ آزمایش شده است. انقضای نرخ، receipt قبلی را reprice نمی‌کند؛
تعلیق اشتراک/entitlement یا revoke مجوز، خواندن پاسخ قبلی را رد می‌کند.

فعال‌سازی پیش‌فرض خاموش است. نسخه قرارداد/allocation/rate فعلاً projection معتبر
تنظیمات سرور است. شاهد محلی `organization-permission/v1` و `ledger-credit:`،
Decision زنده PDP یا admission Gateway نیست. رویداد wire جدید نمی‌تواند شاهد
invocation داخلی را جعل کند. validator و endpoint producer بیرونی، dispatcher
ack/DLQ، قرارداد امضاشده، تسویه مالک و استقرار انجام نشده‌اند.

تست rollback/reopen به معنی آزمون abrupt process kill، قطع برق یا loss volume
نیست. حفظ commit محلی به معنی RPO صفر بعد از restore یک backup قدیمی نیست؛ تست
بازیابی از eventهای اصلی replay می‌کند. DB و قیمت‌ها و هویت‌های تست مصنوعی‌اند.

Skill/MCP و recipeهای موجود API مصرف runtime ندارند و متأثر نبودند؛ README محصول
و راهنمای runtime همراه کد به‌روز شدند. baseline/hashهای prototype تاریخی حفظ شدند.
