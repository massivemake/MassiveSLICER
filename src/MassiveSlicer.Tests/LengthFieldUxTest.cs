using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MassiveSlicer.App;
using MassiveSlicer.Controls;

namespace MassiveSlicer.Tests;

/// <summary>
/// Typing a length with units into real measurement fields (headless): the field ends up holding
/// millimetres, fields not tagged <c>Length</c> are untouched, and a malformed length never
/// changes the value.
/// </summary>
[Collection("headless-avalonia")]
public class LengthFieldUxTest
{
    private readonly HeadlessAvaloniaFixture _ui;
    private static bool _installed;

    public LengthFieldUxTest(HeadlessAvaloniaFixture ui)
    {
        _ui = ui;
        _ui.OnUiThread(() =>
        {
            if (_installed) return;
            LengthFieldUx.Install();
            _installed = true;
        });
    }

    private sealed class Vm : INotifyPropertyChanged
    {
        private double _mm = 100;
        public double Mm { get => _mm; set { _mm = value; Changed(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
    }

    private static (Window w, T field, TextBox other) Show<T>(T field) where T : Control
    {
        var other = new TextBox { Width = 120, Height = 28 };
        var w = new Window { Width = 400, Height = 200, Content = new StackPanel { Children = { field, other } } };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, field, other);
    }

    private static void Commit(TextBox other)
    {
        other.Focus();                // focus leaving the field is a commit
        Dispatcher.UIThread.RunJobs();
    }

    [Theory]
    [InlineData("10in", 254)]
    [InlineData("5 1/2\"", 139.7)]
    [InlineData("2ft", 609.6)]
    [InlineData("12.5cm", 125)]
    [InlineData("5 1/2", 5.5)]          // no unit = mm, fraction and all
    public void Text_field_tagged_Length_stores_millimetres(string typed, double mm)
    {
        _ui.OnUiThread(() =>
        {
            var vm = new Vm();
            var tb = new TextBox { Classes = { LengthFieldUx.LengthClass }, DataContext = vm, Width = 120, Height = 28 };
            tb.Bind(TextBox.TextProperty, new Binding(nameof(Vm.Mm)) { Mode = BindingMode.TwoWay });
            var (_, field, other) = Show(tb);

            field.Focus();
            field.Text = typed;
            Commit(other);

            Assert.Equal(mm, vm.Mm, 3);
            Assert.Equal(LengthFieldUx.FormatMm(mm), field.Text);
        });
    }

    [Fact]
    public void Untagged_field_is_left_alone()
    {
        _ui.OnUiThread(() =>
        {
            var vm = new Vm();
            var tb = new TextBox { DataContext = vm, Width = 120, Height = 28 };   // e.g. a degrees field
            tb.Bind(TextBox.TextProperty, new Binding(nameof(Vm.Mm)) { Mode = BindingMode.TwoWay });
            var (_, field, other) = Show(tb);

            field.Focus();
            field.Text = "10in";
            Commit(other);

            Assert.Equal(100, vm.Mm);          // never became 254
            Assert.Equal("10in", field.Text);
        });
    }

    [Fact]
    public void Malformed_length_keeps_the_value_and_flags_the_field()
    {
        _ui.OnUiThread(() =>
        {
            var vm = new Vm();
            var tb = new TextBox { Classes = { LengthFieldUx.LengthClass }, DataContext = vm, Width = 120, Height = 28 };
            tb.Bind(TextBox.TextProperty, new Binding(nameof(Vm.Mm)) { Mode = BindingMode.TwoWay });
            var (_, field, other) = Show(tb);

            field.Focus();
            field.Text = "51/2in";
            Commit(other);

            Assert.Equal(100, vm.Mm);
            Assert.True(DataValidationErrors.GetHasErrors(field));
        });
    }

    [Fact]
    public void Spinner_tagged_Length_reads_units()
    {
        _ui.OnUiThread(() =>
        {
            var nud = new NumericUpDown { Classes = { LengthFieldUx.LengthClass }, Value = 100, FormatString = "F1", Width = 160, Height = 30 };
            var (_, field, other) = Show(nud);

            Assert.IsType<LengthTextConverter>(field.TextConverter);
            // Type where a user types: the spinner's own text box.
            var inner = field.GetVisualDescendants().OfType<TextBox>().First();
            inner.Focus();
            inner.Text = "10in";
            Commit(other);

            Assert.Equal(254m, field.Value);
        });
    }

    [Fact]
    public void Move_box_tagged_Length_reads_units_and_still_does_sums()
    {
        _ui.OnUiThread(() =>
        {
            var box = new TransformNumberBox { Classes = { LengthFieldUx.LengthClass }, Value = 100, FormatString = "F2", Width = 120, Height = 28 };
            var (_, field, other) = Show(box);

            field.Focus();
            field.Text = "1' 6\"";
            Commit(other);
            Assert.Equal(457.2, field.Value, 3);

            field.Focus();
            field.Text = "100+50";      // plain arithmetic is left to the box's own evaluator
            Commit(other);
            Assert.Equal(150, field.Value, 3);
        });
    }
}
