# بستهٔ آزمایشی React Snapshot LSP 0.1.0

این بسته محلی و آزمایشی است؛ منتشر یا به installer محصول نئو افزوده نشده است. API/MCP جدید ندارد. با همان CLI موجود نئو، یک سند ثابت JSX/TSX/JS/TS را تحلیل می‌کند.

## اجرا

Node 24.12.0 و CLI نئو با پشتیبانی read-only LSP لازم‌اند. پذیرش فعلی روی Windows انجام شده است؛ پشتیبانی سیستم‌عامل‌های دیگر آزموده نشده است. وابستگی‌های همین ابزار شامل TypeScript 5.9.3، @types/react 19.1.0 و csstype 3.2.3 است؛ lockfile نشانی registry و integrity بسته‌های دریافتی را حفظ می‌کند. Node runtime در این بسته وجود ندارد. server نسخهٔ TypeScript متفاوت را هنگام شروع رد می‌کند. نصب یا download ضمنی انجام نمی‌شود.

تنظیم نمونهٔ زیر را در فایل خصوصیِ محل نصب ذخیره کنید؛ workspace، document، executable و arguments را با مسیرهای مطلق مجاز جایگزین کنید. languageId برای JSX برابر javascriptreact، برای TSX برابر typescriptreact، برای JS برابر javascript و برای TS برابر typescript است. line و character صفرمبنا و UTF-16 هستند. readinessProfile باید none باشد؛ roslyn-project فقط برای Roslyn است. timeoutSeconds از ۱ تا ۳۰۰ پشتیبانی می‌شود.

```powershell
dotnet C:\approved\Neo\neo-agent.dll lsp C:\approved\private-react-config.json --allow-server-execution
```

اجرای دستی server نیز با node.exe و مسیر server.mjs روی stdio ممکن است؛ stdout فقط پیام‌های LSP است. arguments اضافی به حالت عادی لازم نیستند. آرگومان دوم اختیاری server مسیر trace خصوصی برای عیب‌یابی است؛ در حالت عادی استفاده نشود و لاگ خام در evidence ارسال نشود.

در پوشهٔ tools/react-lsp، وابستگی‌ها را صریحاً از registry مجاز نصب کنید؛ اجرای server این نصب را انجام نمی‌دهد:

```powershell
npm ci --ignore-scripts --no-audit --no-fund --registry=https://registry.npmjs.org --offline=false
```

تنظیم نمونه، با مسیرهای نیازمند جایگزینی:

```json
{
  "workspace": "C:\\approved\\repository",
  "document": "src/Example.jsx",
  "languageId": "javascriptreact",
  "executable": "C:\\approved\\node\\node.exe",
  "arguments": ["C:\\approved\\Neo\\tools\\react-lsp\\server.mjs"],
  "line": 0,
  "character": 0,
  "timeoutSeconds": 300,
  "readinessProfile": "none"
}
```

## قرارداد و محدودیت‌ها

هر process فقط یک didOpen نسخهٔ ۱ دارد. متن سند ثابت می‌ماند؛ didChange یا تغییر فایل روی دیسک جلسه را نامعتبر می‌کند. textDocument/diagnostic یک گزارش full را در پاسخ همان درخواست، با تحلیل همزمان TypeScript و resultId برابر hash متن، تولید می‌کند. گزارش push بدون نسخه یا گزارش ذخیره‌شدهٔ قبلی مصرف نمی‌شود. hash سند و فایل‌های خوانده‌شده قبل/بعد از تحلیل بررسی می‌شود. تغییر dependency خوانده‌شده نیز خطا می‌دهد. این قرارداد snapshot اتمیک کل filesystem، detection همهٔ تغییرهای resolution یا تحلیل همهٔ فایل‌های پروژه نیست.

تعریف و references از TypeScript Language Service گرفته می‌شوند. کنترل workspace و رد navigation خارجی همچنان در کلاینت نئو باقی است. تحلیل ممکن است کتابخانه‌ها و typeهای نصب‌شده را خارج workspace بخواند؛ process isolation مسئول caller است. server هیچ plugin پروژه، فرمان، ویرایش، save یا اسکریپت build را اجرا نمی‌کند.

برای پروژهٔ دارای tsconfig/jsconfig، گزینه‌ها خوانده می‌شوند. در نبود آن، profile استنباطی allowJs/checkJs/strict/react-jsx/ES2022/node resolution اعمال می‌شود؛ تنظیمات پروژه روی دیسک تغییر نمی‌کنند. diagnostics این profile جایگزین build/lint/test پروژه نیست و ممکن است با تنظیمات ابزار build متفاوت باشد. source متن سند در transport محلی لازم است؛ گزارش CLI source، متن diagnostic و raw log را صادر نمی‌کند.

Language Service همزمان کار می‌کند؛ server در حین تحلیل طولانی تضمین پاسخ فوری به cancellation ندارد. deadline/cancellation و توقف process tree توسط CLI صاحب invocation اعمال می‌شوند. process یا editorهای دیگر نباید متوقف شوند. حلقهٔ انتظار زمانی ثابت برای سالم اعلام‌کردن diagnostics وجود ندارد.

## provenance و مجوزها

package-lock.json پین بسته‌ها و integrity registry را نگه می‌دارد. نسخهٔ archive آزمایش دارای FILES.sha256.json و receipt جداست؛ این فایل‌ها بخشی از checkout ابزار نیستند. این hashها جای امضا یا مالکیت برد نیستند. فایل‌ها و مجوزهای اصلی وابستگی‌ها حفظ شده‌اند: TypeScript — Apache-2.0؛ @types/react و csstype — MIT. مسیر فایل‌های مجوز داخل node_modules هر بسته است.

مبنای API: https://github.com/microsoft/TypeScript/wiki/Using-the-Language-Service-API

آزمون تکرارپذیر داخل بسته با Python 3.12 و Node موجود در PATH اجرا می‌شود. فقط fixtureهای جدا در پوشهٔ .build زیر cwd می‌سازد و مسیر evidence را چاپ می‌کند؛ فایل پروژهٔ اصلی را تغییر نمی‌دهد:

```powershell
python C:\approved\neo-react-snapshot-lsp-0.1.0\tests\test_freshness.py
```

پذیرش آزمایشی قبل از بسته‌بندی شامل پنج اجرای واقعی CLI، تعریف/references بین فایل‌ها، UTF-16 فارسی/emoji، و رد تغییر سند/dependency بود. پذیرش خود بسته در receipt همراه آن ثبت می‌شود. source ابزار در tools/react-lsp محصول قرار دارد؛ به installer یا محیط عملیاتی افزوده نشده و شواهد برد ارسال نشده‌اند.
