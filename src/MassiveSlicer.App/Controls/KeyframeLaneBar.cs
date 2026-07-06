using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace MassiveSlicer.Controls;

/// <summary>One keyframe rendered on the lane: diamond at <see cref="X"/>, influence
/// span from <see cref="LeftX"/> to <see cref="RightX"/> (all in lane pixels).</summary>
public sealed record KeyframeLaneItem(int KeyIndex, double X, double LeftX, double RightX, bool IsSelected);

/// <summary>
/// Interactive keyframe lane under the playback scrubber: clickable keyframe diamonds
/// with draggable left/right influence ticks. Rendering is a single pass (no per-item
/// visuals); interaction callbacks are plain delegates wired from code-behind.
/// </summary>
public class KeyframeLaneBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<KeyframeLaneItem>?> ItemsProperty =
        AvaloniaProperty.Register<KeyframeLaneBar, IReadOnlyList<KeyframeLaneItem>?>(nameof(Items));

    public IReadOnlyList<KeyframeLaneItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>Raised when a keyframe diamond is clicked (arg: KeyIndex).</summary>
    public Action<int>? KeyframeClicked { get; set; }

    /// <summary>Raised while an influence tick is dragged:
    /// (KeyIndex, isLeftTick, pointerX, commit). commit=true on release.</summary>
    public Action<int, bool, double, bool>? InfluenceDragged { get; set; }

    private static readonly IBrush SpanBrush = new SolidColorBrush(Color.FromArgb(70, 126, 216, 126));
    private static readonly IBrush TickBrush = new SolidColorBrush(Color.FromArgb(200, 126, 216, 126));
    private static readonly IBrush KeyBrush  = new SolidColorBrush(Color.FromRgb(126, 216, 126));
    private static readonly Pen    SpanPen   = new(SpanBrush, 3);
    private static readonly Pen    SelPen    = new(Brushes.White, 1.5);

    private int  _dragKey  = -1;
    private bool _dragLeft;
    private int  _pressedKey = -1;

    static KeyframeLaneBar()
    {
        AffectsRender<KeyframeLaneBar>(ItemsProperty);
    }

    public override void Render(DrawingContext context)
    {
        // Transparent hit-test surface across the whole lane.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var items = Items;
        if (items is null || items.Count == 0) return;

        double cy = Bounds.Height / 2.0;
        foreach (var it in items)
        {
            context.DrawLine(SpanPen, new Point(it.LeftX, cy), new Point(it.RightX, cy));
            context.FillRectangle(TickBrush, new Rect(it.LeftX  - 1.5, cy - 5, 3, 10));
            context.FillRectangle(TickBrush, new Rect(it.RightX - 1.5, cy - 5, 3, 10));

            double d = it.IsSelected ? 7.0 : 5.5;
            var geom = new StreamGeometry();
            using (var g = geom.Open())
            {
                g.BeginFigure(new Point(it.X, cy - d), isFilled: true);
                g.LineTo(new Point(it.X + d, cy));
                g.LineTo(new Point(it.X, cy + d));
                g.LineTo(new Point(it.X - d, cy));
                g.EndFigure(isClosed: true);
            }
            context.DrawGeometry(KeyBrush, it.IsSelected ? SelPen : null, geom);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var items = Items;
        if (items is null || items.Count == 0) return;
        var p = e.GetPosition(this);

        _dragKey = -1; _pressedKey = -1;
        double bestTick = 6.0, bestKey = 7.0;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            double dL = Math.Abs(p.X - it.LeftX);
            double dR = Math.Abs(p.X - it.RightX);
            double dK = Math.Abs(p.X - it.X);
            if (dL < bestTick) { bestTick = dL; _dragKey = it.KeyIndex; _dragLeft = true;  }
            if (dR < bestTick) { bestTick = dR; _dragKey = it.KeyIndex; _dragLeft = false; }
            if (dK < bestKey)  { bestKey  = dK; _pressedKey = it.KeyIndex; }
        }
        // A tick grab wins over the diamond unless the diamond is clearly closer.
        if (_dragKey >= 0 && bestKey + 2 < bestTick) _dragKey = -1;
        if (_dragKey >= 0) _pressedKey = -1;

        if (_dragKey >= 0 || _pressedKey >= 0)
        {
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragKey < 0) return;
        InfluenceDragged?.Invoke(_dragKey, _dragLeft, e.GetPosition(this).X, false);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragKey >= 0)
        {
            InfluenceDragged?.Invoke(_dragKey, _dragLeft, e.GetPosition(this).X, true);
            _dragKey = -1;
            e.Handled = true;
        }
        else if (_pressedKey >= 0)
        {
            KeyframeClicked?.Invoke(_pressedKey);
            _pressedKey = -1;
            e.Handled = true;
        }
        e.Pointer.Capture(null);
    }
}
