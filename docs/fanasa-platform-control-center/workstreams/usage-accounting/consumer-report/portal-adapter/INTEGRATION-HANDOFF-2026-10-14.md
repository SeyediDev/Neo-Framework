# تحویل اتصال مصرف Workspace

تاریخ: ۱۴ اکتبر ۲۰۲۶. بررسی source و hashها در `2026-10-14 08:30:06 UTC`.
شواهد آزمون ۱۳ اکتبر [مستقل حفظ شده‌اند](EVIDENCE-2026-10-13.md)؛ بررسی امروز
اجرای دوبارهٔ آزمون یا پذیرش اتصال مالی زنده نیست.

## تحویل و مالکیت

با مجوز قبلی کاربر برای هماهنگی APIها، ابزار ارسال پیام دریافت درخواست را
برای هر دو چت تأیید کرد:

| چت | شناسه | نیاز تحویل‌شده |
| --- | --- | --- |
| Fanasa Platform Control Center | `01a1220e-88e3-73e2-abc0-02b5deef731c` | route/DTO خواندن مالی محدود، اثبات subject انسانی و محل wiring همان Fabric |
| پنجره واحد | `01a0feb9-0662-7141-95e5-7faaaa9ba45c` | بازبینی mapper/renderer و تطبیق با مدل و نمایش Workspace |

تأیید ابزار ارسال، تأیید قرارداد یا ادغام نیست. پس از پیگیری محدود، Portal
[بازبینی قرارداد نمایش](../../../../../../products/Fanasa.UnifiedPortal/docs/USAGE-ADAPTER-REVIEW-2026-10-14.md)
را ثبت کرد: بسته برای نمایش محدود هزینهٔ مصرف ثبت‌شده قابل پذیرش است، اما با
DTO فعلی Portal سازگار نیست. نبود نسخهٔ شاخص/تعداد ثبت در مدل قدیمی، شمارش ردیف
به‌جای سرویس و نمایش سهمیه تأیید شدند. ۲۳ آزمون مستقل Node در بازبینی Portal
موفق شدند؛ fixtures و آزمون‌های .NET/مرورگر دوباره اجرا نشده‌اند. نتایج کامل
۱۳ اکتبر مستقل باقی می‌مانند. قرارداد API مالی و اتصال عملیاتی هنوز اعلام نشده‌اند.
مرکز راهبری در حال پیگیری انتشار فرم ثبت سامانه است؛ این تحویل، آن کار را
متوقف نمی‌کند و مجوز انتشار، client، scope یا grant تازه نمی‌دهد.

## وضعیت source در این snapshot

- [AccountingApi](../../../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/AccountingApi.cs)
  حساب را با نشست انسانی و `billing.read` می‌خواند؛ قرارداد delegated report
  برای Portal نیست.
- [FabricServices](../../../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/FabricServices.cs)
  همان Fabric موجود را ثبت می‌کند؛ `ConsumerUsageReportStore` در DI یا compile
  مرکزی هنوز وارد نشده است. APIهای linked سیاست نیز مجوز خاص خود را دارند و
  مجوز خواندن مالی Portal از آن‌ها نتیجه نمی‌شود.
- [WorkspaceJourney](../../../../../../products/Fanasa.UnifiedPortal/src/Fanasa.UnifiedPortal.Web/WorkspaceJourney.cs)
  و [نمایش فعلی](../../../../../../products/Fanasa.UnifiedPortal/src/Fanasa.UnifiedPortal.Web/wwwroot/workspace.js)
  مدل قدیمی مصرف را دارند. provider تولیدی هنوز `NotConnectedWorkspaceJourneySource`
  است. ثبت مستقیم ادمین در ProductRegistration، پیشنهاد عرضه در انتظار بررسی نیست.

## خروجی موردنیاز مالک مرکزی

۱. نام و نسخهٔ route/DTO، ورودی‌های دقیق consumer و دورهٔ UTC، وضعیت‌های خطا و
سقف‌ها مشخص شوند. نام route یا scope فرضی در این تحویل فعال نشده است.
۲. reader باید هویت Portal و subject انسانیِ اثبات‌شده را تطبیق دهد؛ subject
ارسالی از فرم یا header ساده اثبات delegation نیست. consumer فعال، عضویت و مجوز
زندهٔ `billing.read` پیش از خواندن بررسی شوند. `platform.catalog` مجوز مالی نیست.
۳. `ConsumerUsageReportStore` با همان singleton `FabricDatabase` وارد hosting شود؛
بانک، schema یا registry دیگری لازم نیست. mapper نیز خودِ مرز احراز هویت نیست.
۴. پاسخ مجاز با subject، consumer و هر دو مرز درخواست متصل شود؛ مقدار مالی
رشته بماند. وضعیت ناموفق هیچ مبلغ، ردیف یا timestamp پاسخ قبلی را حفظ نکند.

## تغییرات موردنیاز مالک Portal پس از تطبیق قرارداد

| محل | تغییر قابل بررسی |
| --- | --- |
| provider خواندن Workspace | دریافت فقط از قرارداد مالی مجاز؛ تطبیق subject، سازمان و دوره؛ جلوگیری از اعمال پاسخ دیرهنگام به انتخاب تازه |
| مدل مصرف | حفظ `meterId/meterVersion/unit/quantity/chargeCount`؛ null قدیمی به صفر تبدیل نشود |
| مرز قرارداد | DTO انتقال مستقل و نسخه‌دار یا DTO محلی مطابق API مصوب؛ وابستگی Portal به اسمبلی Web مرکزی ایجاد نشود |
| renderer مصرف | استفاده از renderer مستقل یا پیاده‌سازی همین معانی؛ تعداد محصول متمایز، بدون سهمیهٔ حدس‌زده |
| متریک‌های خلاصه | مبلغ و وضعیت از همان گزارش معتبر جدول خوانده شوند؛ خرابی مصرف، خدمات مجاز را حذف نکند |
| heading و اعلان اتصال | نمایش دورهٔ واقعی و status پاسخ؛ اعلان ثابت unavailable هنگام ready باقی نماند |
| آزمون پذیرش | منع نشت پس از تغییر کاربر/سازمان/دوره، منع عدد JSON گرد‌شده، صفر معتبر در برابر گزارش ثبت‌نشده و RTL موبایل |

مالک API هنوز باید قرارداد را اعلام کند. چرخهٔ درخواست مصرف/عرضه نیز قرارداد
مستقل لازم دارد؛ subscription فعال یا ثبت مستقیم محصول جای آن قرار نمی‌گیرد.
بستهٔ آماده، فایل‌های مشترک Portal/PCC، مجوزها یا نسخهٔ مستقر را تغییر نداده است.

## اثر انگشت source

مسیرهای محصول نسبت به root مخزن و مسیر دو فایل مستقل نسبت به همین پوشه‌اند.

| فایل | SHA-256 |
| --- | --- |
| `products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/AccountingApi.cs` | `B3861562F2F1C4C9D076E8ACD495FFC5555209A378AE6A68C1A7CDB1135184E3` |
| `products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Program.cs` | `E0309216DCBE36F0238A39E7895A592967BC1D01E4BA541DB85F3B58600E02C2` |
| `products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/FabricServices.cs` | `0344F73068E29161D5332BFAE3201ADE3E50BDB1E16724DFB263709410817198` |
| `products/Fanasa.UnifiedPortal/src/Fanasa.UnifiedPortal.Web/WorkspaceJourney.cs` | `10E7237B2A7CA05C0415984748197CE9E1F6074913DBB94D07A6AFC16C60C642` |
| `products/Fanasa.UnifiedPortal/src/Fanasa.UnifiedPortal.Web/wwwroot/workspace.js` | `5DEEB00840599C55FD9C4D8DCEA76614E051CB0362295146A9558CD9919EA568` |
| `WorkspaceUsageReportAdapter.cs` | `2674979824C9949F212395834BA91B8FB2038A86E5D52982AFDAF9AC8FD00B11` |
| `usage-report-view.js` | `E13B12AC68A66F48CADFFFB11285B70B29CBA123DDCAF2674D351B4946B5998F` |
