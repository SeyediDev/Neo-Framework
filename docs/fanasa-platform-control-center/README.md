# Fanasa Platform Control Center

مرجع این چت و مسیر توسعهٔ پلتفرم فن‌آسا — ۱۰ اکتبر ۲۰۲۶.

- [سند نهایی معماری، نسخه ۱٫۰](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/ARCHITECTURE.md): راهبری پلتفرم، OASIS XACML، اثر پارامترهای قرارداد بر Rule/Role/TPS، API Gateway، ثبت و عرضه محصول، فرایند، منابع و اقتصاد مصرف؛ همراه با وضعیت واقعی و ۱۴ تصمیم معماری.
- [برنامه رشد و توسعه](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/GROWTH-AND-DEVELOPMENT-PLAN.md): ۷ فاز، ۳۸ کار با مالک و وابستگی، معیار پذیرش و ۱۲ هدف پیشنهادی قابل اندازه‌گیری.
- [چارت سازمانی، هویت و دسترسی](ORGANIZATION-IDENTITY-ACCESS.md): واحد، سمت، انتصاب، تفکیک نقش سازمانی و نقش محصول، اتصال صریح مجوز و چرخه ورود/انتقال/خروج.
- [API اتصال منابع انسانی](pilot/HR-API-INTEGRATION.md)، [OpenAPI](pilot/hr-api.openapi.yaml) و [تنظیمات نمونه خاموش](pilot/hr-api.settings.example.json): ثبت و خواندن چارت نسخه‌دار از مسیر Gateway با هویت service account، TLS و replay قابل کنترل.
- [پذیرش محلی HR](pilot/hr-api-verification.json) و [بررسی فقط‌خواندنی توسعه](pilot/hr-api-runtime-readonly.json): ۱۷۱ check محلی موفق؛ Gateway توسعه با GET مجاز `200` و اتصال مستقیم بدون گواهی `403`. [قرارداد JSON منتشرشده](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/wwwroot/contracts/organization-hr-v1.openapi.json) نشانی توسعه و نمونه غیرفعال APISIX را جدا معرفی می‌کند.
- [پیش‌نمایش مرکز راهبری](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/wwwroot/previews/control-center.html): پنج بخش تعاملی با سناریوی چارت و دسترسی؛ داده نمایشی است و [کنترل ساختار/محدودیت بررسی بصری](pilot/control-center-preview-verification.json) جدا ثبت شده است.
- [هماهنگی اجرا و کنترل پیچیدگی](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/EXECUTION-COORDINATION.md): محدودهٔ پایلوت، مالکیت چت‌ها و فایل‌ها، شرح مأموریت‌ها و وضعیت واقعی آغاز کار.
- [تطبیق سه بسته و مسیر پذیرش مشترک](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/INTEGRATION-ALIGNMENT.md): یک tuple مصنوعی مشترک، نگاشت Decision/obligations و allocation و مصرف؛ همراه با وضعیت بازیابی اجرای چت‌ها در سند هماهنگی.
- [گزارش نهایی تطبیق محلی](INTEGRATION-REPORT.md): اتصال موفق خروجی‌های واقعی سیاست/Gateway/Accounting، debit یکتا پس از replay و گیت‌های باقی‌ماندهٔ runtime.
- [پیشرفت اجرای پایلوت](PILOT-PROGRESS.md): ۱۲ بررسی موفق native Gateway روی VPS، شواهد انتقال تصاویر و سلامت پس از اجرا، آزمون میزبان PCC و گیت‌های باقی‌مانده.
- [ورودی‌های یک محصول واقعی](pilot/REAL-PILOT-READINESS.md): قرارداد/نرخ/client/grant/allocation معتبر، رسید Gateway و اتصال محدود admission به مصرف موجود.
- [اتصال مرکزی Gateway و PCC](pilot/CENTRAL-GATEWAY-CONNECTION.md): publication و رسید durable، receiver امضاشده در همان میزبان، آزمون‌های اتصال و مرزهای باقی‌ماندهٔ admission و rollout.
- [شاهد پاک‌سازی محلی](pilot/storage-cleanup.json) و [پاک‌سازی VPS](pilot/vps-audio-cleanup.json): حذف ضبط‌های اصلی، قطعه‌ها و مدل‌های مختص رونویسی با مجوز کاربر، همراه با کنترل متن‌ها و گزارش‌های حفظ‌شده.
- [انتقال دانش و سابقه تصمیم‌ها](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/CONTEXT-HANDOFF-2026-10-09.md): زمینهٔ چت قبلی و به‌روزرسانی دستورهای کاربر.

جریان مرجع: **قرارداد → entitlement و policy نسخه‌دار → تصمیم XACML → اعمال در Gateway و محصول → مصرف → حساب و تسویه**.

سند معماری و برنامه، خروجی طراحی‌اند. اجرای زنده و پذیرش عملیاتی هر قابلیت با شواهد مستقل ثبت می‌شوند. درخواست مستقیم کاربر برای تحویل این اسناد، شرط تاریخی انتظار برای پایان مرور همهٔ صوت‌ها را جایگزین کرد. اصل هر شش ضبط پس از مجوز صریح کاربر حذف شد؛ متن‌ها و گزارش‌های موجود حفظ شدند و نبود متن خام اصلی ۱ و ۲ پیش از حذف، در شاهد پاک‌سازی ثبت است.
