using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Neo.Common.Extensions;

public static partial class StringExtensions
{
    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex KebabCaseRegex();

    public static string ToKebabCase(this string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        string kebab = KebabCaseRegex().Replace(input, "-$1");
        return kebab.ToLower();
    }

    public static string ToPascalCase(this string name, bool lower)
    {
        string ret = "";
        string s = string.Join("", name.Split('\''));
        s = s.Replace(',', ' ').Replace(';', ' ');
        foreach (string item in s.Split(' '))
        {
            if (item.Length > 0)
            {
                ret += item[..1].ToUpper();
            }

            if (item.Length > 1)
            {
                if (lower)
                {
                    ret += item[1..].ToLower();
                }
                else
                {
                    ret += item[1..];
                }
            }
        }

        return ret; //string.Join("_", ).ToString();
    }

    public static string ForceMax(this string str, int max)
    {
        if (str != null && str.Length > max)
        {
            str = str[..max];
        }

        return str!;
    }

    public static bool RegexStartsWith(this string str, params string[] patterns)
    {
        return patterns.Any(pattern =>
           Regex.Match(str, "^(" + pattern + ")").Success);
    }

    public static string Remove(this string value, string substring)
    {
        return value.Replace(substring, "");
    }

    public static bool IsNullOrEmpty(this string value)
    {
        return string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(value);
    }

    public static bool HasValue(this string value)
    {
        return !value.IsNullOrEmpty();
    }

    public static bool HasNotValue(this string value)
    {
        return value.IsNullOrEmpty();
    }

    public static bool IsEqualCaseInsensitive(this string value, string compareTo)
    {
        return value.ToLower().Trim() == compareTo.ToLower().Trim();
    }

    public static string Slice(this string value, string splitter, int partNumber)
    {
        string[] slices = value.Split(splitter);
        partNumber--; // slices index starts from zero 
        return value.HasValue() && value.Contains(splitter) && slices.Length > partNumber
            ? slices[partNumber]
            : string.Empty;
    }

    public static string MaskMobileNumber(this string mobileNumber, bool inverse = false)
    {
        return string.IsNullOrWhiteSpace(mobileNumber) || mobileNumber.Length < 9
            ? string.Empty
            : inverse
            ? mobileNumber[9..] + "****" + mobileNumber[..6]
            : mobileNumber[..6] + "xxx" + mobileNumber[9..];
    }

    public static string ToCamelCase(this string value)
    {
        return value[0].ToString().ToLower() + value[1..];
    }

    public static string ToPascalCase(this string value)
    {
        return value[0].ToString().ToUpper() + value[1..];
    }

    public static bool IsSafeMultiCultureString(this string value)
    {
        return string.IsNullOrWhiteSpace(value) || Regex.IsMatch(value, @"^(?!.*[(@#!%$&*)])[A-Za-z\s\u0600-\u06FF0-9_\.\-]+$");
    }

    public static string ToBase64Encode(this string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        return Convert.ToBase64String(bytes);
    }
    /// <summary>
    /// To the int.
    /// </summary>
    /// <param name="value">The parameter value.</param>
    /// <returns></returns>
    public static int Int(this object value)
    {
        int outVal;
        try
        {
            switch (value)
            {
                case double d:
                    outVal = (int)d;
                    break;
                case int d:
                    outVal = d;
                    break;
                case long d:
                    outVal = (int)d;
                    break;
                case string d:
                default:
                    {
                        double temp = Convert.ToDouble(value);
                        outVal = Convert.ToInt32(temp);
                        break;
                    }
            }
        }
        catch
        {
            outVal = 0;
        }
        return outVal;
    }

    public static long Long(this object value)
    {
        long outVal;
        try
        {
            switch (value)
            {
                case double d:
                    outVal = Convert.ToInt64(d);
                    break;
                case int d:
                    outVal = d;
                    break;
                case long d:
                    outVal = d;
                    break;
                case string d:
                default:
                    {
                        double temp = Convert.ToDouble(value);
                        outVal = Convert.ToInt32(temp);
                        break;
                    }
            }
        }
        catch
        {
            outVal = 0;
        }
        return outVal;
    }

    public static string Sha256(this string rawData)
    {
        // Create a SHA256
        using SHA256 sha256Hash = SHA256.Create();
        // ComputeHash - returns byte array  
        byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));

        // Convert byte array to a string   
        StringBuilder builder = new();
        foreach (byte t in bytes)
        {
            _ = builder.Append(t.ToString("x2"));
        }

        return builder.ToString();
    }

    public static bool ToBoolean(this string? value)
    {
        return bool.TryParse(value?.ToString(), out bool result) && result;
    }
}
