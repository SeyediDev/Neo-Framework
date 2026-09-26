# تولید feature با دستور neo

تولیدگر از فایل‌های نمونهٔ آزموده‌شدهٔ CrudResourceDemo استفاده می‌کند: Entity، DTOهای مستقل، تعریف منبع، کنترلر، ترجمه و ثبت Outbox در تراکنش مشترک، احراز هویت آموزشی و Doctor. خروجی یک پروژهٔ مستقلِ قابل اجراست؛ برای افزودن به برنامهٔ موجود، فایل‌ها و DI را با قرارداد همان برنامه تطبیق دهید. تولیدگر خودش build، migration، دیتابیس یا برنامه‌ای اجرا نمی‌کند.

از ریشهٔ checkout نئو:

```powershell
dotnet run --project tools/Neo.Companion/src/Neo.Companion.Cli -- new feature Book --namespace MyApp.Catalog --route books --output ./generated/Books --neo-root .
dotnet run --project generated/Books -- --doctor
$env:DemoToken = [guid]::NewGuid().ToString('N')
dotnet run --project generated/Books
```

نام و namespace باید شناسهٔ C# با حروف لاتین، بدون keyword یا برخورد با نام‌های قالب باشند. route یک قطعهٔ ثابت lowercase است. output باید **وجود نداشته باشد**؛ پوشه یا فایل موجود هرگز ادغام یا بازنویسی نمی‌شود. symlink و junction در مسیرها رد می‌شوند. NeoRoot باید checkout دارای APIهای جدید باشد؛ وجود آن‌ها در NuGetهای قدیمی فرض نمی‌شود. مسیر مرجع در Directory.Build.props خروجی ثبت شده و می‌توانید هنگام جابه‌جایی پروژه اصلاحش کنید.

## نصب دستور neo از بستهٔ محلی

```powershell
dotnet pack tools/Neo.Companion/src/Neo.Companion.Cli -c Release -o tools/Neo.Companion/artifacts/packages
dotnet tool install Neo.Companion.Cli --version 0.6.0 --tool-path tools/Neo.Companion/artifacts/bin --add-source tools/Neo.Companion/artifacts/packages
tools/Neo.Companion/artifacts/bin/neo new feature Book --namespace MyApp.Catalog --route books --output ./generated/Books --neo-root .
```

این دستور بستهٔ محلی می‌سازد و از همان نصب می‌کند؛ انتشار عمومی NuGet جزو این مرحله نیست. در ZIP آماده، اجرای `dotnet cli/Neo.Companion.Cli.dll new feature ...` نیز همان رفتار را دارد.

README خروجی، ورودی HTTP، توکن runtime، حالت بدبینانه و تمرین rollback را توضیح می‌دهد. در API، `expectedVersion` نسخه‌ای است که کاربر خوانده؛ تعارض را با خواندن خودکار نسخهٔ جدید و تکرار بی‌صدای write حل نکنید. پشتیبانی بدبینانه به SQL Server نیاز دارد. برای استفادهٔ واقعی، identity provider، Scope مالکیت/tenant، migration و worker پیام را متناسب با برنامه تنظیم کنید.

CI روی Windows و Linux خروجی را واقعاً build و اجرا می‌کند؛ مجوز، Location، ویرایش موفق، تعارض نسخه و rollback موجودیت/ترجمه/Outbox بررسی می‌شود. تست‌های قفل بدبینانه جداگانه روی SQL Server واقعی اجرا می‌شوند.
