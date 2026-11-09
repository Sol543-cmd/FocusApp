using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FocusApp;

/// <summary>
/// The timeline browser: every event you've learned (all books) on one line. Zoom with the slider, + and −, or Ctrl and
/// the mouse wheel; scroll sideways. Click an event to read its lesson's passage on the right.
/// </summary>
public class TimelineBrowserWindow : Window
{
    private readonly List<(Course.LessonRef Lesson, PackEvent E, string Book, int Order)> _events;
    private readonly Canvas _canvas = new() { Height = 420, Background = Brushes.Transparent };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Slider _zoom = new() { Minimum = 0, Maximum = 6, Value = 0, Width = 180, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _detail = new() { Margin = new Thickness(22, 18, 22, 18) };
    private readonly int _from, _to;
    private PackEvent? _picked;

    public TimelineBrowserWindow(PackEvent? focus = null)
    {
        Title = "Focus · Timeline";
        Width = 1250; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        FontSize = 14;
        _events = Course.LearnedEvents(Course.Config.Courses.Select(c => c.Id)).OrderBy(x => x.E.Year).ToList();
        int lo = _events.Count == 0 ? -100 : _events.Min(x => x.E.Year), hi = _events.Count == 0 ? 100 : _events.Max(x => x.E.Year);
        int pad = Math.Max(10, (hi - lo) / 20);
        _from = lo - pad; _to = hi + pad;

        var root = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(24, 16, 24, 10) };
        var title = new TextBlock { Text = "Timeline", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { Text = $"{_events.Count} events from your lessons", Margin = new Thickness(14, 6, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal };
        Button Small(string t, Action a) { var b = new Button { Content = t, Width = 32, Height = 30, Margin = new Thickness(6, 0, 0, 0) }; b.Click += (_, _) => a(); return b; }
        var zl = new TextBlock { Text = "Zoom", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        zl.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        zoomRow.Children.Add(zl);
        zoomRow.Children.Add(Small("−", () => _zoom.Value = Math.Max(_zoom.Minimum, _zoom.Value - 0.5)));
        zoomRow.Children.Add(_zoom);
        zoomRow.Children.Add(Small("+", () => _zoom.Value = Math.Min(_zoom.Maximum, _zoom.Value + 0.5)));
        var close = new Button { Content = "Close", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(18, 0, 0, 0) };
        close.Click += (_, _) => Close();
        zoomRow.Children.Add(close);
        DockPanel.SetDock(zoomRow, Dock.Right);
        top.Children.Add(zoomRow);
        top.Children.Add(title);
        top.Children.Add(count);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var side = new Border { Width = 380, BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        side.SetResourceReference(Border.BorderBrushProperty, "Line");
        side.SetResourceReference(Border.BackgroundProperty, "Panel");
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);

        _scroll.Content = _canvas;
        var frame = new Border { Child = _scroll, Margin = new Thickness(24, 0, 18, 24), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top };
        frame.SetResourceReference(Border.BackgroundProperty, "Panel");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        root.Children.Add(frame);
        Content = root;

        _zoom.ValueChanged += (_, _) => Draw(keepCenter: true);
        _scroll.SizeChanged += (_, _) => Draw(keepCenter: false);
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) { _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta); e.Handled = true; return; }
            _zoom.Value = Math.Clamp(_zoom.Value + (e.Delta > 0 ? 0.5 : -0.5), _zoom.Minimum, _zoom.Maximum);
            e.Handled = true;
        };
        _picked = focus;
        Loaded += (_, _) => { Draw(false); ShowDetail(); if (focus != null) ScrollTo(focus.Year); };
        if (_events.Count == 0)
            _detail.Children.Add(Text("Nothing here yet: events join the timeline as you finish lessons that have them.", 15, "Muted"));
    }

    private double Width0 => Math.Max(400, _scroll.ActualWidth - 4);
    private double CanvasWidth => Width0 * Math.Pow(2, _zoom.Value);
    private double X(int year) => 30 + (year - _from) * (CanvasWidth - 60) / Math.Max(1, _to - _from);

    private void ScrollTo(int year) => _scroll.ScrollToHorizontalOffset(Math.Max(0, X(year) - _scroll.ActualWidth / 2));

    private void Draw(bool keepCenter)
    {
        if (_scroll.ActualWidth < 50) return;
        double centerFrac = keepCenter && _canvas.Width > 0 ? (_scroll.HorizontalOffset + _scroll.ActualWidth / 2) / _canvas.Width : 0;
        _canvas.Children.Clear();
        _canvas.Width = CanvasWidth;
        double lineY = 360;
        var axis = new Rectangle { Width = CanvasWidth - 60, Height = 3 };
        axis.SetResourceReference(Shape.FillProperty, "Muted");
        Canvas.SetLeft(axis, 30); Canvas.SetTop(axis, lineY); _canvas.Children.Add(axis);
        // Year marks: about one every 110 pixels, on round numbers.
        double perYear = (CanvasWidth - 60) / Math.Max(1, _to - _from);
        int step = new[] { 1, 2, 5, 10, 25, 50, 100, 200, 500, 1000 }.FirstOrDefault(s => s * perYear >= 110);
        if (step == 0) step = 1000;
        for (int y = (int)Math.Ceiling(_from / (double)step) * step; y <= _to; y += step)
        {
            var tick = new Rectangle { Width = 1.5, Height = 12 };
            tick.SetResourceReference(Shape.FillProperty, "Text");
            Canvas.SetLeft(tick, X(y)); Canvas.SetTop(tick, lineY - 4); _canvas.Children.Add(tick);
            var lab = Text(PackEvent.Label(y == 0 ? 1 : y), 12, "Muted");
            lab.Width = 90; lab.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(lab, X(y) - 45); Canvas.SetTop(lab, lineY + 12); _canvas.Children.Add(lab);
        }
        // Events: a dot on the line and a label above, in as many rows as needed so labels never overlap.
        var rowRight = new List<double>();
        foreach (var x in _events)
        {
            double px = X(x.E.Year);
            var label = new Border { Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.2), MaxWidth = 190, Cursor = Cursors.Hand };
            label.Child = new TextBlock { Text = x.E.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis };
            bool on = ReferenceEquals(x.E, _picked) || (_picked != null && _picked.Text == x.E.Text && _picked.Year == x.E.Year);
            label.SetResourceReference(Border.BackgroundProperty, "Panel2");
            label.SetResourceReference(Border.BorderBrushProperty, on ? "FocusBrush" : "Line");
            label.Measure(new Size(190, double.PositiveInfinity));
            double w = label.DesiredSize.Width;
            int row = rowRight.FindIndex(r => r < px - w / 2 - 6);
            if (row < 0) { row = rowRight.Count; rowRight.Add(0); }
            rowRight[row] = px + w / 2;
            double top = lineY - 56 - (row % 7) * 46;
            var stem = new Rectangle { Width = 1.5, Height = Math.Max(2, lineY - top - 34) };
            stem.SetResourceReference(Shape.FillProperty, on ? "FocusBrush" : "Line");
            Canvas.SetLeft(stem, px - 0.75); Canvas.SetTop(stem, top + 34); _canvas.Children.Add(stem);
            var dot = new Ellipse { Width = 10, Height = 10 };
            dot.SetResourceReference(Shape.FillProperty, "FocusBrush");
            Canvas.SetLeft(dot, px - 5); Canvas.SetTop(dot, lineY - 3.5); _canvas.Children.Add(dot);
            Canvas.SetLeft(label, px - w / 2); Canvas.SetTop(label, top); _canvas.Children.Add(label);
            var ev = x.E;
            label.MouseLeftButtonUp += (_, _) => { _picked = ev; Draw(keepCenter: false); ShowDetail(); };
        }
        if (keepCenter && centerFrac > 0) _scroll.ScrollToHorizontalOffset(Math.Max(0, centerFrac * _canvas.Width - _scroll.ActualWidth / 2));
    }

    /// <summary>The picked event, its lesson, and the passage around it in the book.</summary>
    private void ShowDetail()
    {
        if (_picked == null)
        {
            if (_events.Count > 0) { _detail.Children.Clear(); _detail.Children.Add(Text("Click an event to read about it.", 15, "Muted")); }
            return;
        }
        var x = _events.FirstOrDefault(e => ReferenceEquals(e.E, _picked));
        if (x.E == null) x = _events.FirstOrDefault(e => e.E.Text == _picked.Text && e.E.Year == _picked.Year);
        _detail.Children.Clear();
        if (x.E == null) return;
        _detail.Children.Add(Text(PackEvent.Label(x.E.Year), 13, "FocusBrush"));
        var head = Text(x.E.Text, 19, null); head.FontWeight = FontWeights.SemiBold; head.Margin = new Thickness(0, 4, 0, 0);
        _detail.Children.Add(head);
        if (x.E.Note.Length > 0) { var n = Text(x.E.Note, 14.5, null); n.Margin = new Thickness(0, 8, 0, 0); _detail.Children.Add(n); }
        var book = Course.Config.Courses.FirstOrDefault(c => c.Id == x.Book);
        var source = Text($"{book?.Title ?? ""} · Chapter {x.Lesson.Pack.Chapter}, lesson {x.Lesson.NumberInChapter}: {x.Lesson.Lesson.Title}", 12.5, "Muted");
        source.Margin = new Thickness(0, 14, 0, 0);
        _detail.Children.Add(source);
        // The lesson's passage: the paragraph the event is found in, with one before and two after.
        var was = Course.Current.Id;
        try
        {
            Course.UseBookQuietly(x.Book);
            int at = x.E.Find.Length > 0 ? Course.Locate(x.Lesson, new PackRef { Find = x.E.Find }) : -1;
            if (at < 0) { var (s, _) = Course.LessonRange(x.Lesson); at = s; }
            if (at >= 0)
            {
                var passage = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(14, 4, 0, 4), Margin = new Thickness(0, 14, 0, 0) };
                passage.SetResourceReference(Border.BorderBrushProperty, "FocusBrush");
                var sp = new StackPanel();
                for (int i = Math.Max(0, at - 1); i < Math.Min(Course.Book.Count, at + 3); i++)
                {
                    var para = Course.Book[i];
                    if (para.Image != null || para.Chart != null || para.Text.Trim().Length == 0) continue;
                    var t = Text(para.Text, para.Heading > 0 ? 15 : 14.5, i == at ? null : "Muted");
                    t.FontFamily = new FontFamily(Course.Config.ReadingFont);
                    t.LineHeight = 23; t.Margin = new Thickness(0, 0, 0, 10);
                    if (para.Heading > 0) t.FontWeight = FontWeights.SemiBold;
                    sp.Children.Add(t);
                }
                passage.Child = sp;
                _detail.Children.Add(passage);
            }
            else _detail.Children.Add(Text("Import this book's Word file to read the passage here.", 13.5, "Muted"));
        }
        catch (Exception ex) { Log.Error("Timeline browser passage", ex); }
        finally { Course.UseBookQuietly(was); }
    }

    private static TextBlock Text(string text, double size, string? brush)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (brush != null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }
}
