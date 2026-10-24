# تحویل PAYG، حساب و صورتحساب — ۲۴ اکتبر ۲۰۲۶

پیاده‌سازی محلی در شاخهٔ `codex/payg-billing-20261024` آمادهٔ بازبینی است. مصرف معتبر backend، کسر اعتبار اتمیک PAYG، صورتحساب ثابت و اصلاح اعتباری محدود، API مالی مجوزدار و نمایش حساب/مصرف Portal به همان Fabric موجود متصل‌اند.

## مبنا و ادغام

- مبنای شروع: `98ecb7582862f724d3ba1e57d12505feb983fc6b`.
- هنگام اجرا، شاخهٔ انتشار با اصلاح وضعیت تاریخچهٔ Portal به `4b3cfbccedac2facb356e3742f60387a5f28bad2` رسید. بسته در کپی ایزوله روی همین commit بازپایه شد؛ اصلاح unavailable در برابر empty حفظ شد. commit پیاده‌سازی آزموده‌شده `6975719073f8dd644d58d715f8c1b81091ba09e8` است؛ commit بعدی فقط مستندات، شواهد و اصلاح harness آزمون را ثبت می‌کند.
- محل کپی: `C:/Users/JavadSeyediPC/.codex/worktrees/payg-billing-20261024/Neo`؛ این کار checkout انتشار در E را ویرایش نکرد.
- commit نهایی، فهرست کامل فایل‌های تغییرکرده و SHA256 آن‌ها در بستهٔ محلی `.build/payg-billing-20261024/handoff/SOURCE-MANIFEST.json` ثبت می‌شوند. فایل‌های تازه و تصاویر نیز در manifest قرار دارند.
- Program مرکزی، FabricServices، registry و فایل‌های Gateway تغییر نکردند. Program پورتال برای اتصال مجاز سمت سرور تغییر کرد.

## رفتار تحویلی

کاربر سازمان، مصرف، ماندهٔ اعتبار، بدهی و صورتحساب‌های خودش را در Workspace می‌بیند. مدیر مالی در Accounting سند دورهٔ پایان‌یافته و اصلاح اعتباری را با مجوز زنده و CSRF ثبت می‌کند؛ Billing به همین صفحه هدایت می‌شود. بررسی شواهد ثبت‌نشده و بازیابی در UsageAccounting موجود می‌ماند.

مصرف از commit معتبر unit.save می‌آید. HTTP موفق، فرم مرورگر و credential کاتالوگ نمی‌توانند مصرف قابل پرداخت بسازند. کسر اعتبار، تغییر کسب‌وکار، receipt و outbox در همان تراکنش‌اند. تکرار همسان فقط یک اثر مالی دارد؛ اختلاف payload یا actor تعارض است.

سند دورهٔ بسته خطوط قیمت تاریخی و ContentHash ثابت دارد. credit-note مستقل، مبلغ سند قبلی را عوض نمی‌کند. رزرو باز پرداخت، ماندهٔ اصلاح و پرداخت دستی را محدود می‌کند. در PAYG، سند صورت مصرف اعتبار است و مطالبهٔ پرداخت دوم نیست.

## شواهد و آزمون‌های مستقل

| مجموعه | نتیجهٔ نهایی |
| --- | --- |
| Release مرکز راهبری | صفر هشدار و خطا؛ manifest میزبان پیش و پس از ساخت نهایی ثابت |
| PaygMetering.Tests | ۴۳ بررسی موفق؛ unit.save واقعی تا گزارش، صورتحساب و حساب |
| BillingLifecycle.Tests | ۸۱ بررسی موفق؛ receipt، دورهٔ بسته، ظرفیت، اصلاح، رزرو و immutable invoice |
| Billing.Api.Tests | ۶۱ بررسی موفق؛ نشست انسانی، tenant، CSRF، grant زنده، precision و keyهای داخلی |
| ConsumerUsage.Api.Tests موجود | ۱۰۶ بررسی موفق روی DLL نهایی |
| Organization.Tests با --financial-regression | ۳۸۶ بررسی موفق؛ حساب، مصرف، درگاه مصنوعی و backup/restore |
| billing-exact.test.cjs | ۹ آزمون موفق |
| schema-check | ۲۷ آزمون موفق |
| مرورگر Accounting با Razor واقعی | ۸ بررسی موفق در ۱۴۴۰، ۳۹۰ و ۳۲۰ پیکسل؛ عدد کامل و retry پس از قطع پاسخ |
| Portal نهایی پس از ادغام | Release صفر هشدار/خطا؛ ۱۲۵ بررسی Portal.Tests، ۱۴ بررسی PageModel، ۶۷ بررسی linked-source اتصال، Node با ۳۱ و ۱۴، و ۷ گروه مرورگر |

مرورگر Portal هر ۷ گروه UI را با صفر خطای صفحه و صفر درخواست خارجی گذراند. در Chrome محلی ۱۵۶، بستن process مرورگر پس از آزمون هنگ کرد؛ یک اصلاح محدود harness و یک تکرار نیز همین محدودیت cleanup را نشان دادند. processهای تأییدشدهٔ متعلق به همین آزمون پاک شدند؛ برای این اجرای مرورگر، exit تمیز runner ادعا نمی‌شود. لاگ هر دو تلاش و cleanup حفظ شده است. این محدودیت مربوط به harness محلی است؛ آزمون‌های مالی و .NET خروجی صفر داشتند.

این شمارها مستقل‌اند و جمع آن‌ها به‌عنوان تست یکتا گزارش نمی‌شود. لاگ‌های ساخت و اجرا زیر `.build/payg-billing-20261024/` نگه داشته می‌شوند. تنها شکست آخرین دور BillingLifecycle، انتخاب fixture کوچک‌تر از حد ظرفیت بود: ۳۰۰۰ ردیف هنوز زیر ۸ MiB می‌ماند؛ با اندازه‌گیری و تغییر فقط دادهٔ تست به ۴۰۰۰، رفتار رد پیش از commit تأیید شد. لاگ شکست و اندازه‌گیری نیز حفظ شده‌اند.

DLL مرکز راهبری در ساخت نهایی کپی ایزوله و کپی استفاده‌شدهٔ هر پنج مجموعهٔ آزمون مالی:

`EA79BA9BAC82966C5CC35E9988E0ED55636A45ED5F9DAC8157BD2D824BF7CD2C`

۱۴۴ ورودی میزبان پیش و پس از rebuild r6 و نیز ساخت نهایی ثابت بودند. پس از r6 فقط JavaScript/CSS کارت مبلغ اصلاح شدند؛ کد C# و Razor مالی تغییر نکردند. ساخت پس از checkpoint، metadata مربوط به Git را تازه کرد و hash باینری جدید ساخت؛ همهٔ مجموعه‌های مالی و مرورگر دوباره با همین باینری اجرا شدند. manifest نهایی هر دو دارایی اصلاح‌شده را ثبت می‌کند.

[شاهد unit.save](../../../../products/Fanasa.AccessManagement/docs/fixtures/payg-billing-2026-10-24/unit-save-e2e.json): نرخ تاریخی v7 برابر ۱۲ IRR، یک charge، گزارش ۱۲، خط صورتحساب ۱۲، موجودی ۸۸ و بدهی صفر. زمان آزمون مصنوعی برای پایان دوره کنترل شده است؛ سند مالی مشتری واقعی نیست.

[نتیجهٔ مرورگر Accounting](../../../../products/Fanasa.AccessManagement/docs/fixtures/payg-billing-2026-10-24/browser-results.json) و تصاویر [۳۲۰](../../../../products/Fanasa.AccessManagement/docs/fixtures/payg-billing-2026-10-24/accounting-320.png) / [۱۴۴۰](../../../../products/Fanasa.AccessManagement/docs/fixtures/payg-billing-2026-10-24/accounting-1440.png) ذخیره و بررسی بصری شدند. آزمون قطع پاسخ، ابتدا ثبت واقعی مصنوعی را انجام می‌دهد، پاسخ را قطع می‌کند، سپس پس از reload همان key/body را تکرار می‌کند؛ فقط یک credit-note و همان hash صورتحساب باقی می‌ماند.

جزئیات Portal و تصاویر در [گزارش اتصال](consumer-report/portal-transport/PAYG-PORTAL-INTEGRATION-REVIEW-2026-10-24.md) است.
DLL نهایی Portal برابر `40FD81778CF39C76EF21A6491CE74ECFFB9E968DD6E738B1D965282314293D8B` و در دو تست دارای Web reference عیناً یکسان است؛ harness اتصال ۶۷ موردی sourceهای مالی را link می‌کند و Web DLL را بارگذاری نمی‌کند.

باینری‌های حفظ‌شده فقط شاهد اجرای محلی‌اند و بستهٔ نصب نیستند. مرکز راهبری هنگام rebase، InformationalVersion/SourceLink مبنای `4b3cfbcc` را ثبت کرد؛ هویت محتوای واقعی آزموده‌شده از manifest شامل ۱۴۴ فایل به دست می‌آید و با کد پیاده‌سازی تحویلی تطبیق دارد. Portal از commit پیاده‌سازی `6975719` ساخته شده است. CI مقصد باید هر دو را روی commit تحویل نهایی از نو بسازد؛ SourceLink باینری محلی مرکزی، مرجع دریافت sourceهای تازه نیست.

## بازتولید

.NET 10، Node و Chrome/Playwright موجود محلی لازم‌اند؛ اسکریپت‌ها چیزی نصب نمی‌کنند. Restore این نوبت از cache موجود و با NuGetAudit=false برای ساخت آفلاین انجام شد. این نتیجه جای CI و audit آنلاین وابستگی‌ها را نمی‌گیرد.

ابتدا پروژهٔ Web را Release بسازید؛ سپس هر پروژهٔ تست را با `--no-restore -p:BuildProjectReferences=false -m:1 -p:UseSharedCompilation=false` بسازید و DLL آن را اجرا کنید. آزمون Organization فقط با `--financial-regression`، و PaygMetering با `--evidence <path>` اجرا شده‌اند. برای Browser اسکریپت `products/Fanasa.AccessManagement/tests/billing-browser-check.cjs` را از root اجرا کنید؛ خودش میزبان loopback با دادهٔ مصنوعی ایجاد و در پایان متوقف می‌کند. دستورهای Portal در گزارش اتصال آمده‌اند.

بستهٔ handoff شامل patch باینری، Git bundle قابل بازیابی، manifest، لاگ‌ها و شواهد است. bundle به commit مبنا نیاز دارد؛ از شاخهٔ محلی نیز می‌توان تغییرات را بازبینی/ادغام کرد. بررسی تعارض و CI محیط مقصد همچنان پیش از انتشار لازم‌اند.

## مرز فعال‌سازی

کاربر صریحاً ارسال توکن انسانی را فقط از سمت سرور به `https://api.fanasa.net.local:443/` مجاز کرد. allowlist دقیق همین origin، بدون redirect، cookie یا fallback است. توکن در browser DTO/storage وارد نمی‌شود؛ vault سمت سرور کوتاه‌عمر و logout پاک‌کننده است.

`ConsumerBilling.Enabled=false` و GatewayOrigin/Audience/Scope خالی مانده‌اند. درگاه و metering واقعی فعال نشده‌اند؛ آزمون‌ها از هویت، کلید امضا و پرداخت مصنوعی استفاده کردند. اتصال مالی به Gateway واقعی، تغییر client/scope/grant، GitLab، SSH، نصب، پرداخت و انتقال پول در این نوبت انجام نشدند.

فعال‌سازی محدود، routing موجود Gateway، provisioning مصوب، CI، backup/restore و پذیرش زنده نزد مالک محیط‌اند؛ [راهنمای چرخهٔ مالی](../../../../products/Fanasa.AccessManagement/docs/BILLING-LIFECYCLE.md) ترتیب و محدودیت‌ها را دارد. مالیات، ERP، payout، نسبت درآمد، صورتحساب قانونی و رزرو عمومی providerهای دیرپاسخ جزو این پیاده‌سازی نیستند.
