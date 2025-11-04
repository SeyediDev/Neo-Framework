using Neo.Common.Attributes;
using System.ComponentModel;

namespace Neo.Common.Extensions;

public static class EnumExtensions
{
    public static T? GetAttribute<T>(this Enum value) where T : Attribute
    {
        Type type = value.GetType();
        System.Reflection.MemberInfo[] memberInfo = type.GetMember(value.ToString());
        object[] attributes = memberInfo[0].GetCustomAttributes(typeof(T), false);
        return attributes.Length > 0 ? (T)attributes[0] : null;
    }
    public static string? ToName(this Enum value)
    {
        DescriptionAttribute attribute = value.GetAttribute<DescriptionAttribute>()!;
        return attribute == null ? value.GetAttribute<EnumDescriptionAttribute>()?.Name ?? value?.ToString() : attribute.Description;
    }
    public static string? GetTitle(this Enum value)
    {
        var attribute = value.GetAttribute<TitleAttribute>();
        return attribute == null ? value.ToString() : attribute.Title;
    }
    public static string? PersianName(this Enum value)
    {
        return value.GetAttribute<EnumDescriptionAttribute>()?.Name ?? value?.ToString();
    }
    public static string? EnglishName(this Enum value)
    {
        return value.GetAttribute<EnumDescriptionAttribute>()?.EnName ?? value?.ToString();
    }
    public static int ToInt(this Enum value)
    {
        return Convert.ToInt32(value);
    }
    
    public static TEnum ToEnum<TEnum>(this object value, TEnum defualtValue)
        where TEnum : Enum
    {
        switch(value)
        {
            case TEnum e:
                return e;
            case string str:
                if (Enum.TryParse(typeof(TEnum), str, true, out var v))
                    return (TEnum)v;
                return defualtValue;
            case byte or int or long or uint or ulong or short:
                return (TEnum)value;
            default:
                return defualtValue;
        }
    }
}
