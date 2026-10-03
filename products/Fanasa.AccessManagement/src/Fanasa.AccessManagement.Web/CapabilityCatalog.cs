namespace Fanasa.AccessManagement.Web;

public sealed record CapabilityCenter(string Number, string Name, string Pillar, string Description, string OfficialUrl, string Audience, string Color);

public static class CapabilityCatalog
{
    public static IReadOnlyList<CapabilityCenter> All { get; } =
    [
        new("01", "ابرگان", "کسب‌وکار و بازار", "لایه کسب‌وکار دیجیتال و نقطه تماس آدم‌ها با فن‌آسا", "https://fanasa.net/fa/centers/abrgan", "fanasa.abrgan", "teal"), new("02", "کاروان", "کسب‌وکار و بازار", "بازار عامل‌ها، مهارت‌ها و سرویس‌های فن‌آسا", "https://fanasa.net/fa/centers/karavan", "fanasa.karavan", "blue"),
        new("03", "کاوان", "هوش و عامل‌ها", "محیط اجرای عامل‌ها با هویت، سیاست و ردگیری کامل", "https://fanasa.net/fa/centers/kavan", "fanasa.kavan", "amber"), new("04", "پیوند", "هوش و عامل‌ها", "هاب اتصال عامل‌ها به ابزارها، سامانه‌ها و رویدادها", "https://fanasa.net/fa/centers/peyvand", "fanasa.peyvand", "plum"), new("05", "دانا", "هوش و عامل‌ها", "پلتفرم داده و مدل؛ از داده خام تا دانش قابل‌بازیابی", "https://fanasa.net/fa/centers/dana", "fanasa.dana", "teal"),
        new("06", "بنیان", "زیرساخت: مرکز و لبه", "توان محاسباتی هوش مصنوعی فن‌آسا", "https://fanasa.net/fa/centers/bonyan", "fanasa.bonyan", "blue"), new("07", "ابر فن‌آسا", "زیرساخت: مرکز و لبه", "ابر سازمانی و زیرساخت سلف‌سرویس از مرکز تا لبه", "https://fanasa.net/fa/centers/fanasa-cloud", "fanasa.cloud", "amber"), new("08", "آسمان", "زیرساخت: مرکز و لبه", "زیرساخت فیزیکی فن‌آسا", "https://fanasa.net/fa/centers/asman", "fanasa.asman", "plum"), new("09", "کرانه", "زیرساخت: مرکز و لبه", "امتداد ابر فن‌آسا تا کارخانه، فروشگاه و دستگاه", "https://fanasa.net/fa/centers/kerane", "fanasa.kerane", "teal"),
        new("10", "رایان", "ساخت", "کارخانه ساخت عامل‌ها و نرم‌افزار فن‌آسا", "https://fanasa.net/fa/centers/rayan", "fanasa.rayan", "blue"),
        new("11", "دژان", "کنترل", "صفحه اعتماد و امنیت فن‌آسا", "https://fanasa.net/fa/centers/dejan", "fanasa.dejan", "amber"), new("12", "آیین", "کنترل", "صفحه حاکمیت فن‌آسا", "https://fanasa.net/fa/centers/ayin", "fanasa.ayin", "plum"), new("13", "دیده‌بان", "کنترل", "صفحه عملیات فن‌آسا", "https://fanasa.net/fa/centers/didban", "fanasa.didban", "teal"), new("14", "ترازو", "کنترل", "صفحه اقتصاد فن‌آسا", "https://fanasa.net/fa/centers/tarazu", "fanasa.tarazu", "blue")
    ];
}
