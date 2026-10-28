namespace MassiveSlicer.ViewModels;

/// <summary>
/// Copies an outliner list by index. The render thread walks these collections while the UI
/// thread ungroups a model, and a foreach enumerator throws and takes the process down.
/// </summary>
internal static class OutlinerListSnapshot
{
    public static List<T> Copy<T>(IList<T> items)
    {
        var copy = new List<T>();
        for (int i = 0; ; i++)
        {
            T item;
            try
            {
                if ((uint)i >= (uint)items.Count) break;
                item = items[i];
            }
            catch (ArgumentOutOfRangeException)
            {
                break;
            }
            catch (IndexOutOfRangeException)
            {
                break;
            }
            copy.Add(item);
        }
        return copy;
    }
}
