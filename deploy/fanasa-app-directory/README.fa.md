# معرفی عمومی سامانه‌های فن‌آسا

این خروجی ایستا در `https://app.fanasacloud.ir/apps/` قرار می‌گیرد. ریشهٔ دامنه همچنان به پنجره واحد هدایت می‌شود. صفحه معرفی ورود نمی‌خواهد؛ احراز هویت و مجوز هر سامانه در همان سامانه اعمال می‌شود. صفحه منو، تب، جاوااسکریپت، سرویس و پایگاه‌دادهٔ تازه ندارد.

## منبع و به‌روزرسانی

ورودی یک snapshot عمومیِ بازبینی‌شده از ثبت مرکزی و مسیرهای فعال ingress است؛ این فایل جایگزین ثبت مرکزی محصولات نیست. هنگام تغییر مسیر عمومی، معرفی، وضعیت یا افزودن سامانه، snapshot دوباره از منابع اصلی تطبیق داده و خروجی دوباره ساخته و منتشر شود. `generatedAt` زمان برداشت را ثبت می‌کند. دادهٔ کاربران، عضویت، tenant، client، توکن، repository، تنظیمات داخلی و URLهای ورودِ دارای query وارد snapshot نشوند.

گروه‌ها در این نسخه بر اساس کاربرد هستند. انتساب رسمی مرکز قابلیت از ثبت مرکزی مصرف می‌شود؛ تا کامل‌شدن آن، این صفحه برای محصولات فاقد انتساب، مرکز رسمی جدید تعیین نمی‌کند. وضعیت `ready` تنها نمایش عادی لینک است و به‌معنای پذیرش عملیاتی یا تأیید ورود نیست. `setup` و `unavailable` با توضیح کوتاه وضعیت شناخته‌شده نمایش داده می‌شوند؛ صفحه پایش زنده نیست.

لوگوی رسمی بدون تغییر در `assets/fanasa-logo.svg` و فونت مجاز در `assets/Vazir-Regular.woff2` و `assets/Vazir-Bold.woff2` تحویل سازنده شوند. مرجع برند: [کتاب برند فن‌آسا](https://fanasa.net/fa/brand). رنگ‌های این نسخه با مرجع فعلی تطبیق داده شده‌اند.

## ساخت

```powershell
python deploy/fanasa-app-directory/render.py --catalog <public-snapshot.json> --assets <approved-assets-directory> --output <fresh-release-directory>
```

ورودی:

```json
{
  "generatedAt": "2026-10-27T00:00:00+03:30",
  "groups": [{
    "key": "organization",
    "title": "سامانه‌های سازمانی",
    "description": "معرفی کوتاه گروه",
    "apps": [{
      "key": "portal",
      "name": "پنجره واحد",
      "subtitle": "Unified Portal",
      "description": "دسترسی یکپارچه به خدمات سازمان.",
      "url": "https://hub.app.fanasacloud.ir/",
      "status": "ready"
    }]
  }]
}
```

فیلدهای `subtitle`، `note`، `linkLabel` و `status` اختیاری‌اند. وضعیت فقط `ready`، `setup` یا `unavailable` است. سازنده فقط URLهای HTTPS دامنهٔ `app.fanasacloud.ir` و زیردامنه‌های آن را، بدون نام کاربری، رمز، query یا fragment، می‌پذیرد. تمام متن‌ها پیش از درج در HTML escape می‌شوند. ورودی snapshot در پوشهٔ عمومی کپی نمی‌شود.

## انتشار

خروجی در پوشهٔ release تازه زیر `/opt/fanasa-gateway/site/app-directory/releases/` قرار گیرد و symlink `current` پس از بررسی به‌صورت اتمی جابه‌جا شود. نسخهٔ قبلی برای بازگشت نگه داشته شود. فایل `nginx.locations.conf` در server موجود دامنهٔ اصلی include شود؛ ریشه، مسیرهای دیگر و تنظیمات ورود تغییری نکنند. کانتینر موجود، `/opt/fanasa-gateway/site` را به‌صورت فقط‌خواندنی در `/site` دارد؛ سرویس تازه لازم نیست.

پیش از reload، `nginx -t` و پس از انتشار، پاسخ HTTP200 بدون cookie برای `/apps/`، redirect مسیر `/apps`، MIME لوگو و فونت، پاسخ 404 مسیر ناشناخته، چیدمان RTL در دسکتاپ و موبایل و باقی‌ماندن redirect ریشه بررسی شوند. همین فایل، مرجع ساخت و refresh این خروجی است.
