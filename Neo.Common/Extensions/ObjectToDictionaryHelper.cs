using System.ComponentModel;

namespace Neo.Common.Extensions;

public static class ObjectToDictionaryHelper
{
    public static IDictionary<string, object> ToDictionary(this object source)
    {
        return source.ToDictionary<object>();
    }

    public static IDictionary<string, T> ToDictionary<T>(this object source)
    {
        if (source == null) ThrowExceptionWhenSourceArgumentIsNull();

        var dictionary = new Dictionary<string, T>();
        foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(source!))
        {
            var value = property.GetValue(source ?? throw new ArgumentNullException(nameof(source)));
            if (IsOfType<T>(value!))
            {
                dictionary.Add(property.Name, (T)value!);
            }
        }

        return dictionary;
    }

    private static bool IsOfType<T>(object value)
    {
        return value is T;
    }

    private static void ThrowExceptionWhenSourceArgumentIsNull()
    {
        throw new NullReferenceException(
            "Unable to convert anonymous object to a dictionary. The source anonymous object is null.");
    }

    public static T DictionaryToObject<T>(this IDictionary<string, object> dict) where T : new()
    {
        var t = new T();
        var properties = t.GetType().GetProperties();

        foreach (var property in properties)
        {
            if (!dict.Any(x => x.Key.Equals(property.Name, StringComparison.InvariantCultureIgnoreCase)))
                continue;

            var item = dict.First(x
                => x.Key.Equals(property.Name, StringComparison.InvariantCultureIgnoreCase));

            // Find which property type (int, string, double? etc) the CURRENT property is...
            var tPropertyType = t.GetType().GetProperty(property.Name)?.PropertyType;

            // Fix nullables...
            var newT = Nullable.GetUnderlyingType(tPropertyType ?? throw new InvalidOperationException()) ??
                       tPropertyType;

            // ...and change the type
            var newA = Convert.ChangeType(item.Value, newT);
            t.GetType().GetProperty(property.Name)?.SetValue(t, newA, null);
        }

        return t;
    }
}