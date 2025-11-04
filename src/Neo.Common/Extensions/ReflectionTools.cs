using System.Collections;
using System.Reflection;

namespace Neo.Common.Extensions;

public static class ReflectionTools
{
    public static bool IsGenericList(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
    }

    public static bool IsClass(Type type)
    {
        return type != null && type.IsClass && type != typeof(object) && type != typeof(string);
    }

    public static bool IsInBaseType<T>(params Type[] types)
    {
        Type interfaceType = typeof(T);
        return FetchBaseType(interfaceType, types) != null;
    }

    public static bool IsInBaseInterface<T>(this Type type)
    {
        if(type==null) return false;
        Type t = typeof(T);
        return IsInBaseInterface(t, type);
    }

    public static Type? FetchBaseType<T>(params Type[] types)
    {
        return FetchBaseType(typeof(T), types);
    }

    public static IList<object> GetEnumerableValue(object list)
    {
        return list != null && IsGenericList(list.GetType())
            ? [.. ((IEnumerable)list).Cast<object>()]
        : new List<object>();
    }

    private static bool IsInBaseInterface(Type interfaceType, Type reqType)
    {
        return reqType.GetInterface(interfaceType.Name) != null;
    }

    private static Type? FetchBaseType(Type t, Type[] types)
    {
        Type? type = t.BaseType;
        do
        {
            if (type.In(types))
            {
                return type;
            }

            type = type?.BaseType;
        } while (type != null);

        return null;
    }
    
    public static TAttr? GetMethodAttribute<TAttr>(this MethodInfo method, Type? implType=null)
        where TAttr : Attribute
    {
        // اول روی خود متد کلاس پیاده‌سازی
        var attr = method.GetCustomAttribute<TAttr>(inherit: true)
            ?? method.DeclaringType?.GetCustomAttribute<TAttr>()
            ?? implType?.GetCustomAttribute<TAttr>();
        if (attr != null) return attr;
        if (implType != null)
        {
            // سپس روی متدهای اینترفیس
            foreach (var iface in implType.GetInterfaces())
            {
                var map = implType.GetInterfaceMap(iface);
                for (int i = 0; i < map.TargetMethods.Length; i++)
                {
                    if (map.TargetMethods[i] == method)
                    {
                        return map.InterfaceMethods[i].GetCustomAttribute<TAttr>(inherit: true);
                    }
                }
            }
        }

        return null;
    }
    /// <summary>
    /// Gets an attribute from a type or its interfaces.
    /// Searches the type itself first, then all its interfaces.
    /// </summary>
    /// <typeparam name="TAttr">The attribute type to search for</typeparam>
    /// <param name="type">The type to search</param>
    /// <returns>The attribute if found, null otherwise</returns>
    public static TAttr? GetAttribute<TAttr>(this Type type)
        where TAttr : Attribute
    {
        return type.GetAttributeDeep<TAttr>(includeBaseTypes: false);
    }

    /// <summary>
    /// Gets an attribute from a type, its interfaces, or its base types.
    /// Searches in order: type itself, interfaces, base types.
    /// </summary>
    /// <typeparam name="TAttr">The attribute type to search for</typeparam>
    /// <param name="type">The type to search</param>
    /// <param name="includeBaseTypes">Whether to search base types</param>
    /// <returns>The attribute if found, null otherwise</returns>
    public static TAttr? GetAttributeDeep<TAttr>(this Type type, bool includeBaseTypes = true)
        where TAttr : Attribute
    {
        // First check the type itself
        var attr = type.GetCustomAttribute<TAttr>(inherit: true);
        if (attr != null) return attr;

        // Then check all interfaces
        foreach (var iface in type.GetInterfaces())
        {
            attr = iface.GetCustomAttribute<TAttr>(inherit: true);
            if (attr != null) return attr;
        }

        // Finally check base types if requested
        if (includeBaseTypes)
        {
            var baseType = type.BaseType;
            while (baseType != null && baseType != typeof(object))
            {
                attr = baseType.GetCustomAttribute<TAttr>(inherit: true);
                if (attr != null) return attr;

                // Also check interfaces of base types
                foreach (var iface in baseType.GetInterfaces())
                {
                    attr = iface.GetCustomAttribute<TAttr>(inherit: true);
                    if (attr != null) return attr;
                }

                baseType = baseType.BaseType;
            }
        }

        return null;
    }


    public static TParam? FindParameter<TParam>(this ParameterInfo[] parameters, object[] args, string? nameHint = null)
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            if ((nameHint == null || parameters[i].Name == nameHint) && 
                parameters[i].ParameterType == typeof(TParam) && 
                (args?.Length>i && args?[i] is TParam value) )
            {
                return value;
            }
        }
        return default;
    }
}
