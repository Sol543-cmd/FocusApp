using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>A number field with up/down arrows. Arrow keys and the mouse wheel (while typing in it) also work.</summary>
public class NumberBox : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumberBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((NumberBox)d).ShowValue()));

    private readonly TextBox _box;
    private readonly TextBlock _suffix;

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum { get; set; } = 1;
    public int Maximum { get; set; } = 600;
    public int Step { get; set; } = 1;

    public string Suffix
    {
        get => _suffix.Text;
        set
        {
            _suffix.Text = value;
            _suffix.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public NumberBox()
    {
        _box = new TextBox
        {
            MinWidth = 40,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 5, 2, 5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _box.SetResourceReference(TextBox.ForegroundProperty, "Text");
        _box.SetResourceReference(TextBox.CaretBrushProperty, "Text");
        _box.LostFocus += (_, _) => Commit();
        _box.PreviewKeyDown += Box_PreviewKeyDown;
        _box.PreviewMouseWheel += (_, e) =>
        {
            if (!_box.IsKeyboardFocusWithin) return; // don't hijack page scrolling
            Change(e.Delta > 0 ? Step : -Step);
            e.Handled = true;
        };

        _suffix = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0), FontSize = 12 };
        _suffix.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Suffix = "";

        var up = MakeArrow("▲", Step);
        var down = MakeArrow("▼", -Step);
        var arrows = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) };
        arrows.Children.Add(up);
        arrows.Children.Add(down);

        var dock = new DockPanel();
        DockPanel.SetDock(arrows, Dock.Right);
        DockPanel.SetDock(_suffix, Dock.Right);
        dock.Children.Add(arrows);
        dock.Children.Add(_suffix);
        dock.Children.Add(_box);

        var border = new Border { CornerRadius = new CornerRadius(8), Child = dock, MinHeight = 32 };
        border.SetResourceReference(Border.BackgroundProperty, "Panel3");
        Content = border;
        ShowValue();
    }

    private RepeatButton MakeArrow(string glyph, int direction)
    {
        var b = new RepeatButton { Content = glyph, FontSize = 8, Height = 13, Width = 20 };
        b.SetResourceReference(StyleProperty, "SpinButton");
        b.Click += (_, _) => Change(direction > 0 ? Step : -Step);
        return b;
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Commit(); e.Handled = true; break;
            case Key.Up: Change(Step); e.Handled = true; break;
            case Key.Down: Change(-Step); e.Handled = true; break;
        }
    }

    private void Change(int delta)
    {
        Commit();
        Value = Math.Clamp(Value + delta, Minimum, Maximum);
        ShowValue();
    }

    /// <summary>Applies whatever was typed (or puts back the last good value).</summary>
    public void Commit()
    {
        if (int.TryParse(_box.Text.Trim(), out int typed)) Value = Math.Clamp(typed, Minimum, Maximum);
        ShowValue();
    }

    private void ShowValue()
    {
        string text = Value.ToString();
        if (_box.Text != text) _box.Text = text;
    }
}
