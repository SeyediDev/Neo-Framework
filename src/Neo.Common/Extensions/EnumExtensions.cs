using Neo.Common.Attributes;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

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
        DescriptionAttribute descriptionAttribute = value.GetAttribute<DescriptionAttribute>()!;
        if (descriptionAttribute != null) 
        {
            return descriptionAttribute.Description;
        }

        EnumDescriptionAttribute enumDescriptionAttribute = value.GetAttribute<EnumDescriptionAttribute>()!;
        if (enumDescriptionAttribute != null)
        {
            return enumDescriptionAttribute.Name;
        }
        DisplayAttribute displayAttribute = value.GetAttribute<DisplayAttribute>()!;
        if (displayAttribute != null)
        {
            return displayAttribute.Name;
        }

        return value?.ToString();
    }
    public static string? GetTitle(this Enum value)
    {
        var attribute = value.GetAttribute<TitleAttribute>();
        return attribute == null ? value.ToString() : attribute.Title;
    }
    public static int ToInt(this Enum value)
    {
        return Convert.ToInt32(value);
    }
    
    public static TEnum ToEnum<TEnum>(this object value, TEnum defaultValue)
        where TEnum : Enum
    {
        switch(value)
        {
            case TEnum e:
                return e;
            case string str:
                if (Enum.TryParse(typeof(TEnum), str, true, out var v))
                    return (TEnum)v;
                return defaultValue;
            case byte or int or long or uint or ulong or short:
                return (TEnum)value;
            default:
                return defaultValue;
        }
    }
}
