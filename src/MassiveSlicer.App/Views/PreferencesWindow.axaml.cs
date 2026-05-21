using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MassiveSlicer.App.Views;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
        TitleBar.PointerPressed += (_, e) => BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void OnDone(object? sender, RoutedEventArgs e)  => Close();

    private void NavNavigation_Click(object? sender, RoutedEventArgs e)  => ShowSection(0);
    private void NavPerformance_Click(object? sender, RoutedEventArgs e) => ShowSection(1);

    private void ShowSection(int index)
    {
        SectionNavigation.IsVisible  = index == 0;
        SectionPerformance.IsVisible = index == 1;
        BtnNavigation.Classes.Set("Active",  index == 0);
        BtnPerformance.Classes.Set("Active", index == 1);
    }
}
