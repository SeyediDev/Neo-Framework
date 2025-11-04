using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Neo.Common.Extensions;

public static partial class TypeExtensions
{
    public static bool IsNullOrDefault<T>(this T argument)
    {
        // deal with normal scenarios
        if (argument == null) return true;
        if (Equals(argument, default(T))) return true;

        // deal with non-null nullables
        var methodType = typeof(T);
        if (Nullable.GetUnderlyingType(methodType) != null) return false;

        // deal with boxed value types
        var argumentType = argument.GetType();
        if (!argumentType.IsValueType || argumentType == methodType) return false;
        var obj = Activator.CreateInstance(argument.GetType());
        return obj?.Equals(argument) ?? false;
    }

    public static void IfType<T>(this object item, Action<T> action) where T : class
    {
        if (item is T t)
        {
            action(t);
        }
    }

    public static bool HasValue<T>(this T argument)
    {
        return !argument.IsNullOrDefault();
    }

    public static T As<T>(this object obj)
        where T : class
    {
        return (T)obj;
    }

    public static string ToJson<T>(this T entity, JsonSerializerOptions options = null!)
    {
        if (entity is null)
            return null!;

        if (options == null)
        {
            options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }
        else if (options.PropertyNameCaseInsensitive == default)
        {
            options.PropertyNameCaseInsensitive = true;
        }

        return JsonSerializer.Serialize(entity, options);
    }

    public static T FromJson<T>(this string value, [Optional] JsonSerializerOptions settings)
    {
        if (string.IsNullOrWhiteSpace(value))
            return default!;

        if (settings == null)
        {
            settings = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }
        else if (settings.PropertyNameCaseInsensitive == default)
        {
            settings.PropertyNameCaseInsensitive = true;
        }

        return JsonSerializer.Deserialize<T>(value, settings)!;
    }

    public static IEnumerable<Type> ExtractSubs<T>(this Type thisType)
    {
        Type[] nestedTypes = thisType.GetNestedTypes();
        return nestedTypes.ToList().Where(t => t.IsSubclassOf(typeof(T)));
    }

    public static IEnumerable<T?> ExtractSubsInstances<T>(this Type thisType)
    {
        IEnumerable<Type> subs = ExtractSubs<T>(thisType);
        foreach (Type sub in subs)
        {
            yield return Invoke<T>(sub);
        }
    }
    private static T? Invoke<T>(Type item)
    {
        Type[] types = [];
        ConstructorInfo? cons = item.GetConstructor(types);
        object[] parameters = [];
        return cons?.Invoke(parameters) is not T instance ? default : instance;
    }
}
