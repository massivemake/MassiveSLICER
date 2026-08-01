using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using MassiveSlicer.Controls;

namespace MassiveSlicer.Tests;

/// <summary>Minimal Avalonia app so the headless driver can lay out and route input.</summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
}

/// <summary>
/// One headless Avalonia session, shared by every test in the collection.
/// </summary>
/// <remarks>
/// Set up by hand rather than with Avalonia.Headless.XUnit, whose xunit.v3 dependency collides with
/// this project's xunit 2 and breaks the rest of the suite. Avalonia can only be initialised once
/// per process and is thread-affine, so the session lives in a collection fixture and the tests
/// that use it are serialised onto its thread.
/// </remarks>
public sealed class HeadlessAvaloniaFixture : IDisposable
{
    private readonly Thread _uiThread;
    private readonly SemaphoreSlim _ready = new(0);
    private readonly CancellationTokenSource _stop = new();

    public HeadlessAvaloniaFixture()
    {
        _uiThread = new Thread(() =>
        {
            AppBuilder.Configure<HeadlessTestApp>()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                      .SetupWithoutStarting();
            _ready.Release();
            Dispatcher.UIThread.MainLoop(_stop.Token);
        })
        {
            IsBackground = true,
            Name         = "headless-avalonia",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(30));
    }

    /// <summary>Runs <paramref name="action"/> on the Avalonia thread and rethrows anything it throws.</summary>
    public void OnUiThread(Action action)
    {
        Exception? failure = null;
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        }).GetTask().Wait(TimeSpan.FromSeconds(30));
        if (failure is not null) throw failure;
    }

    public void Dispose() => _stop.Cancel();
}

[CollectionDefinition("headless-avalonia")]
public sealed class HeadlessAvaloniaCollection : ICollectionFixture<HeadlessAvaloniaFixture> { }

/// <summary>
/// Drives real pointer input at a <see cref="TransformNumberBox"/>.
/// </summary>
/// <remarks>
/// The click behaviour was "fixed" twice without ever being observed — the console bridge cannot
/// reach the Avalonia overlay layer, so both attempts were guesses judged by eye, and both were
/// wrong. These click the control for real and read the selection back, so what it does is a
/// measurement rather than an opinion.
/// </remarks>
[Collection("headless-avalonia")]
public class TransformNumberBoxClickTest
{
    private readonly HeadlessAvaloniaFixture _ui;

    public TransformNumberBoxClickTest(HeadlessAvaloniaFixture ui) => _ui = ui;

    private static (Window Window, TransformNumberBox Box, TextBox Other) Show(double value)
    {
        var box = new TransformNumberBox
        {
            Value        = value,
            FormatString = "F2",
            Width        = 120,
            Height       = 28,
        };
        // Something else to click, for "focus has gone elsewhere".
        var other  = new TextBox { Width = 120, Height = 28 };
        var window = new Window
        {
            Width   = 400,
            Height  = 200,
            Content = new StackPanel { Children = { box, other } },
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, box, other);
    }

    /// <summary>
    /// Clicks <paramref name="target"/> at <paramref name="atFractionOfWidth"/> across it.
    /// </summary>
    /// <remarks>
    /// The fraction matters: two clicks at the same spot in quick succession are a double-click,
    /// and selecting a word is the right answer to one of those. There is no virtual clock here to
    /// space them out in time, so consecutive clicks are spaced out in distance instead — well past
    /// the double-click slop — to make them the two separate clicks a user would perform.
    /// </remarks>
    private static void Click(Window window, Control target, double atFractionOfWidth = 0.5)
    {
        var topLeft = target.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);
        var point   = topLeft + new Point(
            target.Bounds.Width * atFractionOfWidth, target.Bounds.Height / 2);

        window.MouseDown(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static (int Start, int Length) Selection(TextBox box)
    {
        int lo = Math.Min(box.SelectionStart, box.SelectionEnd);
        int hi = Math.Max(box.SelectionStart, box.SelectionEnd);
        return (lo, hi - lo);
    }

    [Fact]
    public void First_click_selects_the_whole_value_including_the_decimals()
    {
        _ui.OnUiThread(() =>
        {
            var (window, box, _) = Show(1847.40);

            Click(window, box);

            Assert.True(box.IsFocused, "the click never focused the box");
            Assert.Equal("1847.40", box.Text);
            var (start, length) = Selection(box);
            Assert.Equal(0, start);
            Assert.Equal(box.Text!.Length, length);
        });
    }

    [Fact]
    public void Typing_after_the_first_click_replaces_the_whole_value()
    {
        // The reason selecting everything matters: the next keystroke overwrites rather than
        // inserting into the middle of the old number.
        _ui.OnUiThread(() =>
        {
            var (window, box, _) = Show(1847.40);

            Click(window, box);
            window.KeyTextInput("5");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("5", box.Text);
        });
    }

    [Fact]
    public void A_second_click_drops_the_highlight_so_arithmetic_can_be_typed()
    {
        // Locked spec: first click selects, second places the caret — which is how "+90" gets
        // written against a value that is still on screen.
        _ui.OnUiThread(() =>
        {
            var (window, box, _) = Show(1847.40);

            Click(window, box, atFractionOfWidth: 0.15);
            Click(window, box, atFractionOfWidth: 0.85);

            Assert.True(box.IsFocused);
            Assert.Equal(0, Selection(box).Length);
        });
    }

    [Fact]
    public void Coming_back_to_the_box_selects_everything_again()
    {
        _ui.OnUiThread(() =>
        {
            var (window, box, other) = Show(1847.40);

            Click(window, box);
            Click(window, other);
            Assert.False(box.IsFocused);

            Click(window, box);

            Assert.True(box.IsFocused);
            var (start, length) = Selection(box);
            Assert.Equal(0, start);
            Assert.Equal(box.Text!.Length, length);
        });
    }
}
