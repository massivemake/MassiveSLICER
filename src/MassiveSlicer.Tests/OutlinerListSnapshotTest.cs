using System.Collections.ObjectModel;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

public class OutlinerListSnapshotTest
{
    [Fact]
    public void Copy_survives_adds_and_removes_from_another_thread()
    {
        var items = new ObservableCollection<int>();
        for (int i = 0; i < 400; i++) items.Add(i);

        int failures = 0;
        var walker = new Thread(() =>
        {
            for (int n = 0; n < 300; n++)
            {
                try
                {
                    var copy = OutlinerListSnapshot.Copy(items);
                    _ = copy.Count;
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref failures);
                }
            }
        });
        walker.Start();
        for (int n = 0; n < 300; n++)
        {
            items.Add(1000 + n);
            if (items.Count > 80)
                items.RemoveAt(0);
        }
        walker.Join();
        Assert.Equal(0, failures);
    }
}
