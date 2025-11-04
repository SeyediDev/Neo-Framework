using System.Globalization;

namespace Neo.Common.Extensions;

public static class DateTimeExtensions
{
    public static string ToPersianDate(this DateTime dateTime)
    {
        // Create a PersianCalendar instance
        PersianCalendar persianCalendar = new();

        // Convert the DateTime to a Persian date string
        int year = persianCalendar.GetYear(dateTime);
        int month = persianCalendar.GetMonth(dateTime);
        int day = persianCalendar.GetDayOfMonth(dateTime);

        return $"{year}/{month:D2}/{day:D2}"; // Format as "yyyy/MM/dd"
    }

    public static string ToPersianDateStr(this DateTime dateTime)
    {
        PersianCalendar persianCalendar = new();

        int persianYear = persianCalendar.GetYear(dateTime);
        int persianMonth = persianCalendar.GetMonth(dateTime);
        int persianDay = persianCalendar.GetDayOfMonth(dateTime);

        // Format the Persian date to an 8-character string (YYYYMMDD)
        string persianDateString = $"{persianYear:D4}{persianMonth:D2}{persianDay:D2}";

        return persianDateString;
    }

    public static int ToPersianDateInt(this DateTime dateTime)
    {
        PersianCalendar persianCalendar = new();

        int persianYear = persianCalendar.GetYear(dateTime);
        int persianMonth = persianCalendar.GetMonth(dateTime);
        int persianDay = persianCalendar.GetDayOfMonth(dateTime);

        // Format the Persian date to an 8-character string (YYYYMMDD)
        string persianDateString = $"{persianYear:D4}{persianMonth:D2}{persianDay:D2}";

        return int.TryParse(persianDateString, out int persianDateInt)
            ? persianDateInt
            : throw new InvalidOperationException("Failed to convert Persian date string to integer.");
    }
}
