using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MassiveSlicer.App.Behaviors;

/// <summary>
/// Avalonia <see cref="Visual.ClipToBounds"/> clips to a rectangle, so child
/// fills square off a Border's <see cref="Border.CornerRadius"/>. This sets
/// <see cref="Visual.Clip"/> to a rounded rect that matches the border.
/// </summary>
public static class CornerClip
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enable", typeof(CornerClip));

    public static void SetEnable(Control element, bool value) => element.SetValue(EnableProperty, value);
    public static bool GetEnable(Control element) => element.GetValue(EnableProperty);

    static CornerClip()
    {
        EnableProperty.Changed.AddClassHandler<Control>(OnEnableChanged);
    }

    static void OnEnableChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
        {
            control.SizeChanged += OnSizeChanged;
            if (control is Border border)
                border.PropertyChanged += OnBorderPropertyChanged;
            Apply(control);
        }
        else
        {
            control.SizeChanged -= OnSizeChanged;
            if (control is Border b)
                b.PropertyChanged -= OnBorderPropertyChanged;
            control.Clip = null;
        }
    }

    static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control c)
            Apply(c);
    }

    static void OnBorderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Border.CornerRadiusProperty && sender is Control c)
            Apply(c);
    }

    static void Apply(Control control)
    {
        var radius = control is Border border ? border.CornerRadius : new CornerRadius(5);
        var clip = CreateClip(control.Bounds.Size, radius);
        if (clip is null)
            return;
        control.Clip = clip;
    }

    /// <summary>Rounded-rect clip. Uniform radius = the largest corner (shells are 5 all around).</summary>
    public static RectangleGeometry? CreateClip(Size size, CornerRadius radius)
    {
        if (size.Width < 1 || size.Height < 1)
            return null;

        double r = Math.Max(
            Math.Max(radius.TopLeft, radius.TopRight),
            Math.Max(radius.BottomLeft, radius.BottomRight));
        r = Math.Min(r, Math.Min(size.Width, size.Height) / 2.0);

        return new RectangleGeometry
        {
            Rect = new Rect(size),
            RadiusX = r,
            RadiusY = r,
        };
    }
}
