# اجرای PAYG، حسابداری و Billing — ۲۴ اکتبر ۲۰۲۶

مبنای سورس: `98ecb7582862f724d3ba1e57d12505feb983fc6b`؛ درخت انتشار Neo در آغاز clean بود.
فضای توسعهٔ متصل: `C:/Users/JavadSeyediPC/.codex/worktrees/payg-billing-20261024/Neo`.
فضای آزاد پیش از ایجاد: C حدود 13.60 GiB و E حدود 0.97 GiB؛ buildها روی C می‌مانند.
ورک‌تری‌های موجود متعلق به کارهای PIP/Gateway بودند و استفاده نشدند.

## وضعیت آغاز اجرا و تطبیق با شواهد تاریخی

TASK و تحویل‌های ۱۰ تا ۱۴ اکتبر سابقه‌اند. محدودیت فقط docs با مأموریت تازه جایگزین شده است.
در آغاز این اجرا، ConsumerUsageReportStore، API نسخه۱ و reader/projection پورتال در محصول بودند.
MeasuredUsageStore، outbox، recovery/reconciliation و Zarinpal موجود بازاستفاده می‌شوند.
در مبنای آغاز، مسیر measured سازمان فقط postpaid را می‌پذیرفت؛ نام کیف PAYG به‌تنهایی شاهد پذیرش انتهابه‌انتها نبود.
در مبنای آغاز، Program پورتال provider اتصال‌نیافته داشت و human credential سمت سرور و wiring لازم بود.
Invoice در مبنای آغاز مبلغ دوره داشت اما خطوط تاریخی ثابت و اصلاح اعتباری متصل به charge نداشت.

## شکاف‌های P0 و اجرای کوچک

1. بازکردن PAYG فقط برای snapshot معتبر و حساب/اشتراک سازگار unit.save؛ اثبات اتمی‌بودن mutation و debit در همان Fabric. برای عملیات محلی ثابت، رزرو مستقل لازم نیست؛ unknown remote به replay کسب‌وکار تبدیل نمی‌شود.
2. صدور دوره UTC پایان‌یافته با خطوط قیمت تاریخی ثابت، جلوگیری از تکرار و بستن دوره؛ pending همان دوره مانع صدور؛ credit-note محدود به charge و ظرفیت اصلاح، بدون بازنویسی سند و بدون refund واقعی.
3. API نسخه۱ خواندن حساب محدود با همان مرز انسانی billing.read و API مالی Cookie/CSRF/billing.write؛ مبلغ در انتقال رشته دقیق است.
4. اتصال Portal از Gateway و توکن انسانی واقعی OIDC با نگهداری کوتاه‌عمر سمت سرور؛ client credential کاتالوگ، subject فرم و ذخیره توکن در مرورگر منبع مالی نیستند.
5. صفحه کاربردی حساب، مصرف و صورتحساب؛ تست مصنوعی تکرارپذیر unit.save تا report/invoice و سناریوهای مالی/tenant/قطع.

## مالکیت فایل‌ها در کپی ایزوله

- consumer_fixture: UsageContracts، OrganizationUsageMetering و PaygMetering.Tests.
- ledger_audit: Accounting، BillingInvoiceLifecycle/BillingBalance، OrganizationUsagePriceStore و BillingLifecycle.Tests.
- usage_display: فقط Portal؛ credential/provider، reader/projection، Workspace و تست‌های آن.
- root: API و مرز مشترک مالی، سازگاری گزارش/درگاه، Accounting/Billing UI، wiring لازم، آزمون API و بسته ادغام/runbook.

Program مرکزی، رجیستری و فایل‌های Gateway/Policy تغییر خودسرانه نمی‌کنند. همه تغییرات مشترک با base/hash برای مالک انتشار بسته می‌شوند.
پایان محلی شامل کد، API، UI، آزمون و شاهد است؛ GitLab، نصب، client/scope/grant، پول، نرخ تجاری، مالیات و تسویه واقعی فعال نمی‌شوند.

## نتیجهٔ این اجرا

PAYG اتمیک unit.save، صورتحساب با خطوط تاریخی ثابت، اصلاح اعتباری محدود، API مجوزدار و اتصال مالی Portal به Gateway ثابت در کپی ایزوله پیاده شدند. [تحویل پذیرش](ACCEPTANCE-2026-10-24.md) نتیجهٔ نهایی آزمون‌ها و بستهٔ ادغام را ثبت می‌کند. تنظیمات زنده، دسترسی‌ها و checkout انتشار تغییر نکردند.
