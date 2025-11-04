using System.Reflection;

namespace Neo.Common.Extensions;

public static class DictionaryExtensions
{
    public static TResult ToObject<TResult>(this IDictionary<string, string> source)
        where TResult : class, new()
    {
        var someObject = new TResult();
        var someObjectType = someObject.GetType();

        foreach (var item in source)
        {
            var property = (MemberInfo?)someObjectType.GetProperty(item.Key) ??
                     someObjectType.GetField(item.Key);
            if (property is PropertyInfo p)
                p.SetValue(someObject, item.Value, null);
            else if (property is FieldInfo f)
                f.SetValue(someObject, item.Value);
        }

        return someObject;
    }

    public static void MergeObject<T>(this IDictionary<string, object> dest, T obj)
        where T : class
    {
        void AddThings(MemberInfo[] memberInfos)
        {
            foreach (var memberInfo in memberInfos)
            {
                object? fieldValue = null;
                if (memberInfo.MemberType == MemberTypes.Field)
                    fieldValue = (memberInfo as FieldInfo)?.GetValue(obj);
                if (memberInfo.MemberType == MemberTypes.Property)
                    fieldValue = (memberInfo as PropertyInfo)?.GetValue(obj);
                if (fieldValue == null) continue;
                if (!dest.ContainsKey(memberInfo.Name))
                    dest.Add(memberInfo.Name, fieldValue);
                else
                    dest[memberInfo.Name] = fieldValue;
            }
        }

        if (obj == null) return;
        var type = obj.GetType();
        if (!type.IsClass)
            return;
        AddThings(type.GetFields());
        AddThings(type.GetProperties());
    }
    public static TValue? GetOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
    {
        return dictionary.TryGetValue(key, out TValue? obj) ? obj : default;
    }

    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, Func<TKey, TValue> factory)
    {
        if (dictionary.TryGetValue(key, out TValue? obj))
        {
            return obj;
        }

        return dictionary[key] = factory(key);
    }

    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, Func<TValue> factory)
    {
        return dictionary.GetOrAdd(key, k => factory());
    }
}
