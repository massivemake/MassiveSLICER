using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MassiveSlicer.App.Views;

/// <summary>
/// Asks what percent the desktop slicer used. 10 means the file is 10% and
/// will be scaled by 10 to land at 100%.
/// </summary>
public sealed class ScaledToolpathImportWindow : Window
{
    readonly TextBox _percent;

    public float? SourcePercent { get; private set; }

    public ScaledToolpathImportWindow()
    {
        Title = "Import Scaled Toolpath";
        Width = 460;
        Height = 230;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1b1b1b"));
        Foreground = Brushes.White;

        _percent = new TextBox
        {
            Text = "10",
            Width = 80,
            PlaceholderText = "10",
        };
        _percent.AttachedToVisualTree += (_, _) =>
        {
            _percent.SelectAll();
            _percent.Focus();
        };

        var import = new Button
        {
            Content = "Import",
            MinWidth = 96,
            Background = new SolidColorBrush(Color.Parse("#71a72a")),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        import.Click += (_, _) => Accept();

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(import);

        var root = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
        };
        root.Children.Add(new TextBlock
        {
            Text = "This file was sliced small. MassiveSLICER scales it back to 100% and puts it on the print bed so you can Send it to MassiveDRIVE.",
            TextWrapping = TextWrapping.Wrap,
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = "Sliced at", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_percent);
        row.Children.Add(new TextBlock { Text = "% of full size", VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(row);
        root.Children.Add(buttons);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
                Accept();
            else if (e.Key == Avalonia.Input.Key.Escape)
                Close(false);
        };
    }

    void Accept()
    {
        if (!float.TryParse(_percent.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float pct)
            && !float.TryParse(_percent.Text, out pct))
        {
            _percent.Focus();
            return;
        }
        if (pct is <= 0.01f or > 100f)
        {
            _percent.Focus();
            return;
        }
        SourcePercent = pct;
        Close(true);
    }
}
