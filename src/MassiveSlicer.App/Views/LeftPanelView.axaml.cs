using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MassiveSlicer.App.Views;

public partial class LeftPanelView : UserControl
{
    /// <summary>Raised when the import dropzone is clicked — the window shows the file picker.</summary>
    public event Action? ImportClickRequested;

    /// <summary>Raised with local file paths dropped onto the import dropzone.</summary>
    public event Action<string[]>? ImportFilesDropped;

    /// <summary>
    /// PersistExpander restores saved open/closed state on Loaded. Don't steal the
    /// scroll position until that restore has finished and the user expands a card.
    /// </summary>
    private bool _scrollExpandedCards;

    public LeftPanelView()
    {
        InitializeComponent();

        AttachedToVisualTree += OnAttachedToVisualTree;

        ImportDropZone.PointerPressed += (_, _) => ImportClickRequested?.Invoke();
        ImportDropZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        ImportDropZone.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?
                .Select(f => f.TryGetLocalPath())
                .Where(p => p is not null)
                .Select(p => p!)
                .ToArray();
            if (paths is { Length: > 0 })
                ImportFilesDropped?.Invoke(paths);
        });
    }

    void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        foreach (var ex in this.GetVisualDescendants().OfType<Expander>())
        {
            if (!ex.Classes.Contains("StepCard")) continue;
            ex.PropertyChanged -= OnStepCardPropertyChanged;
            ex.PropertyChanged += OnStepCardPropertyChanged;
        }

        // After PersistExpander.Loaded restore (and the first layout) allow user-driven scrolls.
        Dispatcher.UIThread.Post(() => _scrollExpandedCards = true, DispatcherPriority.ContextIdle);
    }

    void OnStepCardPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_scrollExpandedCards) return;
        if (e.Property != Expander.IsExpandedProperty) return;
        if (sender is not Expander card) return;
        if (e.GetNewValue<bool>() is not true) return;

        // Wait for the expanded body to measure so Extent can actually scroll this far.
        Dispatcher.UIThread.Post(() => ScrollCardToTop(card), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Pins <paramref name="card"/> to the top of the left-sidebar <see cref="ScrollViewer"/>
    /// so a card opened near the bottom has as much of its body on screen as possible.
    /// </summary>
    internal static void ScrollCardToTop(Control card)
    {
        if (card.FindAncestorOfType<ScrollViewer>() is not { } sv) return;
        if (card.TranslatePoint(new Point(0, 0), sv) is not { } origin) return;

        double max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        double y = Math.Clamp(sv.Offset.Y + origin.Y, 0, max);
        if (Math.Abs(y - sv.Offset.Y) < 0.5) return;
        sv.Offset = new Vector(sv.Offset.X, y);
    }

    private void JointAngle_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            e.Handled = true;
    }
}
