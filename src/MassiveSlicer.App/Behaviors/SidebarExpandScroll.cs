using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MassiveSlicer.App.Behaviors;

/// <summary>
/// Pins an expanded sidebar <c>StepCard</c> to the top of its column.
/// Used by both left (<c>LeftPanelHost</c>) and right (per-tab) scrollers.
/// A bottom pad is added so the last card can actually reach the top —
/// without it, VIEWPORT only moves a few pixels.
/// </summary>
public static class SidebarExpandScroll
{
    const string PadName = "__SidebarScrollPad";
    const int MaxAttempts = 30;
    // Retries driven from LayoutUpdated re-enter the layout pass they were raised from,
    // so they are capped far below MaxAttempts. Avalonia aborts the pass long before 30.
    const int MaxLayoutPasses = 4;

    static bool _armed;

    /// <summary>Call once from each sidebar UserControl so the class handler exists.</summary>
    public static void Arm()
    {
        if (_armed) return;
        _armed = true;
        Expander.IsExpandedProperty.Changed.AddClassHandler<Expander>(OnExpanded);
    }

    static void OnExpanded(Expander card, AvaloniaPropertyChangedEventArgs e)
    {
        if (!card.Classes.Contains("StepCard")) return;
        if (!IsNowExpanded(e)) return;
        if (!UserScrollAllowed(card)) return;
        Schedule(card);
    }

    static bool IsNowExpanded(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is bool b) return b;
        try { return e.GetNewValue<bool>(); }
        catch { return false; }
    }

    static bool UserScrollAllowed(Expander card)
    {
        if (card.FindAncestorOfType<Views.LeftPanelView>() is { } left)
            return left.AllowExpandScroll;
        if (card.FindAncestorOfType<Views.RightPanelView>() is { } right)
            return right.AllowExpandScroll;
        return false;
    }

    public static void Schedule(Expander card)
    {
        card.InvalidateMeasure();
        card.InvalidateArrange();

        var sv = FindColumnScroller(card);
        if (sv is not null)
        {
            EnsurePad(sv, PadHeight(sv));
            sv.InvalidateMeasure();
        }

        int attempts = 0;
        void Tick()
        {
            if (!card.IsExpanded) return;
            if (TryPinToTop(card) || ++attempts >= MaxAttempts)
                return;
            Dispatcher.UIThread.Post(Tick, DispatcherPriority.Background);
        }

        Dispatcher.UIThread.Post(Tick, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(Tick, DispatcherPriority.Background);
        if (sv is not null)
        {
            // TryPinToTop writes sv.Offset and resizes the pad, so every call from here
            // dirties layout and re-raises LayoutUpdated inside the SAME render callback.
            // This handler must therefore count its own passes: `attempts` is only advanced
            // by Tick, so a card that can never satisfy TryPinToTop (its content grew and it
            // no longer reaches the top) kept this subscribed forever and Avalonia killed the
            // app with "Infinite layout loop detected". Keep the cap small — these retries
            // nest inside one layout pass, unlike Tick's, which each get their own dispatcher
            // turn. Tick still does the real work across MaxAttempts.
            int layoutPasses = 0;
            void OnLayout(object? _, EventArgs __)
            {
                if (!card.IsExpanded || ++layoutPasses > MaxLayoutPasses || TryPinToTop(card))
                    sv.LayoutUpdated -= OnLayout;
            }
            sv.LayoutUpdated += OnLayout;
        }
    }

    /// <summary>
    /// Nearest scrollable ancestor. Skips ROBOT's inner Disabled viewer.
    /// Left cards use <c>LeftPanelHost</c>; right cards use the tab ScrollViewer.
    /// </summary>
    public static ScrollViewer? FindColumnScroller(Control card)
    {
        ScrollViewer? namedHost = null;
        ScrollViewer? nearestEnabled = null;
        for (var v = card.GetVisualParent(); v is not null; v = v.GetVisualParent())
        {
            if (v is not ScrollViewer sv) continue;
            if (sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
                continue;
            nearestEnabled ??= sv;
            if (sv.Name is "LeftPanelHost" or "RightPanelHost")
                namedHost = sv;
        }

        if (namedHost?.Name == "LeftPanelHost")
            return namedHost;
        return nearestEnabled ?? namedHost;
    }

    public static bool TryPinToTop(Control card)
    {
        if (FindColumnScroller(card) is not { } sv) return false;

        EnsurePad(sv, PadHeight(sv));

        double y = ContentOffsetY(card, sv);
        if (double.IsNaN(y)) return false;

        double max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        if (max < 0.5)
            max = Math.Max(y, sv.Viewport.Height);

        double target = Math.Clamp(y, 0, Math.Max(max, y));
        if (Math.Abs(target - sv.Offset.Y) >= 0.5)
            sv.Offset = new Vector(sv.Offset.X, target);

        if (card.TranslatePoint(new Point(0, 0), sv) is { } origin)
            return Math.Abs(origin.Y) < 3;
        return Math.Abs(sv.Offset.Y - target) < 0.5;
    }

    static double PadHeight(ScrollViewer sv)
    {
        double h = sv.Viewport.Height;
        if (h < 8) h = sv.Bounds.Height;
        if (h < 8) h = 720;
        // Leave a sliver so the last card's header sits at the top, not flush-cut.
        return Math.Max(120, h - 8);
    }

    static double ContentOffsetY(Control card, ScrollViewer sv)
    {
        var content = ScrollContent(sv);
        if (content is not null && card.TranslatePoint(new Point(0, 0), content) is { } inContent)
            return inContent.Y;
        if (card.TranslatePoint(new Point(0, 0), sv) is { } inView)
            return sv.Offset.Y + inView.Y;
        return double.NaN;
    }

    static Visual? ScrollContent(ScrollViewer sv)
    {
        if (sv.Content is Visual c) return Unwrap(c);
        return sv.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
    }

    static Visual Unwrap(Visual v)
    {
        if (v is Border { Child: Visual child }) return Unwrap(child);
        if (v is UserControl uc)
        {
            if (uc.Content is Visual inner) return Unwrap(inner);
            var stack = uc.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault();
            if (stack is not null) return stack;
        }
        return v;
    }

    static void EnsurePad(ScrollViewer sv, double height)
    {
        if (sv.Content is not Visual raw) return;
        if (Unwrap(raw) is not Panel panel) return;

        Control? pad = null;
        foreach (var child in panel.Children)
        {
            if (child.Name == PadName)
            {
                pad = child;
                break;
            }
        }

        if (pad is null)
        {
            pad = new Border
            {
                Name = PadName,
                Height = height,
                IsHitTestVisible = false,
                Background = Avalonia.Media.Brushes.Transparent,
            };
            panel.Children.Add(pad);
        }
        else if (Math.Abs(pad.Height - height) > 1)
        {
            pad.Height = height;
        }
    }
}
