# آماده‌سازی تنظیمات HR و PIP

`prepare_configuration.py` طرح غیرمحرمانهٔ provider، consumer و source را اعتبارسنجی می‌کند و قطعه‌های تنظیمات .NET و بدنهٔ ثبت منبع غیرفعال می‌سازد. هیچ شبکه، نصب، ثبت هویت، grant، تزریق secret یا فعال‌سازی انجام نمی‌دهد.

از ریشهٔ مخزن اجرا کنید:

```powershell
python -B deploy/fanasa-organization-hr/pip-preflight/prepare_configuration.py --plan <nonsecret-plan.json> --source-name "منبع HR توسعه" --reason "آماده‌سازی اتصال مصوب" --output-directory <new-directory>
```

والد پوشهٔ خروجی باید موجود باشد؛ خود پوشه باید جدید باشد. فایل موجود بازنویسی نمی‌شود. ورودی نامعتبر قبل از ساخت خروجی رد می‌شود. کد خروج موفق صفر و خطا دو است؛ اگر خطای دیسک باعث خروجی ناقص شد، آن پوشه را آمادهٔ استفاده ندانید.

برای تمرین بدون اتصال، طرح `deploy/fanasa-organization-hr/pip-preflight/plan.example.json` را به `--plan` بدهید. میزبان‌ها و شناسه‌های این مثال مصنوعی‌اند و برای نصب واقعی مناسب نیستند. نام و دلیل نیز باید غیرمحرمانه باشند؛ رمز و token را در طرح، فایل خروجی یا آرگومان‌ها نگذارید.

| فایل خروجی | کاربرد |
|---|---|
| `provider.fragment.json` | reader در `Organization:HrAttributes` با مهلت مشاهدهٔ ۶۰ ثانیه؛ Authority/Audience موردنیاز در `Organization:HrApi`. |
| `consumer.fragment.json` | پروفایل OAuth و binding دقیق tenant، endpoint، مرجع secret، CA و IPهای خصوصی مجاز. |
| `source.register.json` | بدنهٔ `RegisterPipSourceCommand` با schema2، شش صفت HR، `enabled=false`، timeout ده ثانیه و سقف ۶۵۵۳۶ بایت. |
| `preparation.json` | روش و path ثبت منبع، runtime مستقل هر طرف، مرجع تزریق secret و مقصدهای خروجی؛ این metadata شاهد نصب نیست. |

این قطعه‌ها appsettings کامل نیستند. آن‌ها را با تنظیمات موجود ادغام کنید و اعضای قبلی آرایه‌های `Readers`، `Profiles` و `Bindings` را حفظ کنید؛ جایگزینی کل آرایه یا override شاخص صفر می‌تواند اتصال دیگری را تغییر دهد. کلیدهای یکتا و همبستگی tenant/reference باید پس از ادغام کنترل شوند. تفاوت Authority/Audience موجود باید بررسی شود؛ آن‌ها را خودکار overwrite نکنید. `Organization:HrApi` همچنان به تنظیم کامل موجود، فعال‌بودن، client قبلی و pin گواهی Gateway نیاز دارد. Kestrel، SSO، مسیر داده و حفاظت‌های فعلی حفظ شوند.

provider و consumer ممکن است دو runtime جدا باشند. تنظیمات OAuth/Transport و secret فقط در مصرف‌کننده نصب می‌شوند؛ تنظیمات HrAttributes متعلق به ارائه‌دهنده‌اند. اگر HR در Kubernetes و مصرف‌کنندهٔ PIP در Web مبتنی بر systemd است، این طرح به‌خودی‌خود نیازی به گسترش egress در Pod ارائه‌دهنده ایجاد نمی‌کند. DNS و دسترسی به هر دو مقصد هویت و Gateway باید از runtime مصرف‌کننده بررسی شوند. CA باید در همان runtime موجود و خواندنی باشد؛ تغییر credential، CA یا binding به restart هماهنگ برای تخلیهٔ cache نیاز دارد.

برای systemd، کلید متغیر محیط فقط حروف ASCII، رقم و underscore می‌پذیرد و با رقم شروع نمی‌شود. نمونهٔ معتبر:

```text
ClientSecretReference: organization_hr_client_secret
Environment key: Pip__Secrets__organization_hr_client_secret
```

این‌ها نام مرجع‌اند، نه مقدار secret. مقدار واقعی از سازوکار محافظت‌شدهٔ استقرار تزریق می‌شود و در قطعه‌های تولیدشده قرار نمی‌گیرد. داخل مرجع secret از `__` استفاده نکنید؛ ASP.NET آن را به `:` تبدیل می‌کند و lookup مرجع اصلی شکست می‌خورد. profile reference با client-secret reference متفاوت است؛ همهٔ ارتباط‌های طرح باید دقیقاً حفظ شوند.

پیش از ثبت منبع زنده، client اختصاصی فعال، subject واقعی سرویس، عضویت tenant، اشتراک فعال و grant محدود `organization.read`، route و mTLS باید برقرار باشند. سپس بدنهٔ `source.register.json` با نشست cookie مجاز و antiforgery به path درج‌شده در `preparation.json` ارسال می‌شود. این ثبت را در tenant آزمون یکپارچه‌سازی انجام دهید؛ سازمان الگو برای ویرایش عادی فقط‌خواندنی است. مسیر تکمیل: ثبت غیرفعال ← آزمون اتصال ← فعال‌سازی با رسید تازه ← sync ← بررسی وضعیت همان شخص ← شبیه‌سازی سیاست. موفقیت آماده‌ساز یا آزمون اتصال به‌تنهایی مجوز دسترسی ایجاد نمی‌کند.

آزمون‌های محلی، از ریشهٔ مخزن:

```powershell
python -B -m unittest discover -s deploy/fanasa-organization-hr/pip-preflight -p 'test_*.py' -v
```

پذیرش زنده به مشاهدهٔ HR→PIP→DB→PDP، رد دسترسی نامجاز و دادهٔ منقضی وابسته است؛ نتیجهٔ محلی جای این شواهد را نمی‌گیرد.
