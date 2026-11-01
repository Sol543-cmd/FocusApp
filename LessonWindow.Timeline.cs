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
/// The timeline step of a lesson: drag each event onto a dated line (marks every 50 years, or finer for close dates).
/// While dragging, a bubble shows the year under the pointer; release on the year you think is right. A placed card
/// can be dragged again, or nudged a year at a time with ‹ › or the arrow keys (handy on a touchscreen).
/// Finished shows each card right (teal) or wrong (amber, with an arrow to the real date).
/// </summary>
public partial class LessonWindow
{
    private void ShowTimeline(List<(Course.LessonRef Lesson, PackEvent E)> events)
    {
        SetStep("Timeline");
        _page.MaxWidth = 1100;
        _page.Children.Add(Text("Timeline", 13, brush: "Muted"));
        _page.Children.Add(Text("Drag each card to the year you think it happened", 23, bold: true, top: 6));
        int lo = events.Min(x => x.E.Year), hi = events.Max(x => x.E.Year);
        int span = Math.Max(10, hi - lo);
        int step = span > 400 ? 100 : span > 150 ? 50 : span > 60 ? 25 : span > 25 ? 10 : 5;
        int from = (int)Math.Floor((lo - span * 0.12) / step) * step, to = (int)Math.Ceiling((hi + span * 0.12) / step) * step;
        var placed = new Dictionary<int, int>(); // event → year placed
        int selected = -1, dragging = -1;
        bool checkedDone = false;

        var canvas = new Canvas { Height = 330, ClipToBounds = true, Background = Brushes.Transparent };
        var frame = new Border { Child = canvas, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 18, 0, 0) };
        frame.SetResourceReference(Border.BackgroundProperty, "Panel");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        const double lineY = 236, pad = 40;
        double Wd() => Math.Max(100, canvas.ActualWidth) - 2 * pad;
        double X(int year) => pad + (year - from) * Wd() / (to - from);
        int YearAt(double x) => Math.Clamp((int)Math.Round(from + (x - pad) / Wd() * (to - from)), from, to);

        var tray = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var nudge = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        var results = new StackPanel();
        Button finishedBtn = null!;

        TextBlock Small(string t, string brush)
        {
            var tb = new TextBlock { Text = t, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return tb;
        }
        Border CardFor(string text, string border, string? sub = null, string subBrush = "Muted")
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            if (sub != null) sp.Children.Add(Small(sub, subBrush));
            var b = new Border { Child = sp, Width = 150, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5) };
            b.SetResourceReference(Border.BackgroundProperty, "Panel2");
            b.SetResourceReference(Border.BorderBrushProperty, border);
            return b;
        }

        // ----- Dragging -----
        Border? ghost = null, bubble = null;
        Ellipse? dot = null;
        void MoveDrag(Point at)
        {
            if (ghost == null || bubble == null || dot == null) return;
            int year = YearAt(at.X);
            ((TextBlock)bubble.Child).Text = PackEvent.Label(year);
            double top = Math.Min(at.Y - 30, lineY - 96);
            Canvas.SetLeft(ghost, at.X - 75); Canvas.SetTop(ghost, top);
            bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(bubble, at.X - bubble.DesiredSize.Width / 2); Canvas.SetTop(bubble, top - 34);
            Canvas.SetLeft(dot, X(year) - 7); Canvas.SetTop(dot, lineY - 6);
        }
        void StartDrag(int i, Point at)
        {
            if (checkedDone) return;
            dragging = i; selected = i;
            ghost = CardFor(events[i].E.Text, "FocusBrush");
            bubble = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 4), Child = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Brushes.White } };
            bubble.SetResourceReference(Border.BackgroundProperty, "FocusBrush");
            dot = new Ellipse { Width = 14, Height = 14 };
            dot.SetResourceReference(Shape.FillProperty, "FocusBrush");
            canvas.Children.Add(ghost); canvas.Children.Add(bubble); canvas.Children.Add(dot);
            canvas.CaptureMouse();
            MoveDrag(at);
        }
        void EndDrag(Point at, bool drop)
        {
            if (dragging < 0) return;
            int i = dragging; dragging = -1;
            if (canvas.IsMouseCaptured) canvas.ReleaseMouseCapture();
            if (drop && at.X > 0 && at.X < canvas.ActualWidth && at.Y > 0 && at.Y < canvas.ActualHeight) placed[i] = YearAt(at.X);
            Redraw();
        }
        canvas.MouseMove += (_, e) => { if (dragging >= 0) MoveDrag(e.GetPosition(canvas)); };
        canvas.MouseLeftButtonUp += (_, e) => EndDrag(e.GetPosition(canvas), drop: true);
        canvas.LostMouseCapture += (_, _) => { if (dragging >= 0) EndDrag(new Point(-1, -1), drop: false); };

        // ----- Drawing -----
        void Redraw()
        {
            canvas.Children.Clear(); ghost = null; bubble = null; dot = null;
            if (canvas.ActualWidth < 100) return;
            var axis = new Rectangle { Width = Wd(), Height = 3, RadiusX = 1.5, RadiusY = 1.5 };
            axis.SetResourceReference(Shape.FillProperty, "Muted");
            Canvas.SetLeft(axis, pad); Canvas.SetTop(axis, lineY); canvas.Children.Add(axis);
            int minor = Math.Max(1, step / 5);
            for (int y = from; y <= to; y += minor)
            {
                bool major = (y - from) % step == 0;
                var tick = new Rectangle { Width = major ? 2 : 1, Height = major ? 16 : 9 };
                tick.SetResourceReference(Shape.FillProperty, major ? "Text" : "Muted");
                Canvas.SetLeft(tick, X(y)); Canvas.SetTop(tick, lineY - (major ? 6 : 3)); canvas.Children.Add(tick);
                if (!major) continue;
                string text = y < 0 ? (y == from || y + step >= 0 ? $"{-y} BCE" : $"{-y}") : y == 0 ? "1 CE" : (y - step < 0 || y == from ? $"{y} CE" : $"{y}");
                var lab = Small(text, "Text"); lab.Width = 70; lab.FontSize = 12.5;
                Canvas.SetLeft(lab, X(y) - 35); Canvas.SetTop(lab, lineY + 16); canvas.Children.Add(lab);
            }
            double[] rowRight = { -1e9, -1e9, -1e9 };
            foreach (var (i, year) in placed.OrderBy(kv => kv.Value).ToList())
            {
                var ev = events[i].E;
                bool right = Math.Abs(year - ev.Year) <= ev.Within;
                double x = X(year);
                int row = Enumerable.Range(0, 3).FirstOrDefault(r => rowRight[r] < x - 80);
                rowRight[row] = x + 80;
                double top = 14 + row * 64;
                string border = !checkedDone ? (i == selected ? "FocusBrush" : "Line") : right ? "FocusBrush" : "BreakBrush";
                string? sub = !checkedDone ? PackEvent.Label(year) : right ? $"{PackEvent.Label(ev.Year)} · right" : $"You: {PackEvent.Label(year)} · real: {PackEvent.Label(ev.Year)}";
                var card = CardFor(ev.Text, border, sub, checkedDone ? (right ? "FocusBrush" : "BreakBrush") : "Muted");
                Canvas.SetLeft(card, x - 75); Canvas.SetTop(card, top); canvas.Children.Add(card);
                card.Measure(new Size(150, double.PositiveInfinity));
                var stem = new Rectangle { Width = 2, Height = Math.Max(4, lineY - top - card.DesiredSize.Height) };
                stem.SetResourceReference(Shape.FillProperty, border == "Line" ? "Muted" : border);
                Canvas.SetLeft(stem, x - 1); Canvas.SetTop(stem, top + card.DesiredSize.Height); canvas.Children.Add(stem);
                if (checkedDone && !right)
                {
                    double rx = X(ev.Year);
                    var arrow = new Line { X1 = x, Y1 = lineY - 12, X2 = rx, Y2 = lineY - 12, StrokeThickness = 2 };
                    arrow.SetResourceReference(Shape.StrokeProperty, "BreakBrush");
                    canvas.Children.Add(arrow);
                    var real = new Ellipse { Width = 12, Height = 12 };
                    real.SetResourceReference(Shape.FillProperty, "BreakBrush");
                    Canvas.SetLeft(real, rx - 6); Canvas.SetTop(real, lineY - 5); canvas.Children.Add(real);
                }
                else if (!checkedDone)
                {
                    int idx = i;
                    card.Cursor = Cursors.SizeWE;
                    card.MouseLeftButtonDown += (_, e) => { e.Handled = true; placed.Remove(idx); StartDrag(idx, e.GetPosition(canvas)); };
                }
            }
            tray.Children.Clear();
            for (int i = 0; i < events.Count; i++)
            {
                if (placed.ContainsKey(i)) continue;
                int idx = i;
                var b = CardFor(events[i].E.Text, "Line");
                b.Width = double.NaN; b.MaxWidth = 260; b.Margin = new Thickness(0, 0, 10, 10); b.Padding = new Thickness(14, 10, 14, 10); b.Cursor = Cursors.Hand;
                b.MouseLeftButtonDown += (_, e) => { e.Handled = true; StartDrag(idx, e.GetPosition(canvas)); };
                tray.Children.Add(b);
            }
            nudge.Visibility = !checkedDone && selected >= 0 && placed.ContainsKey(selected) ? Visibility.Visible : Visibility.Collapsed;
            finishedBtn.IsEnabled = !checkedDone && placed.Count == events.Count;
        }
        canvas.SizeChanged += (_, _) => { if (dragging < 0) Redraw(); };

        // ----- Nudging (touch, or the arrow keys) -----
        void Nudge(int by) { if (selected >= 0 && placed.ContainsKey(selected)) { placed[selected] = Math.Clamp(placed[selected] + by, from, to); Redraw(); } }
        var nudgeLabel = Text("Selected card:", 13, brush: "Muted");
        nudgeLabel.VerticalAlignment = VerticalAlignment.Center; nudgeLabel.Margin = new Thickness(0, 0, 10, 0);
        var back = new Button { Content = "‹ 1 year", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        back.Click += (_, _) => Nudge(-1);
        var fwd = new Button { Content = "1 year ›", Padding = new Thickness(12, 5, 12, 5) };
        fwd.Click += (_, _) => Nudge(1);
        nudge.Children.Add(nudgeLabel); nudge.Children.Add(back); nudge.Children.Add(fwd);
        _keys = k => { if (k == Key.Left) Nudge(-1); else if (k == Key.Right) Nudge(1); };

        finishedBtn = Primary("Finished", () =>
        {
            checkedDone = true;
            int right = 0;
            foreach (var (i, year) in placed)
            {
                var ev = events[i].E;
                bool ok = Math.Abs(year - ev.Year) <= ev.Within;
                if (ok) right++;
                Course.Add(new TrainingEvent { Kind = "timeline", Question = ev.Text, Correct = ok, Lesson = events[i].Lesson.Index });
            }
            _asked += events.Count; _right += right;
            MarkDone("timeline");
            Redraw();
            finishedBtn.Visibility = Visibility.Collapsed;
            results.Children.Add(Text($"{right} of {events.Count} placed close enough", 18, bold: true, top: 10));
            foreach (var (i, year) in placed.Where(kv => Math.Abs(kv.Value - events[kv.Key].E.Year) > events[kv.Key].E.Within))
            {
                var (l, ev) = events[i];
                var box = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                box.Children.Add(Text($"{ev.Text} · {PackEvent.Label(ev.Year)}", 15, bold: true));
                if (ev.Note.Length > 0) box.Children.Add(Text(ev.Note, 14, brush: "Muted", top: 2));
                if (ev.Find.Length > 0) { var see = Link("See it in the book", () => ShowWhere(l, new PackRef { Find = ev.Find })); see.HorizontalAlignment = HorizontalAlignment.Left; see.Margin = new Thickness(0, 4, 0, 0); box.Children.Add(see); }
                results.Children.Add(box);
            }
            if (right < events.Count) results.Children.Add(Text("The ones you missed come back first in the next timeline.", 13, brush: "Muted", top: 10));
            results.Children.Add(Primary("Next", Next));
        });
        finishedBtn.IsEnabled = false;

        _page.Children.Add(frame);
        _page.Children.Add(nudge);
        _page.Children.Add(Text("Still to place", 13, brush: "Muted", top: 14));
        _page.Children.Add(tray);
        _page.Children.Add(finishedBtn);
        _page.Children.Add(results);
    }
}
