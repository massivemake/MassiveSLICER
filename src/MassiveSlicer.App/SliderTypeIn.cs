using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MassiveSlicer.App;

/// <summary>
/// App-wide slider affordance: double-clicking any <see cref="Slider"/> opens a small
/// type-in flyout at the pointer. Enter commits (clamped to the slider's range, flowing
/// through the slider's normal two-way binding), Escape or dismissal cancels.
/// </summary>
internal static class SliderTypeIn
{
    /// <summary>Installs the global double-tap handler. Call once at startup.</summary>
    public static void Install()
    {
        InputElement.DoubleTappedEvent.AddClassHandler<Slider>((slider, e) =>
        {
            ShowEditor(slider);
            e.Handled = true;
        });
    }

    private static void ShowEditor(Slider slider)
    {
        var textBox = new TextBox
        {
            Text = slider.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            Width = 76,
            FontSize = 12,
            Padding = new Avalonia.Thickness(6, 4),
        };

        var flyout = new Flyout
        {
            Content = textBox,
            Placement = PlacementMode.Pointer,
            ShowMode = FlyoutShowMode.Transient,
        };

        void Commit()
        {
            var raw = (textBox.Text ?? "").Trim().Replace(',', '.');
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
            flyout.Hide();
        }

        textBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter)  { Commit(); ke.Handled = true; }
            if (ke.Key == Key.Escape) { flyout.Hide(); ke.Handled = true; }
        };

        flyout.ShowAt(slider, showAtPointer: true);
        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        });
    }
}
