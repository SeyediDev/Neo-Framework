# زرین‌پال

درگاه v4 برای شارژ کیف Pay as you go و تسویه کامل/جزئی صورتحساب Postpaid
پیاده شده است. مبلغ صحیح **ریال (IRR)** است؛ تبدیل تومان انجام نمی‌شود.
کاتالوگ و حساب چندارزی باقی هستند، اما پرداخت این درگاه فقط برای حساب ریالی است.

## فعال‌سازی

پیش‌فرض درگاه خاموش است. تنظیمات خصوصی محیط:

```text
Payments__Zarinpal__Enabled=true
Payments__Zarinpal__Sandbox=true
Payments__Zarinpal__MerchantId=<merchant UUID>
Payments__Zarinpal__CallbackUrl=https://<public-host>/api/payments/zarinpal/callback
Payments__Zarinpal__MinimumAmount=1000
Payments__Zarinpal__MaximumAmount=1000000000
```

Sandbox فقط در Development مجاز است؛ پایگاه آزمایشی باید از داده واقعی مستقل
باشد. برای production مقدار Sandbox=false و Merchant ID واقعی پذیرنده لازم است.
Callback باید HTTPS با مسیر دقیق بالا، بدون query/fragment و از اینترنت قابل
دسترسی باشد. host خصوصی `fanasa.net.local` برای بازگشت عمومی بانک کافی نیست؛
پذیرنده باید دامنه عمومی و تنظیمات مورد تأیید زرین‌پال را آماده کند. هیچ TLS
validation غیرفعال نشده است. مقادیر پذیرنده در مخزن ثبت نشوند.

## گردش پرداخت

- `POST /api/payments/zarinpal/tenants/{tenantId}` با `requestId`, `amount`,
  `invoiceId` اختیاری؛ billing.write و CSRF لازم است.
- شناسه درخواست به tenant، عامل، مبلغ و صورتحساب متصل است. تکرار آن، درخواست
  جدیدی در درگاه ایجاد نمی‌کند. مبلغ و مرجع حساب قبل از تماس بیرونی ذخیره می‌شوند.
- زرین‌پال authority می‌دهد و رابط حساب کاربر را به درگاه می‌برد.
- callback عمومی، paymentId و authority ذخیره‌شده را تطبیق می‌دهد؛ `Status=OK`
  به‌تنهایی اعتبار ایجاد نمی‌کند. verify سروری با مبلغ ذخیره‌شده اجرا می‌شود.
- کد 100 یا 101 همراه ref_id مثبت پذیرفته می‌شود. مرجع تکراری برای دو پرداخت
  رد می‌شود. تأیید پایدار می‌شود؛ ثبت credit/payment، وضعیت paid و outbox در
  یک تراکنش قرار دارند. بازگشت تکراری اعتبار یا تسویه دوباره ایجاد نمی‌کند.
- `GET` مسیر tenant فقط وضعیت‌های همان حساب را نشان می‌دهد؛ merchant و authority
  در لیست نمایش داده نمی‌شوند. `POST .../{paymentId}/retry` با billing.write و
  CSRF تأیید/ثبت را دوباره اجرا می‌کند. پرداخت verified از مرجع پایدار استفاده
  می‌کند و برای هر retry دوباره به سرویس بیرونی وابسته نیست.

شماره کارت، card_hash، mobile و email دریافت یا ذخیره نمی‌شوند. مرجع مالی در
دفتر با sandbox/live مشخص می‌شود. outbox پایدار است؛ dispatcher بیرونی ندارد.

## تطبیق و محدودیت‌ها

Timeout درخواست اولیه وضعیت requesting را حفظ می‌کند؛ ممکن است درگاه درخواست
را ایجاد کرده باشد، بنابراین retry شبکه خودکار یا ایجاد authority دوم انجام
نمی‌شود. این وضعیت نیازمند تطبیق با گزارش پذیرنده است. وضعیت NOK از query عمومی
باعث آزادکردن رزرو صورتحساب نمی‌شود، چون قابل جعل است. درخواست‌های باز Postpaid
مانده صورتحساب را رزرو می‌کنند. استعلام سروری پیاده شده است؛ درخواست ابطال یا
refund به پذیرنده ارسال نمی‌شود. رزرو برای درخواست بدون authority هنوز
نیازمند تطبیق پذیرنده است؛ پرداخت جدید
را قبل از بررسی درخواست نامشخص ایجاد نکنید.

`POST .../{paymentId}/reconcile` با billing.write و CSRF، استعلام سروری را اجرا
و وضعیت درگاه، زمان، عامل و outbox ممیزی را ثبت می‌کند. PAID/VERIFIED همچنان
باید از verify با مبلغ ذخیره‌شده عبور کنند. IN_BANK و FAILED اعتبار ایجاد
نمی‌کنند و رزرو را نگه می‌دارند. فقط REVERSED برای درخواست pending بدون ref_id
رزرو را آزاد می‌کند. بازگشت جعلی یا retry نمی‌تواند درخواست reversed را شارژ کند.
REVERSED برای پرداخت verified/paid سابقه مالی را تغییر نمی‌دهد و برای تطبیق مالی
نمایش داده می‌شود. این مسیر هیچ درخواست refund/reversal به بانک ارسال نمی‌کند.

اگر پرداخت تأیید شد ولی حساب به علت تسویه دستی هم‌زمان یا غیرفعال‌شدن tenant
آن را نپذیرفت، وضعیت verified و ref_id محفوظ می‌مانند. تطبیق مالی/بازپرداخت
باید توسط مسئول مالی انجام شود؛ سامانه پول را خودکار برگشت نمی‌زند.
مدل/ارز حساب دارای پرداخت باز قابل تغییر نیست.

دکمه‌های «شارژ/تسویه تأییدشده» مسیر ثبت دستی برای مسئول دارای billing.write
هستند؛ اتصال بانک فقط از بخش زرین‌پال و verify سروری انجام می‌شود.
کارمزد، refund/reversal، webhook امضاشده، تسهیم و صورتحساب قانونی پیاده نشده‌اند.

## شواهد

درخواست بدون اطلاعات شخصی به sandbox رسمی با Merchant UUID آزمایشی، کد 100 و
authority برگرداند؛ هیچ پرداخت بانکی واقعی انجام نشد. مجموعه آزمون محلی با
gateway بدل، wire contract، retry، جعل authority، کد 101، ماندگاری، تسویه جزئی
و خطای ثبت محلی را بررسی می‌کند. مجموعه اکنون ۱۰۶ بررسی موفق دارد و وضعیت‌های
IN_BANK/FAILED/REVERSED، بازیابی پرداخت با verify و حفظ سابقه مالی پس از برگشت
را پوشش می‌دهد. پرداخت واقعی و بازگشت عمومی end-to-end تا
تأمین Merchant ID و دامنه عملیاتی تأیید نشده‌اند.

منابع رسمی:

- [راهنمای اتصال](https://www.zarinpal.com/docs/paymentGateway/connectToGateway)
- [محیط آزمایشی](https://www.zarinpal.com/docs/paymentGateway/sandBox)
- [واحد پولی](https://www.zarinpal.com/docs/paymentGateway/moreFeatures/currency)
- [استعلام وضعیت](https://www.zarinpal.com/docs/paymentGateway/otherMethods/Inquiry)
