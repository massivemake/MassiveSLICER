using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MassiveSlicer.App.Views;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
        DialogWindowChrome.Apply(this);
        TitleBar.PointerPressed += (_, e) => BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void OnDone(object? sender, RoutedEventArgs e)  => Close();

    private void NavNavigation_Click(object? sender, RoutedEventArgs e)  => ShowSection(0);
    private void NavPerformance_Click(object? sender, RoutedEventArgs e) => ShowSection(1);
    private void NavAppearance_Click(object? sender, RoutedEventArgs e)  => ShowSection(2);
    private void NavConnections_Click(object? sender, RoutedEventArgs e) => ShowSection(3);

    /// <summary>Opens directly on the Connections section (dock cog, ERP hints).</summary>
    public void ShowConnections() => ShowSection(3);

    private void ShowSection(int index)
    {
        // Keep-on-bed lives on the Navigation page. Leaving it visible on
        // Connections ate the top of the scroller so LFAM 3 user/pass never
        // came into view.
        SectionKeepOnBed.IsVisible   = index == 0;
        SectionNavigation.IsVisible  = index == 0;
        SectionPerformance.IsVisible = index == 1;
        SectionAppearance.IsVisible  = index == 2;
        SectionConnections.IsVisible = index == 3;
        BtnNavigation.Classes.Set("Active",  index == 0);
        BtnPerformance.Classes.Set("Active", index == 1);
        BtnAppearance.Classes.Set("Active",  index == 2);
        BtnConnections.Classes.Set("Active", index == 3);
        PrefsScroller.Offset = new Avalonia.Vector(0, 0);
    }
}
