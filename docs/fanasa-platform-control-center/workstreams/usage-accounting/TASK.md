# Fanasa Usage & Accounting

شناسه چت: 01a1251f-dd42-7f51-b64c-7331c4fa7169

مرجع هماهنگی: [تقسیم کار و بودجه پیچیدگی](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/EXECUTION-COORDINATION.md).

این شرح از دستور مستقیم کاربر برای پوشش موازی معماری تهیه شده است. ابتدا این مأموریت را با ابزارها اجرا و خروجی محدود و قابل ادغام تحویل بده.

وظیفه این چت مشخص است؛ همین حالا با ابزارها شروع کن و سؤال کلی نپرس. کاربر در Fanasa Platform Control Center (01a1220e-88e3-73e2-abc0-02b5deef731c) چت مستقل مصرف/Accounting خواسته. ARCHITECTURE.md و GROWTH-AND-DEVELOPMENT-PLAN.md در E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/ و Accounting/PersistentAccessManagement/Fabric موجود را بخوان. فقط docs/fanasa-platform-control-center/workstreams/usage-accounting/ را ویرایش کن. خروجی: schema نسخه‌دار UsageEvent با owner/consumer/product/contract/allocation/operation/rate snapshot، مرز billable یک API، نمونه مصرف idempotent/reconciliation مستقل و آزمون duplicate با payload متفاوت، crash قبل/بعد commit/outbox، timeout unknown outcome و عدم blind replay، تطبیق ledger fixture و RPO/restore. از Ledger و outbox موجود بازاستفاده پیشنهاد بده؛ legacy charge و نسبت70/30 را فعال/مصوب فرض نکن. reservation مالی تنها برای مدل پرداخت واقعاً لازم؛ بدون microservice، DB یا broker تازه. PDP/role/route/Redis limiter را دوباره نساز. schema مصرف مالکیت توست؛ projection/decision چت Fanasa Contract & XACML (01a1251f-7d8f-71f2-9956-1da693f49969) است. تغییر فایل‌های مشترک Program.cs/PersistentAccessManagement/registry/migrations و سند مرجع را انجام نده؛ wiring پیشنهادی قابل بررسی تحویل بده. fixture برای کار مستقل کافی است، نبود قرارداد امضاشده/برد/پاسخ چت دیگر مانع این بسته نیست. deployment/settlement/payout و تغییر دسترسی از این پیام نتیجه نگیر. 

وضعیت تحویل — ۱۰ اکتبر ۲۰۲۶: بستهٔ مستقل UsageEvent 1.0، مرز billable، fixture/reconciliation، sample و wiring پیشنهادی در همین directory ساخته شد. ۶۲ check رفتاری C# و ۲۶ test ساختاری موفق‌اند؛ ledger مصنوعی دو charge و جمع ۳۰ IRR دارد و FabricRecovery/restore/replay آزموده شد. [شواهد](EVIDENCE.md) و [فهرست بسته](README.md) مرجع تحویل‌اند. اتصال runtime، ادغام فایل‌های مشترک، قرارداد امضاشده، settlement/payout و پذیرش مالی عملیاتی از این وضعیت نتیجه نمی‌شود.

