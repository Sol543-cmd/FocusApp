using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// The book lesson's popup: like Greek practice, it covers the screen you're working on, with Start lesson and (except
/// at the day's last lesson time) Later in the top right. The last one can't be closed: only quitting Focus puts it off,
/// and it comes back each time Focus starts that day until the lesson is done.
/// </summary>
public sealed class LessonPromptWindow : Window
{
    private readonly System.Drawing.Rectangle _screen;
    private bool _closingOk;

    public LessonPromptWindow(System.Drawing.Rectangle screen, string label, string title, string meta, string startText,
                              string? laterText, string? note, Action start, Action? later)
    {
        _screen = screen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Title = "Book lesson";
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");

        var root = new Grid();
        if (laterText != null && later != null)
        {
            var laterBtn = new Button { Content = laterText, Padding = new Thickness(20, 10, 20, 10), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right,
                                        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 24, 28, 0) };
            laterBtn.Click += (_, _) => later();
            root.Children.Add(laterBtn);
        }
        var stack = new StackPanel();
        var labelText = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold };
        if (note != null) labelText.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xB8, 0x66));
        else labelText.SetResourceReference(TextBlock.ForegroundProperty, "FocusBrush");
        stack.Children.Add(labelText);
        stack.Children.Add(new TextBlock { Text = title, FontSize = 30, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) });
        if (meta.Length > 0)
        {
            var m = new TextBlock { Text = meta, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
            m.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            stack.Children.Add(m);
        }
        if (note != null)
        {
            stack.Children.Add(new Border
            {
                Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2A, 0x22)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x4A, 0x2E)),
                Child = new TextBlock { Text = note, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xB8, 0x66)) }
            });
        }
        var go = new Button { Content = startText, Padding = new Thickness(34, 14, 34, 14), FontSize = 16, Margin = new Thickness(0, 22, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsDefault = true };
        go.SetResourceReference(StyleProperty, "PrimaryButton");
        go.Click += (_, _) => start();
        stack.Children.Add(go);
        var card = new Border { Child = stack, Width = 620, Padding = new Thickness(44, 40, 44, 40), CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1),
                                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        root.Children.Add(card);
        Content = root;

        SourceInitialized += (_, _) => FitToScreen();
        Loaded += (_, _) => { FitToScreen(); Activate(); go.Focus(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && later != null) { later(); e.Handled = true; } };
        // Only Start, Later or quitting Focus close it (Alt+F4 doesn't).
        Closing += (_, e) => { if (!_closingOk && !Application.Current.Dispatcher.HasShutdownStarted) e.Cancel = true; };
    }

    /// <summary>Covers the whole screen, in real pixels (works with any display scaling).</summary>
    private void FitToScreen()
    {
        var source = PresentationSource.FromVisual(this);
        double sx = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1, sy = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
        Left = _screen.Left * sx; Top = _screen.Top * sy; Width = _screen.Width * sx; Height = _screen.Height * sy;
    }

    public void CloseNow()
    {
        _closingOk = true;
        Close();
    }
}
