using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace Neo.Common.Extensions;

public static partial class TypeConverterExtension
{
    public static T ToEnumOrDefault<T>(this string value, T defaultValue = default) where T : struct
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : Enum.TryParse(value, true, out T result) ? result : defaultValue;
    }

    public static T? ToNullableEnum<T>(this string value) where T : struct
    {
        return string.IsNullOrWhiteSpace(value) ? null : Enum.TryParse(value, true, out T result) ? result : null;
    }

    public static DateTime ToDateTimeOrDefault(this string value, DateTime defaultValue = default)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : DateTime.TryParse(value, out var result) ? result : defaultValue;
    }

    public static DateTime? ToNullableDateTimeOrDefault(this string value)
        => !string.IsNullOrEmpty((value ?? "").Trim()) ? value?.ToDateTimeOrDefault() : null;

    public static short ToInt16(this string? value, short defaultValue = default)
    {
        return string.IsNullOrWhiteSpace(value?.ToString()) ? defaultValue : short.TryParse(value?.ToString(), out var result) ? result : defaultValue;
    }

    public static int ToInt(this object? value, short defaultValue = default)
    {
        if(value is null )
            return defaultValue;
        return Convert.ToInt32(value);
    }

    public static short ToInt16OrDefault(this string value, short defaultValue = default)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : short.TryParse(value, out var result) ? result : defaultValue;
    }

    public static int ToInt32OrDefault(this string? value, int defaultValue = default)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : int.TryParse(value, out var result) ? result : defaultValue;
    }

    public static int? ToNullableInt32(this string? value)
        => !string.IsNullOrEmpty(value) ? value.ToInt32OrDefault() : null;

    public static long ToInt64OrDefault(this string value, long defaultValue = default)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : long.TryParse(value, out var result) ? result : defaultValue;
    }

    public static long? ToNullableInt64(this string value)
        => !string.IsNullOrEmpty(value) ? value.ToInt64OrDefault() : null;

    public static bool ToBooleanOrDefault(this string value, bool defaultValue = default)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : bool.TryParse(value, out var result) ? result : defaultValue;
    }

    public static float ToFloatOrDefault(this string value, float defaultValue = default)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : float.TryParse(value, out var result) ? result : defaultValue;
    }

    public static decimal ToDecimalOrDefault(this string value, decimal defaultValue = default)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : decimal.TryParse(value, out var result) ? result : defaultValue;
    }

    public static double ToDoubleOrDefault(this string value, double defaultValue = default)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : double.TryParse(value, out var result) ? result : defaultValue;
    }

    public static bool IsWholeNumber(this string value)
        => long.TryParse(value, out _);

    public static bool IsDecimalNumber(this string value)
        => decimal.TryParse(value, out _);

    public static bool IsBoolean(this string value)
        => bool.TryParse(value, out var _);

    public static string ToString(this byte[] value)
        => value != null ? BitConverter.ToString(value) : "";

    public static byte[] ToByte(this string value) => Encoding.UTF8.GetBytes(value);

    public static string Base64Encode(this string plainText)
    {
        var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(plainTextBytes);
    }

    public static string Base64Decode(this string base64EncodedData)
    {
        var base64EncodedBytes = Convert.FromBase64String(base64EncodedData);
        return Encoding.UTF8.GetString(base64EncodedBytes);
    }
    
    public static string ToBase64(this Image image)
    {
// #if !DEBUG
//             var _image = (Image) image.Clone();
//             var inverted = _image.InvertColor(new Random().Next(1, 30));
//             image = (Image) inverted.Clone();
// #endif

        using var m = new MemoryStream();
#pragma warning disable CA1416 // Validate platform compatibility
        image?.Save(m, ImageFormat.Jpeg);
#pragma warning restore CA1416 // Validate platform compatibility
        var imageBytes = m.ToArray();

        //var byteLength = imageBytes.Length;
        //var mid = byteLength / 50;
        // for (var i = 0; i < 1; i++)
        // {
        //     var randomByte = new Random().Next(byteLength - mid, byteLength);
        //     imageBytes[randomByte] = Convert.ToString(new Random().Next(1, 55), 16).ToByte()[0];
        //     //imageBytes[randomByte] = new Random().NextBytes(imageBytes);
        // }

        // Convert byte[] to Base64 String
        var base64String = Convert.ToBase64String(imageBytes);
        return base64String;
    }
}
