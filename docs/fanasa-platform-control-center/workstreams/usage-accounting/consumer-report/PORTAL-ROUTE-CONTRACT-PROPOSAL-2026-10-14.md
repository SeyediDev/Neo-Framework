# پیشنهاد اتصال گزارش مصرف Portal

وضعیت: پیشنهاد بررسی‌شده؛ پیاده‌سازی، تصویب scope یا فعال‌سازی عملیاتی نیست. این یادداشت بر بازبینی سورس workspace و بستهٔ consumer-report استوار است، نه ادعای پذیرش نسخهٔ فعال سرور.

مسیر پیشنهادی:

`GET /api/accounting/v1/consumers/{consumerTenantId:guid}/usage-report?from=<UTC>&to=<UTC>`

subject انسانی باید از credential معتبر با audience API مرکزی استخراج شود؛ subject query/header پذیرفته نشود. هویت client Portal جدا بررسی شود. پیش از خواندن، consumer فعال، عضویت فعال subject و grant جاری و منقضی‌نشدهٔ `billing.read` کنترل شود. `platform.catalog` و وجود یک سرویس در کاتالوگ مجوز مالی نیستند. Cookie مدیر حسابداری برای delegation مصرف‌کننده استفاده نشود.

بازه نیمه‌باز UTC برابر `[from,to)`، شرط `from < to` و سقف ۳۶۶ روز پیشنهاد می‌شود. سقف ثابت سرور ۱۰٬۰۰۰ ردیف/۸ MiB است؛ `maxRows` ورودی کاربر نباشد. پاسخ credential نامعتبر ۴۰۱، عدم مجوز ۴۰۳، بازه نامعتبر ۴۰۰ و اختلال transport/storage پاسخ ۵۰۳ بدون جزئیات داخلی باشد. خروجی با `Cache-Control: no-store` برگردد.

قرارداد transport پیشنهادی، مستقل از assembly وب مرکزی:

```json
{
  "subject": "server-side validated subject",
  "report": {
    "consumerTenantId": "uuid",
    "from": "UTC", "to": "UTC", "asOf": "UTC",
    "status": "ready | notRecorded | incomplete | unavailable",
    "basis": "recorded ledger",
    "currency": "string or null", "totalAmount": "exact string or null",
    "items": [{
      "productKey": "string", "meterId": "string",
      "meterVersion": null, "unit": null,
      "quantity": null, "amount": "exact string", "chargeCount": 0
    }]
  }
}
```

مبلغ و quantity به‌صورت رشتهٔ دقیق حفظ شوند؛ فیلدهای legacy نامعلوم null باشند. subject فقط برای binding داخلی transport است و از Bootstrap مرورگر حذف شود. raw ledger، actor، reference، کلیدهای داخلی و diagnostics نمایش داده نشوند. در وضعیت غیر-ready دادهٔ مالی ناقص نمایش داده نشود؛ UI مبالغ، ردیف‌ها، currency و timestamps مالی قبلی را پاک کند. تغییر انتخاب consumer/bازه باید پاسخ‌های قدیمی را رد کند.

شکاف‌های سورس بررسی‌شده: Store فقط در بستهٔ docs قرار دارد و در assembly/DI مرکزی وصل نیست. ثبت پیشنهادی کنار storeهای metering در `FabricServices.cs` با همان Fabric database انجام شود؛ schema/database تازه لازم نیست. Portal از `NotConnectedWorkspaceJourneySource` استفاده می‌کند و `SaveTokens=false` دارد؛ توکن `client_credentials` کاتالوگ، subject انسانی را اثبات نمی‌کند. مسیر واقعی credential انسانی، پیش‌نیاز اتصال است و در این یادداشت ایجاد نمی‌شود.

خوانندهٔ مالی مستقل در Program Portal ثبت و پس از انتخاب consumer در `Workspace.cshtml.cs` فراخوانی شود. شکست مالی فقط خروجی مالی را پاک کند و سرویس‌های مجاز را حفظ کند. DTO کنونی WorkspaceJourney برای بازه، meter/version/count و nullهای legacy کافی نیست؛ quota از قرارداد مالی حذف شود.

هیچ client، scope، grant، محصول، route عملیاتی یا انتشار Portal از طریق این پیشنهاد تغییر نکرده است.
