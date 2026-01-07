# مانیتورینگ میکروفرانت - نسخه موقت برای تست

این فایل یک نسخه موقت از مانیتورینگ به صورت میکروفرانت React است که برای تست و ارزیابی ایجاد شده است.

## نحوه استفاده

### 1. اجرای سرویس Next.js

ابتدا باید سرویس Next.js مانیتورینگ را اجرا کنید:

```bash
cd D:\Projects\Neo.Bpms\src\Neo.UI.Monitoring
npm install  # اگر هنوز نصب نشده
npm run dev  # اجرا در پورت 3001
```

### 2. دسترسی به صفحه

بعد از اجرای سرویس Next.js، می‌توانید به صفحه میکروفرانت دسترسی پیدا کنید:

```
http://localhost:5000/Monitoring/Index2
```

یا

```
http://localhost:5000/api/monitoring/Index2
```

### 3. ساختار

- **Index2.cshtml**: View اصلی که میکروفرانت React را embed می‌کند
- **Next.js App**: در `D:\Projects\Neo.Bpms\src\Neo.UI.Monitoring` قرار دارد
- **Endpoint**: در `MonitoringController.Index2()` تعریف شده است

## روش‌های Embed

### روش فعلی: iframe (برای تست)

صفحه از iframe استفاده می‌کند که ساده‌ترین روش برای تست است:

```html
<iframe src="http://localhost:3001" frameborder="0"></iframe>
```

**مزایا:**
- ساده و سریع برای تست
- جداسازی کامل CSS و JavaScript
- نیاز به تغییرات کم در کد

**معایب:**
- محدودیت‌های امنیتی (X-Frame-Options)
- مشکل در ارتباط بین parent و iframe
- مشکل در responsive design

### روش آینده: Script Tag + Mount (پیشنهادی)

برای production، بهتر است از script tag استفاده کنیم:

```html
<div id="monitoring-root"></div>
<script src="/monitoring/static/js/main.js"></script>
<script>
  MonitoringApp.mount('#monitoring-root', {
    apiBase: '/api/monitoring',
    // config options
  });
</script>
```

این روش نیاز به:
1. Build کردن Next.js به صورت standalone bundle
2. ایجاد entry point برای export کردن کامپوننت
3. تنظیم webpack برای Module Federation (اختیاری)

## تنظیمات

### تغییر URL سرویس Next.js

اگر سرویس Next.js در پورت دیگری اجرا می‌شود، در `Index2.cshtml` تغییر دهید:

```javascript
const MONITORING_URL = 'http://localhost:3001'; // پورت مورد نظر
```

### تغییر API Base URL

اگر API در مسیر دیگری است:

```javascript
const API_BASE = '/api/monitoring'; // مسیر API
```

## نکات مهم

1. **CORS**: مطمئن شوید که CORS در Next.js و ASP.NET Core درست تنظیم شده است
2. **X-Frame-Options**: در `next.config.js` باید `SAMEORIGIN` باشد
3. **API Proxy**: در development، Next.js از rewrites برای proxy کردن API استفاده می‌کند
4. **Static Export**: برای production، می‌توان از `npm run export` استفاده کرد

## مراحل بعدی

برای تبدیل به میکروفرانت واقعی:

1. ✅ ایجاد View برای embed (Index2.cshtml)
2. ⏳ ایجاد standalone bundle از کامپوننت React
3. ⏳ ایجاد entry point برای mount کردن کامپوننت
4. ⏳ تنظیم webpack برای Module Federation
5. ⏳ تست در محیط‌های مختلف (Admin Panel قدیم، جدید، API)
6. ⏳ حذف نسخه‌های تکراری

## مشکلات احتمالی

### خطای "Cannot connect to monitoring service"

- مطمئن شوید که Next.js در حال اجرا است (`npm run dev`)
- پورت 3001 را بررسی کنید
- Firewall را بررسی کنید

### خطای CORS

- در `next.config.js` تنظیمات CORS را بررسی کنید
- در ASP.NET Core، CORS را فعال کنید

### خطای X-Frame-Options

- در `next.config.js`، `X-Frame-Options: SAMEORIGIN` را تنظیم کنید
- یا برای development، آن را disable کنید

## تست

برای تست کامل:

1. سرویس Next.js را اجرا کنید
2. سرویس ASP.NET Core را اجرا کنید
3. به `/Monitoring/Index2` بروید
4. بررسی کنید که:
   - صفحه مانیتورینگ نمایش داده می‌شود
   - API calls کار می‌کنند
   - Real-time updates کار می‌کنند
   - UI responsive است

