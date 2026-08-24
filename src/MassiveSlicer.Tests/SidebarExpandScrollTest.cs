using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using MassiveSlicer.App.Behaviors;

namespace MassiveSlicer.Tests;

/// <summary>
/// Expanding Mill MORE used to crash with Avalonia's
/// "Infinite layout loop detected" because pin-to-top ran from LayoutUpdated.
/// </summary>
[Collection("headless-avalonia")]
public sealed class SidebarExpandScrollTest
{
    readonly HeadlessAvaloniaFixture _fx;
    public SidebarExpandScrollTest(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public void Expanding_last_step_card_does_not_throw_infinite_layout_loop()
    {
        _fx.OnUiThread(() =>
        {
            var more = new Expander
            {
                Classes    = { "StepCard" },
                Header     = "MORE",
                IsExpanded = false,
                Content    = new Border { Height = 900, Background = Brushes.DimGray },
            };

            var stack = new StackPanel();
            stack.Children.Add(new Border { Height = 700, Background = Brushes.Gray });
            stack.Children.Add(more);

            var sv = new ScrollViewer
            {
                Width                       = 320,
                Height                      = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content                     = stack,
            };

            var window = new Window { Width = 340, Height = 500, Content = sv };
            try
            {
                window.Show();
                window.UpdateLayout();

                more.IsExpanded = true;
                SidebarExpandScroll.Schedule(more);
                SidebarExpandScroll.Schedule(more);

                for (int i = 0; i < 40; i++)
                {
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
