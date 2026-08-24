using System.Collections.Generic;
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
///
/// A bottom pad is added only for the amount needed so that card can reach
/// the top — not a permanent full-viewport spacer (that left blank scroll
/// under every sidebar). Pad shrinks when cards collapse.
/// </summary>
public static class SidebarExpandScroll
{
    const string PadName = "__SidebarScrollPad";
    const int MaxAttempts = 30;

    static bool _armed;

    /// <summary>Call once from each sidebar UserControl so the class handler exists.</summary>
    public static void Arm()
    {
        if (_armed) return;
        _armed = true;
        Expander.IsExpandedProperty.Changed.AddClassHandler<Expander>(OnExpandedChanged);
    }

    static void OnExpandedChanged(Expander card, AvaloniaPropertyChangedEventArgs e)
    {
        if (!card.Classes.Contains("StepCard")) return;
        if (!UserScrollAllowed(card)) return;

        if (IsNowExpanded(e))
        {
            Schedule(card);
            return;
        }

        // Collapsed: drop unused blank pad and clamp scroll to real content.
        if (FindColumnScroller(card) is { } sv)
            ShrinkPadAndClamp(sv);
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

    static readonly HashSet<Expander> _pinningCards = [];
    static bool _inPin;

    public static void Schedule(Expander card)
    {
        // One session per card. Re-entry from our own pad/offset layout
        // used to subscribe LayoutUpdated without incrementing attempts —
        // Mill MORE (last card, lots of newly visible nested expanders)
        // then hit Avalonia's "Infinite layout loop detected" and crashed.
        if (!_pinningCards.Add(card)) return;

        card.InvalidateMeasure();
        card.InvalidateArrange();

        var sv = FindColumnScroller(card);
        if (sv is not null)
        {
            // Minimal pad for this card only (0 when content already fills the column).
            EnsurePad(sv, NeededPad(sv, card));
            sv.InvalidateMeasure();
        }

        int attempts = 0;
        void Finish()
        {
            _pinningCards.Remove(card);
            // After pin, keep only what the still-expanded cards need — no leftover blank.
            if (FindColumnScroller(card) is { } scroller)
                ShrinkPadAndClamp(scroller);
        }

        void Tick()
        {
            if (!card.IsExpanded)
            {
                Finish();
                return;
            }

            var scroller = FindColumnScroller(card);
            double need = scroller is null ? 0 : NeededPad(scroller, card);
            if (scroller is not null)
                EnsurePad(scroller, need);

            if (TryPinToTop(card) || ++attempts >= MaxAttempts)
            {
                Finish();
                return;
            }
            Dispatcher.UIThread.Post(Tick, DispatcherPriority.Background);
        }

        // Background retries only. Do not hook LayoutUpdated: changing pad
        // or Offset from that callback invalidates layout and loops forever.
        Dispatcher.UIThread.Post(Tick, DispatcherPriority.Background);
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
        if (_inPin) return false;
        if (FindColumnScroller(card) is not { } sv) return false;

        _inPin = true;
        try
        {
            EnsurePad(sv, NeededPad(sv, card));

            double y = ContentOffsetY(card, sv);
            if (double.IsNaN(y)) return false;

            double max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            double target = Math.Clamp(y, 0, max);
            if (Math.Abs(target - sv.Offset.Y) >= 0.5)
                sv.Offset = new Vector(sv.Offset.X, target);

            if (card.TranslatePoint(new Point(0, 0), sv) is { } origin)
                return Math.Abs(origin.Y) < 3;
            return Math.Abs(sv.Offset.Y - target) < 0.5;
        }
        finally
        {
            _inPin = false;
        }
    }

    /// <summary>
    /// Pad so Extent can reach cardY + viewport (pin card to top).
    /// Zero when the stack already fills the column past that point —
    /// no permanent blank scroll under short sidebars.
    /// </summary>
    static double NeededPad(ScrollViewer sv, Control card)
    {
        double viewport = sv.Viewport.Height;
        if (viewport < 8) viewport = sv.Bounds.Height;
        if (viewport < 8) viewport = 720;

        double y = ContentOffsetY(card, sv);
        if (double.IsNaN(y)) y = 0;

        double real = RealContentHeight(sv);
        // Leave a few px so the header is not flush-cut against the bottom clip.
        double need = y + viewport - real - 4;
        if (need < 1) return 0;
        return need;
    }

    /// <summary>Stack height excluding the expandable blank pad.</summary>
    static double RealContentHeight(ScrollViewer sv)
    {
        if (sv.Content is not Visual raw) return Math.Max(0, sv.Extent.Height);
        if (Unwrap(raw) is not Panel panel) return Math.Max(0, sv.Extent.Height);

        double h = 0;
        foreach (var child in panel.Children)
        {
            if (child.Name == PadName) continue;
            if (!child.IsVisible) continue;
            double ch = child.Bounds.Height;
            if (ch < 0.5) ch = child.DesiredSize.Height;
            h += ch;
            if (child is Control c)
                h += c.Margin.Top + c.Margin.Bottom;
        }

        if (h < 1)
            h = Math.Max(0, sv.Extent.Height - CurrentPadHeight(panel));
        return h;
    }

    static double CurrentPadHeight(Panel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child.Name == PadName)
                return child.Height;
        }
        return 0;
    }

    /// <summary>
    /// Pad required by any still-expanded StepCard in this scroller (usually 0).
    /// </summary>
    static double MaxNeededPad(ScrollViewer sv)
    {
        double max = 0;
        foreach (var exp in sv.GetVisualDescendants().OfType<Expander>())
        {
            if (!exp.Classes.Contains("StepCard") || !exp.IsExpanded) continue;
            max = Math.Max(max, NeededPad(sv, exp));
        }
        return max;
    }

    static void ShrinkPadAndClamp(ScrollViewer sv)
    {
        if (_inPin) return;
        double need = MaxNeededPad(sv);
        EnsurePad(sv, need);

        // Clamp after pad shrink so Offset cannot sit in removed blank.
        void Clamp()
        {
            double viewport = sv.Viewport.Height;
            if (viewport < 8) viewport = sv.Bounds.Height;
            double max = Math.Max(0, RealContentHeight(sv) + need - Math.Max(1, viewport));
            // Prefer live Extent when layout has caught up.
            double extentMax = Math.Max(0, sv.Extent.Height - Math.Max(1, viewport));
            if (extentMax > 0.5)
                max = Math.Min(max, extentMax);
            if (sv.Offset.Y > max + 0.5)
                sv.Offset = new Vector(sv.Offset.X, max);
        }

        Clamp();
        Dispatcher.UIThread.Post(Clamp, DispatcherPriority.Background);
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

        if (height < 1)
        {
            if (pad is not null)
                panel.Children.Remove(pad);
            return;
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
        else if (Math.Abs(pad.Height - height) > 4)
        {
            pad.Height = height;
        }
    }
}
