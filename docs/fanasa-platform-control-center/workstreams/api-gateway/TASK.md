# Fanasa API Gateway

شناسه چت: 01a1251e-ad48-74a0-972b-54b92ddbaa1d

مرجع هماهنگی: [تقسیم کار و بودجه پیچیدگی](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/EXECUTION-COORDINATION.md).

این شرح از دستور مستقیم کاربر برای پوشش موازی معماری تهیه شده است. ابتدا این مأموریت را با ابزارها اجرا و خروجی محدود و قابل ادغام تحویل بده.

وظیفه این چت مشخص است؛ همین حالا با ابزارها انجام بده و سؤال کلی نپرس. چت مالک: Fanasa Platform Control Center، شناسه 01a1220e-88e3-73e2-abc0-02b5deef731c. کاربر مستقیم خواسته چت مستقل API Gateway ایجاد و شروع شود. ابتدا E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/ARCHITECTURE.md و GROWTH-AND-DEVELOPMENT-PLAN.md و API-GATEWAY.fa.md مخزن NeoCluster را بخوان. بسته پایلوت حداقلی APISIX برای یک محصول/یک API بساز؛ فقط fanasa-platform-workstreams/api-gateway/ در NeoCluster را ویرایش کن. خروجی: route config قابل اعتبارسنجی، نمونه/قرارداد PEP adapter که Decision داخل پاسخ PDP و obligations را بررسی کند، audience/scope/trusted tenant، TPS/burst/quota allocation مشترک با Redis، راه عدم bypass، failure mode و rollback، و آزمون لازم دو route از یک allocation بدون تکثیر سهمیه/بدون reset با policyVersion. multi-replica را در fixture تست کن و نصب زنده را فرض نکن. Gateway عمومی با Fanasa.AgentGateway خصوصی متفاوت است. از pipeline و فایل‌های مشترک/Program.cs/registry/policy/ledger جدا بمان؛ چت «از گیت تا دپلوی» مالک pipeline است. Keycloak/PCC/Portal/product موجود حفظ شوند؛ PDP و Accounting سرویس مستقل تازه نشوند، Temporal/mesh/broker تازه به این مرحله نیاید. قرارداد projection/decision متعلق به چت Fanasa Contract & XACML (01a1251f-7d8f-71f2-9956-1da693f49969)، schema Usage متعلق به Fanasa Usage & Accounting (01a1251f-dd42-7f51-b64c-7331c4fa7169) است. با fixture مستقل ادامه بده و پیشنهاد wiring مشترک را قابل بررسی تحویل بده؛ به نبود پاسخ چت دیگر متوقف نشو. اول read/source evidence و سپس فایل اختصاصی و تست/اعتبارسنجی، بعد گزارش مسیر فایل و پیشرفت واقعی.  این کار محلی مجاز و قابل برگشت است؛ deployment یا دسترسی/هزینه تازه از آن نتیجه نگیر.

وضعیت ثبت: چت ایجاد و پیام ارسال شده؛ تا ثبت اولیه، شروع کار با شاهد ابزار/خروجی تأیید نشده است.

