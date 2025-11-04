namespace Neo.Common.Extensions;

public static class SplitExtensions
{
    public static IEnumerable<List<T>> SplitList<T>(List<T> locations, int nSize = 50)
    {
        for (int i = 0; i < locations.Count; i += nSize)
        {
            yield return locations.GetRange(i, Math.Min(nSize, locations.Count - i));
        }
    }

    public static void RunBatch<T>(this List<T> list, int batchSize, Action<List<T>> action)
    {
        if (batchSize <= 0)
        {
            action(list);
        }
        else
        {
            for (int i = 0; i < list.Count;)
            {
                var partList = list.Skip(i).Take(batchSize).ToList();
                i += partList.Count;
                action(partList);
            }
        }
    }
}
