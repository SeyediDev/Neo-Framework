namespace Neo.Common.Extensions;

public static partial class GenericExtensions
{
    public static bool In<T>(this T item, params T[] items)
    {
        return items == null ? throw new ArgumentNullException(nameof(items)) : items.Contains(item);
    }

    public static bool In<T>(this T item, IEnumerable<T> items)
    {
        return items == null ? throw new ArgumentNullException(nameof(items)) : items.Contains(item);
    }

    public static bool NotNullNorEmpty<T>(this IEnumerable<T> source)
    {
        return source?.Any() == true;
    }

    public static string Limit(this string str, int limit)
    {
        return str[..Math.Min(limit, str.Length)];
    }

    public static void Try<T>(this T item, Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // ignored
        }
    }

    public static bool IsEmpty(this long? @this)
    {
        return @this == null || @this.Value == 0;
    }

    /// <summary>
    /// Wraps this object instance into an IEnumerable&lt;T&gt;
    /// consisting of a single item.
    /// </summary>
    /// <typeparam name="T"> Type of the object. </typeparam>
    /// <param name="item"> The instance that will be wrapped. </param>
    /// <returns> An IEnumerable&lt;T&gt; consisting of a single item. </returns>
    public static IEnumerable<T> Yield<T>(this T item)
    {
        yield return item;
    }

    public static bool TryGetValue<TKey, T>(IDictionary<TKey, T> dic, ref TKey key, Func<TKey> nullValueReplacement,
        out T? item)
    {
        key ??= nullValueReplacement();
        return dic.TryGetValue(key, out item);
    }
}
