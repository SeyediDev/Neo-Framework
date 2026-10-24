# شواهد تحویل پیش‌نمایش UI

تاریخ بررسی: **۲۰۲۶-۱۰-۱۱**. این تحویل، پیش‌نمایش محلی قرارداد و سیاست دسترسی است؛ نسخهٔ Backend یا تحویل سورس ۰.۴.۰ را تغییر نمی‌دهد. تاریخ ارزیابی دادهٔ نمونه برای تکرارپذیری روی **۲۰۲۶-۱۰-۱۰** ثابت است.

## نتیجهٔ مرورگر

اجرای نهایی `node qa.cjs` با **Chrome/155.0.8059.40** و exit code **0** پایان یافت: **۴۷ بررسی موفق، صفر شکست**. گزارش ماشینی در [qa-results.json](evidence/qa-results.json) موجود است؛ شروع `2026-10-11T06:26:41.756Z` و پایان `2026-10-11T06:27:24.292Z` بود.

بررسی‌ها شامل بارگذاری قلم و نشان رسمی، جست‌وجو و ترکیب فیلترها، empty state و بازنشانی، استقلال lifecycle و مشاهدهٔ Gateway، drawer و تب‌های RTL با Home/End، Escape و بازگشت focus، دلیل اجباری تغییر، تعارض revision و تلاش دوباره، escape کردن متن دلیل، دانلود واقعی JSON، استقلال چهار revision و مرجع سیاست، انتشار و بازنشستگی محلی، چهار نتیجهٔ تصمیم، عضویت/نقش/grant، بازهٔ منقضی یا آینده، obligationهای تکمیل‌نشده، نقشهٔ مرز سازمان، رویداد محلی، منوی موبایل و بازخوانی صفحه است.

در viewportهای **۱۴۴۰×۱۱۰۰** و **۳۹۰×۸۴۴**، اسکرول افقی بیرون جدول وجود نداشت. sidebar بستهٔ موبایل از نمایش و مسیر Tab خارج است؛ جدول داخل `table-wrap` اسکرول می‌شود. تصاویر نهایی با مشاهدهٔ بصری بررسی شدند. این بررسی responsive در Chrome است و آزمایش دستگاه فیزیکی یا مرورگرهای دیگر محسوب نمی‌شود.

خطای JavaScript، درخواست خارجی از صفحه و شکست بارگذاری: **صفر**. LocalStorage، SessionStorage و cookie نمونه خالی ماندند. سرور پیش‌نمایش روی loopback `127.0.0.1:4186` پاسخ HTTP **200** داد؛ نمایش آن در پنل Codex درخواست شد. استقرار انجام نشده است.

## تصاویر

| نما | شاهد |
| --- | --- |
| قراردادهای دسکتاپ | [desktop-contracts.png](evidence/desktop-contracts.png) |
| جزئیات دسکتاپ | [desktop-drawer.png](evidence/desktop-drawer.png) |
| تعارض بازبینی | [desktop-revision-conflict.png](evidence/desktop-revision-conflict.png) |
| آزمایشگاه دسکتاپ | [desktop-decision.png](evidence/desktop-decision.png) |
| نقشهٔ اجرا | [desktop-architecture.png](evidence/desktop-architecture.png) |
| قراردادهای موبایل | [mobile-contracts.png](evidence/mobile-contracts.png) |
| جدول موبایل | [mobile-contracts-table.png](evidence/mobile-contracts-table.png) |
| جزئیات موبایل | [mobile-drawer.png](evidence/mobile-drawer.png) |
| آزمایشگاه موبایل | [mobile-decision.png](evidence/mobile-decision.png) |

[خروجی JSON آزمون](evidence/fanasa-contract-policy-demo.json) نیز دادهٔ نمایشی است. متن دلیل شامل ورودی آزمون escaping است و سند تجاری نیست.

دو خروجی موقت `evidence/last-failure.png` و `evidence/downloads.htm` جزو شواهد تحویل نیستند؛ در بخش `excludedFiles` از manifest مشخص شده‌اند. فهرست تحویل شامل ۲۷ فایل سورس، دارایی و شاهد نهایی است.

## مرز معنایی

انتشار محلی، `policyVersion` و بازبینی مدیریتی را تغییر می‌دهد؛ نسخهٔ قرارداد، `configRevision`، مرجع پذیرفته‌شدهٔ سیاست و مشاهدهٔ Gateway ثابت‌اند. نبود تطبیق مرجع سیاست با نسخهٔ جدید، `Indeterminate` می‌دهد. Permit شرایط را توضیح می‌دهد و obligationها هنوز «لازم برای اجرا» هستند. وضعیت ذخیره‌شدهٔ Active با انقضای بازه تغییر نمی‌کند. حدود قراردادی، شمارندهٔ باقی‌مانده نیستند.

آزمایشگاه گزارش واقعی PDP یا per-gate trace نیست. بازنشستگی نمونه با Deny توضیح داده می‌شود؛ میزبان واقعی در نبود snapshot معتبر سیاست می‌تواند Indeterminate بدهد. UI هیچ API، هویت، role/grant، registry، مصرف یا بدهکارسازی واقعی ایجاد نمی‌کند.

## حفظ پایهٔ Backend

پنج فایل زیر پیش و پس از این مرحله با SHA-256 پایهٔ تحویل ۰.۴.۰ برابر بودند. suiteهای .NET این مرحله دوباره اجرا نشدند؛ نتایج **۹۸۹ بررسی موفق** مربوط به [شواهد قبلی ۰.۴.۰](../EVIDENCE.v0.4.0.md) است.

بازبینی مستقل، برابری hash تمام **۵۵ input** نسخهٔ ۰.۴.۰ را نیز تأیید کرد. این بررسی ادعای ثابت‌بودن کل workspace نیست: README محصول و `FabricServices.cs` نسبت به shared snapshot قبلی hash متفاوت دارند و در محدودهٔ کار موازی‌اند. این پیش‌نمایش آن فایل‌ها را ویرایش نکرده است.

| فایل | SHA-256 |
| --- | --- |
| Web DLL، `products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/bin/Debug/net10.0/Fanasa.AccessManagement.Web.dll` | `89E1863FC651961BD187DC040153B9252FB5A3B5D7BE3D7A1AD446590E6B5209` |
| Core DLL، `implementation/src/Fanasa.ContractPolicy/bin/Debug/net10.0/Fanasa.ContractPolicy.dll` | `4C87ACC8FB26E112DD86E5ACBE306B390A881030B7A02D9955426153B84B4841` |
| Receiver، `implementation/pcc/ContractPolicyGatewayContext.cs` | `A83F8A2898B3D811BBB89775FFA51BED6670ECA2B39F6CE52233C9A0F1627308` |
| Gateway fixture، `implementation/tests/Fanasa.ContractPolicy.Gateway.Tests/Program.cs` | `0979C82022A5F7E91D743576E23D0DCB84831EEE0756984CEE5C4AB7B77E3377` |
| Gateway DLL، `implementation/tests/Fanasa.ContractPolicy.Gateway.Tests/bin/Debug/net10.0/Fanasa.ContractPolicy.Gateway.Tests.dll` | `072517E7BFA4D7F39A00A0C97D0DA7709975C48084D97EC7D4772BBE67FDDA8B` |

تمام تغییرات سورس این مرحله در `implementation/ui/` انجام شدند؛ profileهای موقت مرورگر زیر `.build/contract-policy-ui-qa` جزو تحویل نیستند. API عمومی Neo، MCP، Skill و راهنماهای مشترک Backend تحت تأثیر قرار نگرفتند. `node --check` و بررسی whitespace موفق بودند. منبع و hash دارایی‌های رسمی در `assets/provenance.json` و hash فایل‌های تحویل در `manifest.ui-preview.v1.json` ثبت است؛ manifest خودش در فهرست hash قرار نمی‌گیرد.
