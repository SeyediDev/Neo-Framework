# پشتیبان‌گیری و بازیابی

ابزار مدیریتی `tools/FabricRecovery`، یک بسته از `fabric.db` و
`platform-registry.db` می‌سازد. چارت، تاریخچه، عضویت، grant، دفتر مصرف، وضعیت
پرداخت و outbox در Fabric هستند؛ کاتالوگ و مجوزهای توسعه در رجیستری قرار دارند.
این ابزار API وب نیست و رمز یا دسترسی جدیدی ایجاد نمی‌کند.

## تهیه بسته

۱. تمام نمونه‌های Access و هر writer دیگری را متوقف کنید. `--offline` اعلام
صریح اپراتور درباره توقف نویسنده‌هاست؛ ابزار سرویس را متوقف نمی‌کند و توقف
آن را خودکار تشخیص نمی‌دهد. snapshot هر SQLite سازگار است، اما دو فایل مستقل
برای داشتن نقطه بازیابی مشترک نیازمند توقف نویسنده‌ها هستند.

۲. پوشه والد مقصد را روی volume امن با ACL محدود آماده کنید. بسته شامل داده
شخصی و مالی است؛ رمزنگاری volume و نسخه بیرونی باید در زیرساخت فراهم شوند.
در Linux/macOS پوشه تازه با مجوز 700 ساخته می‌شود؛ Windows از ACL والد ارث می‌برد.

۳. مسیرهای واقعی `Fabric:DataDirectory/fabric.db` و `Platform:RegistryPath`
را صریح بدهید؛ ابزار مسیر production را حدس نمی‌زند:

```powershell
dotnet run --project products/Fanasa.AccessManagement/tools/FabricRecovery -- backup C:/data/fabric/fabric.db C:/data/platform-registry.db C:/backups/access-20261006 --offline
dotnet run --project products/Fanasa.AccessManagement/tools/FabricRecovery -- verify C:/backups/access-20261006
```

مقصد باید وجود نداشته باشد و والد آن باید موجود باشد. API backup خود SQLite
استفاده می‌شود؛ کپی ساده فایل اصلی با وجود WAL مجاز نیست. `integrity_check`،
`foreign_key_check` و جدول‌های مورد انتظار بررسی می‌شوند. manifest نسخه، زمان،
اندازه و SHA-256 هر فایل را نگه می‌دارد. پوشه `.partial-...` فقط پس از بررسی کامل
با نام نهایی منتشر می‌شود؛ در شکست، پوشه ناقص برای عیب‌یابی باقی می‌ماند و بسته
معتبر تلقی نمی‌شود. symlink/junction و فایل‌های اضافی در بسته رد می‌شوند.
SHA-256 خرابی را تشخیص می‌دهد؛ امضای دیجیتال یا اثبات اصالت نیست. فقط نسخه‌ای
را بازیابی کنید که منشأ و نگهداری آن قابل اعتماد است.

## تمرین بازیابی

```powershell
dotnet run --project products/Fanasa.AccessManagement/tools/FabricRecovery -- restore C:/backups/access-20261006 C:/data/access-restored-20261006 --offline
```

ابزار هرگز روی پایگاه فعلی restore نمی‌کند. مقصد تازه شامل هر دو DB و manifest
است. قبل از اتصال برنامه، داده‌ها را بررسی کنید؛ سپس مسیرهای اجرا را به مقصد
جدید تغییر دهید:

```text
Fabric__DataDirectory=C:/data/access-restored-20261006
Platform__RegistryPath=C:/data/access-restored-20261006/platform-registry.db
Payments__Zarinpal__Enabled=false
```

بازیابی grant و عضویت، وضعیت زمان backup را برمی‌گرداند. لغو دسترسی‌ها و
offboardingهای بعد از آن باید پیش از بازکردن دسترسی کاربران دوباره اعمال و با
مرجع هویت تطبیق داده شوند. پرداخت‌های بعد از backup را با گزارش زرین‌پال/سرویس
مالی تطبیق دهید؛ درخواست پرداخت جدید را پیش از آن فعال نکنید. داده‌های درگاه
بیرونی با restore محلی عقب نمی‌روند. outbox نیز به وضعیت backup برمی‌گردد؛
مصرف‌کننده بیرونی باید idempotent باشد.

بعد از اولین اجرای برنامه، پوشه بازیابی یک پایگاه زنده است و manifest قدیمی
معیار checksum آن نیست؛ بررسی manifest فقط برای بسته دست‌نخورده انجام می‌شود.
فایل‌های JSON legacy، تنظیمات و secrets میزبان، کلاینت‌های Keycloak، گواهی‌ها
و پایگاه Keycloak در این بسته نیستند و باید مستقل نگهداری شوند.

## اعتبارسنجی

آزمون‌های خودکار شامل بازیابی عضویت و مجوز رجیستری، چارت و snapshot تاریخی،
کیف اعتبار و outbox، داده commit‌شده WAL، رد مقصد موجود، checksum خراب و
path traversal هستند. این آزمون روی داده آزمایشی است؛ تمرین بازیابی محیط
عملیاتی و نگهداری نسخه بیرونی هنوز باید در زیرساخت انجام شود.

شواهد این مرحله: ۱۲۱ آزمون مجموعه در نسخه Release موفق بود؛ build بدون خطا و
هشدار انجام شد. ابزار خط فرمان روی fixture مستقل، توالی backup، verify، restore
و verify مقصد بازیابی‌شده را با exit code صفر گذراند. هیچ پایگاه عملیاتی تغییر نکرد.
