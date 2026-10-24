# بازبینی قرارداد معرفی سرویس Portal

وضعیت: پیشنهاد؛ route/scope قطعی یا فعال نیست. بازبینی سورس workspace انجام شده است و این متن ادعای بررسی نسخهٔ فعال Portal نیست.

در سورس بررسی‌شده route `published-services` وجود ندارد؛ سند SERVICE-DISCOVERY مسیر `DiscoveryPath` را خالی و سند انتشار وضعیت `NotConfigured` را ثبت کرده است. endpoint موجود `GET /api/platform/products?subject=...` تحت PlatformService/`platform.registry`، client توسعه‌دهنده و `developer.read` روی tenant خود محصول فیلتر می‌شود. این رفتار قرارداد معرفی عمومی سرویس یا entitlement مصرف‌کنندهٔ انتخاب‌شده را تأمین نمی‌کند.

adapter Portal مسیر `application-tenants` و پارامتر tenant برای products می‌خواهد؛ provider بررسی‌شده اولی را ندارد و دومی را مصرف نمی‌کند. اتصال معرفی سرویس به‌تنهایی این شکاف مستقل را رفع نمی‌کند.

پیشنهاد حداقلی:

`GET /api/platform/published-services`

سیاست خواندنی مستقل با scope پیشنهادی `platform.services.read`، بدون subject/tenant ورودی، فقط محصولاتی که وضعیت انتشارشان صریحاً در Registry مرکزی ثبت و audit شده باشد. scope هنوز provision یا تصویب نشده است.

DTO پیشنهادی: `{ productId, key, displayName, center, description, detailsUrl?, requestAccessUrl?, published:true }`. productId همان شناسهٔ Registry است. URL ورود، repository، owner، tenant، attributes و clients در پاسخ معرفی عمومی نباشند. لینک درخواست دسترسی تا وجود جریان واقعی null بماند؛ مقدار فرضی برای acquisition ایجاد نشود.

ثبت محصول به‌تنهایی انتشار، entitlement یا grant نیست. authorized-products برای consumer باید جداگانه اصلاح و پذیرفته شود. `platform.catalog` نیز مجوز `billing.read` ایجاد نمی‌کند. هیچ تنظیم Portal، client، scope، grant یا انتشار عملیاتی در این بازبینی تغییر نکرده است.
