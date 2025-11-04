using System.Reflection;

namespace Neo.Common.Extensions;

public static class MemberInfoExtentions
{
    public static Type MemberInfoType(this MemberInfo memberInfo)
    => memberInfo switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => typeof(object),
    };
    public static object? GetValue(this MemberInfo memberInfo, object obj)
        => memberInfo switch
        {
            PropertyInfo property => property?.GetValue(obj, null),
            FieldInfo field => field.GetValue(obj),
            _ => null,
        };
    public static string? GetName(this MemberInfo memberInfo)
        => memberInfo switch
        {
            PropertyInfo property => property.Name,
            FieldInfo field => field.Name,
            _ => memberInfo.Name,
        };

    private static T GetAttribute<T>(IEnumerable<object> attributes) where T : Attribute
    {
        foreach (var attr in attributes)
        {
            if (attr is T attribute)
                return attribute;
        }

        return default!;
    }

    public static void SetValue(this MemberInfo memberInfo, object obj, object? value)
    {
        switch (memberInfo)
        {
            case PropertyInfo property:
                if (!property.CanWrite)
                    return;
                property.SetValue(obj, value, null);
                break;
            case FieldInfo field:
                if (field.IsInitOnly)
                    return;
                field.SetValue(obj, value);
                break;
        }
    }
}
