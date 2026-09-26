# انتشار Neo.Companion.Cli در NuGet

بستهٔ NuGet این بخش، `Neo.Companion.Cli` و دستور نصب‌شوندهٔ آن `neo` است. MCP و Skillها در ZIP همراه توزیع می‌شوند. انتشار این ابزار، بسته‌های اصلی Neo را منتشر نمی‌کند.

پیش‌نیاز انتشار: Repository secret با نام `NUGET_API_KEY` در ریپازیتوری `SeyediDev/Neo-Framework`؛ کلید NuGet باید اجازهٔ Push برای بستهٔ جدید `Neo.Companion.Cli` را داشته باشد. مقدار کلید را در سورس، خروجی فرمان یا چت قرار ندهید.

Workflow اختصاصی `.github/workflows/companion-nuget.yml` با tag به‌شکل `companion-cli-v0.6.0` اجرا می‌شود. نسخهٔ tag باید دقیقاً برابر Version پروژه باشد. workflow فقط CLI را در Release بسته‌بندی می‌کند، metadata و نصب واقعی دستور را می‌آزماید، nupkg را به‌عنوان artifact نگه می‌دارد و همان فایل را به NuGet.org می‌فرستد. نبود کلید یا رد انتشار، خطای آشکار ایجاد می‌کند؛ از skip-duplicate برای پنهان‌کردن نتیجه استفاده نمی‌شود.

ساخت محلی فایل قابل بررسی:

```powershell
dotnet pack tools/Neo.Companion/src/Neo.Companion.Cli -c Release -o tools/Neo.Companion/artifacts/nuget-release
```

پس از موفقیت انتشار و قابل دریافت‌شدن نسخه در NuGet:

```powershell
dotnet tool install --global Neo.Companion.Cli --version 0.6.0
neo --help
```

صرف ساخته‌شدن فایل nupkg یا وجود tag، اثبات انتشار نیست. نتیجهٔ workflow و قابل دریافت‌بودن نسخه از NuGet باید بررسی شود. راهنمای نصب داخل بسته، checkout سازگار نئو و محدودیت‌های نمونهٔ تولیدشده را مشخص می‌کند.
