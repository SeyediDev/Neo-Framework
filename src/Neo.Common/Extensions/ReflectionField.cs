using System.Net;
using System.Reflection;

namespace Neo.Common.Extensions;

public static class ReflectionField
{
    public static void Copy<T>(T to, T from)
    {
        if (from is null)
        {
            throw new ArgumentNullException(nameof(from));
        }

        if (to is null)
        {
            throw new ArgumentNullException(nameof(to));
        }

        foreach (MemberInfo member in Members<T>())
        {
            SetValue(to, member, GetValue(from, member));
        }
    }

    public static bool GetValue<T>(T obj, string name, out object? value)
    {
        Type type = typeof(T);
        value = null;
        return obj is not null && GetValue(obj, type, name, out value);
    }

    public static bool GetValue(object obj, Type type, string name, out object? value)
    {
        MemberInfo? memberInfo = FetchMember(type, name);
        if (memberInfo != null)
        {
            GetMemberValue(obj, memberInfo, out value);
        }
        else
        {
            value = null;
        }

        return memberInfo != null;
    }

    public static void GetMemberValue(object obj, MemberInfo memberInfo, out object? value)
    {
        value = GetValue(obj, memberInfo);
        Type? memberType = FetchMemberType(memberInfo);
        if (memberType?.IsEnum ?? false)
        {
            FieldInfo[] enumItems = memberType.GetFields();
            string? v = value?.ToString();
            FieldInfo? item = enumItems.FirstOrDefault(ei => ei.Name == v);
            value = item?.GetRawConstantValue() ?? Convert.ToInt32(value);
        }
    }

    public static object? GetValue(object obj, MemberInfo memberInfo)
    {
        return memberInfo switch
        {
            PropertyInfo property => property.GetValue(obj, null),
            FieldInfo field => field.GetValue(obj),
            _ => null,
        };
    }

    public static void SetValue(object obj, MemberInfo memberInfo, object? value)
    {
        switch (memberInfo)
        {
            case PropertyInfo property:
                if (!property.CanWrite)
                {
                    return;
                }

                property.SetValue(obj, value, null);
                break;
            case FieldInfo field:
                if (field.IsInitOnly)
                {
                    return;
                }

                field.SetValue(obj, value);
                break;
        }
    }

    public static Type? FetchMemberType(MemberInfo memberInfo)
    {
        return memberInfo switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => null,
        };
    }

    public static bool IsClass(MemberInfo member)
    {
        Type? memberType = FetchMemberType(member);
        return memberType is not null && IsClass(memberType);
    }

    public static bool IsClass(Type type)
    {
        if ((type?.IsClass ?? false) && type != typeof(string) && type != typeof(IPAddress))
        {
            Type? underlyingType = Nullable.GetUnderlyingType(type);
            if (underlyingType != null)
            {
                if (underlyingType.IsClass && underlyingType != typeof(string) && type != typeof(IPAddress))
                {
                    return true;
                }
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    public static MemberInfo? FetchMember<T>(string name)
    {
        Type type = typeof(T);
        return FetchMember(type, name);
    }

    public static MemberInfo? FetchMember(Type type, string name)
    {
        return (MemberInfo?)type.GetProperty(name) ?? type.GetField(name);
    }

    public static Dictionary<string,MemberInfo> MembersToDictionary<T>()
    {
        Type type = typeof(T);
        return Members(type).ToList().ToDictionary(t=>t.Name);
    }

    public static IEnumerable<MemberInfo> Members<T>()
    {
        Type type = typeof(T);
        return Members(type);
    }

    public static IEnumerable<MemberInfo> Members(Type type)
    {
        foreach (MemberInfo memberInfo in type.GetMembers())
        {
            if (memberInfo.MemberType is MemberTypes.Field or MemberTypes.Property)
            {
                if (memberInfo is PropertyInfo propertyInfo)
                {
                    if (!propertyInfo.CanWrite || !propertyInfo.CanRead)
                        continue;
                }
                yield return memberInfo;
            }
        }
    }

    public static bool IsStatic(MemberInfo member)
    {
        return member switch
        {
            PropertyInfo property => (property.GetGetMethod()?.IsStatic ?? true) && (property.GetSetMethod()?.IsStatic ?? true),
            FieldInfo field => field.IsStatic,
            _ => false,
        };
    }

    public static object? CreateObject(Type memberType)
    {
        ConstructorInfo? ctr = memberType.GetConstructor([]);
        return ctr?.Invoke([]);
    }

    /// <summary>
    /// Returns true when the member should be considered REQUIRED (non-nullable) in C# semantics:
    /// - [Required] attribute => required
    /// - Non-nullable value types => required
    /// - Reference types annotated as non-nullable by compiler metadata or by NotNull/DisallowNull attributes => required
    /// Otherwise returns false (not required / nullable).
    /// </summary>
    public static bool IsFieldRequired(this MemberInfo memberInfo, Type memberType)
    {
        // 1) Explicit [Required] attribute => required
        var attrs = memberInfo.GetCustomAttributes(true);
        if (attrs.Any(a => a.GetType().Name == "RequiredAttribute"))
            return true;

        // 2) Value types: nullable if Nullable<T>, otherwise required
        if (Nullable.GetUnderlyingType(memberType) != null)
            return false; // Nullable<T> => not required
        if (memberType.IsValueType)
            return true; // non-nullable value type => required

        // 3) Check common nullability-related attributes
        foreach (var a in attrs)
        {
            var name = a.GetType().Name;
            if (name == "AllowNullAttribute" || name == "MaybeNullAttribute")
                return false;
            if (name == "NotNullAttribute" || name == "DisallowNullAttribute")
                return true;
        }

        // 4) Try to read compiler nullability metadata (NullableAttribute on member)
        //    If present: constructor arg can be byte or byte[]; convention: 1 = not annotated/non-nullable, 2 = annotated/nullable.
        var cad = memberInfo.CustomAttributes.FirstOrDefault(c => c.AttributeType.Name == "NullableAttribute");
        if (cad != null && cad.ConstructorArguments.Count > 0)
        {
            var arg = cad.ConstructorArguments[0];
            // case: byte[] { ... }
            if (arg.Value is IReadOnlyCollection<CustomAttributeTypedArgument> arr && arr.Count > 0)
            {
                var first = arr.ElementAt(0).Value;
                if (first is byte b)
                    return b == 1; // 1 => non-nullable => required
            }
            // case: single byte
            if (arg.Value is byte bSingle)
            {
                return bSingle == 1;
            }
        }

        // 5) Fallback to NullableContextAttribute on declaring type
        var declaring = memberInfo.DeclaringType;
        if (declaring != null)
        {
            var ctx = declaring.CustomAttributes.FirstOrDefault(c => c.AttributeType.Name == "NullableContextAttribute");
            if (ctx != null && ctx.ConstructorArguments.Count > 0)
            {
                var ctxVal = ctx.ConstructorArguments[0].Value;
                if (ctxVal is byte ctxByte)
                {
                    return ctxByte == 1; // 1 => treat as non-nullable in this implementation
                }
            }

            // 6) Fallback to assembly-level NullableContextAttribute
            var asmCtx = declaring.Assembly.CustomAttributes.FirstOrDefault(c => c.AttributeType.Name == "NullableContextAttribute");
            if (asmCtx != null && asmCtx.ConstructorArguments.Count > 0)
            {
                var asmCtxVal = asmCtx.ConstructorArguments[0].Value;
                if (asmCtxVal is byte asmByte)
                {
                    return asmByte == 1;
                }
            }
        }

        // 7) No reliable metadata found => assume reference types are nullable (not required)
        return false;
    }
}
