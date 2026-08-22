using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MassiveSlicer.App.Behaviors;
using MassiveSlicer.ViewModels;

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
    public bool AllowExpandScroll { get; private set; }

    static LeftPanelView() => SidebarExpandScroll.Arm();

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
        Dispatcher.UIThread.Post(() => AllowExpandScroll = true, DispatcherPriority.ContextIdle);
    }

    private void JointAngle_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            e.Handled = true;
    }

}
