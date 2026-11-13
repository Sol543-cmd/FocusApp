using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FocusApp;

/// <summary>
/// The timeline, two ways.
/// In a lesson: the events are listed with a box beside each; type the year you think (4 BC, -4, 30, AD 30) and press
/// Enter: the card appears on the timeline and the next box is ready. Finished shows each card right (teal) or wrong
/// (amber, with an arrow to the real date).
/// Timeline practice: like Greek word practice. A few events on one side, their dates plus a couple of extra dates on the
/// other; pick one, then the other, and the pair drops onto the timeline. Check (or Enter) marks wrong pairs on the
/// timeline without telling the answer, and those come back to pair again; right ones stay on the timeline all round.
/// </summary>
public partial class LessonWindow
{
    /// <summary>A year typed as 4 BC, 4 BCE, -4, 30, 30 AD, AD 30, c. 30 or 30 CE (null if it isn't one).</summary>
    public static int? ParseYear(string text)
    {
        var t = text.Trim().ToUpperInvariant().Replace(".", "").Replace(",", "");
        if (t.StartsWith("C ") || t.StartsWith("CA ")) t = t[(t.IndexOf(' ') + 1)..];
        bool bce = Regex.IsMatch(t, @"B\s*C\s*E?$|^-");
        t = Regex.Replace(t, @"B\s*C\s*E?|C\s*E|A\s*D|-|\s", "");
        if (!int.TryParse(t, out int y) || y > 3000) return null;
        return bce ? -Math.Abs(y) : y;
    }

    /// <summary>A card on the timeline: its text, where it sits, its border and a small line under the text.</summary>
    private sealed record TlMark(string Text, int Year, string Border, string? Sub = null, string SubBrush = "Muted", int? Real = null, Action? Click = null);

    /// <summary>The dated line with its cards, laid out in as many rows as it takes so no card covers another.</summary>
    private sealed class TimelineView : Border
    {
        private readonly Canvas _canvas = new() { Height = 300, ClipToBounds = true, Background = Brushes.Transparent };
        private readonly int _from, _to, _step;
        private const double Pad = 40;
        public List<TlMark> Marks { get; } = new();

        public TimelineView(IEnumerable<int> years)
        {
            var ys = years.ToList();
            int lo = ys.Min(), hi = ys.Max();
            int span = Math.Max(10, hi - lo);
            _step = span > 400 ? 100 : span > 150 ? 50 : span > 60 ? 25 : span > 25 ? 10 : 5;
            _from = (int)Math.Floor((lo - span * 0.12) / _step) * _step;
            _to = (int)Math.Ceiling((hi + span * 0.12) / _step) * _step;
            Child = _canvas;
            CornerRadius = new CornerRadius(14); BorderThickness = new Thickness(1); Margin = new Thickness(0, 16, 0, 0);
            SetResourceReference(BackgroundProperty, "Panel");
            SetResourceReference(BorderBrushProperty, "Line");
            _canvas.SizeChanged += (_, _) => Redraw();
        }

        private double Wd() => Math.Max(100, _canvas.ActualWidth) - 2 * Pad;
        private double X(int year) => Pad + (Math.Clamp(year, _from, _to) - _from) * Wd() / (_to - _from);

        private static TextBlock Small(string t, string brush)
        {
            var tb = new TextBlock { Text = t, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return tb;
        }

        public void Redraw()
        {
            _canvas.Children.Clear();
            if (_canvas.ActualWidth < 100) return;
            var layout = new List<(TlMark M, double X, int Row, Border Card)>();
            var rowRight = new List<double>();
            var rowHeight = new List<double>();
            foreach (var m in Marks.OrderBy(m => m.Year))
            {
                double x = X(m.Year);
                int row = rowRight.FindIndex(r => r < x - 80);
                if (row < 0) { row = rowRight.Count; rowRight.Add(-1e9); rowHeight.Add(0); }
                rowRight[row] = x + 80;
                var sp = new StackPanel();
                var main = new TextBlock { Text = m.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
                main.SetResourceReference(TextBlock.ForegroundProperty, "Text");
                sp.Children.Add(main);
                if (m.Sub != null) sp.Children.Add(Small(m.Sub, m.SubBrush));
                var card = new Border { Child = sp, Width = 150, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5) };
                card.SetResourceReference(Border.BackgroundProperty, "Panel2");
                card.SetResourceReference(Border.BorderBrushProperty, m.Border);
                if (m.Click is Action click) { card.Cursor = Cursors.Hand; card.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); }; }
                card.Measure(new Size(150, double.PositiveInfinity));
                rowHeight[row] = Math.Max(rowHeight[row], card.DesiredSize.Height);
                layout.Add((m, x, row, card));
            }
            double rowsTall = rowHeight.Sum() + 10 * Math.Max(0, rowHeight.Count - 1);
            double lineY = Math.Max(150, 14 + rowsTall + 30);
            _canvas.Height = lineY + 50;
            var axis = new Rectangle { Width = Wd(), Height = 3, RadiusX = 1.5, RadiusY = 1.5 };
            axis.SetResourceReference(Shape.FillProperty, "Muted");
            Canvas.SetLeft(axis, Pad); Canvas.SetTop(axis, lineY); _canvas.Children.Add(axis);
            int minor = Math.Max(1, _step / 5);
            for (int y = _from; y <= _to; y += minor)
            {
                bool major = (y - _from) % _step == 0;
                var tick = new Rectangle { Width = major ? 2 : 1, Height = major ? 16 : 9 };
                tick.SetResourceReference(Shape.FillProperty, major ? "Text" : "Muted");
                Canvas.SetLeft(tick, X(y)); Canvas.SetTop(tick, lineY - (major ? 6 : 3)); _canvas.Children.Add(tick);
                if (!major) continue;
                string text = y < 0 ? (y == _from || y + _step >= 0 ? $"{-y} BCE" : $"{-y}") : y == 0 ? "1 CE" : (y - _step < 0 || y == _from ? $"{y} CE" : $"{y}");
                var lab = Small(text, "Text"); lab.Width = 70; lab.FontSize = 12.5;
                Canvas.SetLeft(lab, X(y) - 35); Canvas.SetTop(lab, lineY + 16); _canvas.Children.Add(lab);
            }
            // Stems first (behind every card), then the cards, so no stem crosses a card's text.
            foreach (var (m, x, row, card) in layout)
            {
                double stop = 14 + rowHeight.Take(row).Sum() + 10 * row;
                var stem = new Rectangle { Width = 2, Height = Math.Max(4, lineY - stop - card.DesiredSize.Height) };
                stem.SetResourceReference(Shape.FillProperty, m.Border == "Line" ? "Muted" : m.Border);
                Canvas.SetLeft(stem, x - 1); Canvas.SetTop(stem, stop + card.DesiredSize.Height); _canvas.Children.Add(stem);
            }
            foreach (var (m, x, row, card) in layout)
            {
                double top = 14 + rowHeight.Take(row).Sum() + 10 * row;
                Canvas.SetLeft(card, Math.Clamp(x - 75, 0, Math.Max(0, _canvas.ActualWidth - 150))); Canvas.SetTop(card, top); _canvas.Children.Add(card);
                if (m.Real is int real)
                {
                    double rx = X(real);
                    var arrow = new Line { X1 = x, Y1 = lineY - 12, X2 = rx, Y2 = lineY - 12, StrokeThickness = 2 };
                    arrow.SetResourceReference(Shape.StrokeProperty, "BreakBrush");
                    _canvas.Children.Add(arrow);
                    var dot = new Ellipse { Width = 12, Height = 12 };
                    dot.SetResourceReference(Shape.FillProperty, "BreakBrush");
                    Canvas.SetLeft(dot, rx - 6); Canvas.SetTop(dot, lineY - 5); _canvas.Children.Add(dot);
                }
            }
        }
    }

    private void RecordTimeline((Course.LessonRef Lesson, PackEvent E) item, int year, bool ok)
    {
        int off = Math.Abs(year - item.E.Year);
        if (off == 0) _exact++;
        // Grade: how many years off (timeline practice spaces each event by it).
        Course.Add(new TrainingEvent { Kind = "timeline", Question = item.E.Text, Correct = ok, Grade = off, Lesson = item.Lesson.Index,
                                       CourseId = Course.EventBook.TryGetValue(item.E, out var book) ? book : "" });
    }

    // ================= In a lesson: type each date =================

    private void ShowTimeline(List<(Course.LessonRef Lesson, PackEvent E)> events)
    {
        if (_timelineOnly != null) { ShowTimelineMatch(events); return; }
        SetStep("Timeline");
        _page.MaxWidth = 1100;
        _page.Children.Add(Text("Timeline", 13, brush: "Muted"));
        _page.Children.Add(Text("When did each happen?", 23, bold: true, top: 6));
        _page.Children.Add(Text("Type a year (like 4 BC, 30 or AD 30) and press Enter. Your card appears on the timeline and the next box is ready.", 14.5, brush: "Muted", top: 4));
        var placed = new Dictionary<int, int>();
        bool checkedDone = false;
        var view = new TimelineView(events.Select(e => e.E.Year));
        var boxes = new List<TextBox>();
        var states = new List<TextBlock>();
        var results = new StackPanel();
        Button finishedBtn = null!;

        void Redraw()
        {
            view.Marks.Clear();
            foreach (var (i, year) in placed)
            {
                var ev = events[i].E;
                int idx = i;
                bool right = Math.Abs(year - ev.Year) <= ev.Within;
                if (!checkedDone) view.Marks.Add(new TlMark(ev.Text, year, "Line", PackEvent.Label(year), Click: () => { boxes[idx].Focus(); boxes[idx].SelectAll(); }));
                else view.Marks.Add(new TlMark(ev.Text, year, right ? "FocusBrush" : "BreakBrush",
                    right ? (year == ev.Year ? $"Right · {PackEvent.Label(ev.Year)}" : $"Right · exact: {PackEvent.Label(ev.Year)}") : $"You: {PackEvent.Label(year)} · real: {PackEvent.Label(ev.Year)}",
                    right ? "FocusBrush" : "BreakBrush", right ? null : ev.Year));
            }
            view.Redraw();
            finishedBtn.IsEnabled = !checkedDone && placed.Count == events.Count;
        }

        var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        for (int i = 0; i < events.Count; i++)
        {
            int idx = i;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            var frame = new Border { Child = row, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8) };
            frame.SetResourceReference(Border.BackgroundProperty, "Panel");
            frame.SetResourceReference(Border.BorderBrushProperty, "Line");
            row.Margin = new Thickness(0);
            var name = new TextBlock { Text = events[i].E.Text, FontSize = 14.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var box = new TextBox { FontSize = 14, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "A year: 4 BC, 30, AD 30" };
            var state = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
            state.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Grid.SetColumn(box, 1); Grid.SetColumn(state, 2);
            row.Children.Add(name); row.Children.Add(box); row.Children.Add(state);
            box.GotKeyboardFocus += (_, _) => { frame.SetResourceReference(Border.BorderBrushProperty, "FocusBrush"); if (!checkedDone && !placed.ContainsKey(idx)) state.Text = "Enter ↵"; };
            box.LostKeyboardFocus += (_, _) => { frame.SetResourceReference(Border.BorderBrushProperty, "Line"); if (!placed.ContainsKey(idx) && state.Text == "Enter ↵") state.Text = ""; };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || checkedDone) return;
                e.Handled = true;
                if (ParseYear(box.Text) is not int year) { state.Text = "Type a year, like 4 BC or 70"; state.SetResourceReference(TextBlock.ForegroundProperty, "BreakBrush"); return; }
                placed[idx] = year;
                box.Text = PackEvent.Label(year);
                state.Text = "On the timeline"; state.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                Redraw();
                // The next box still empty (after this one, then from the top); all done: Finished.
                var next = Enumerable.Range(idx + 1, events.Count).Select(k => k % events.Count).FirstOrDefault(k => !placed.ContainsKey(k), -1);
                if (next >= 0) { boxes[next].Focus(); } else finishedBtn.Focus();
            };
            boxes.Add(box); states.Add(state);
            list.Children.Add(frame);
        }

        finishedBtn = Primary("Finished", () =>
        {
            if (checkedDone) return;
            checkedDone = true;
            int right = 0;
            foreach (var (i, year) in placed)
            {
                var ev = events[i].E;
                bool ok = Math.Abs(year - ev.Year) <= ev.Within;
                if (ok) right++;
                RecordTimeline(events[i], year, ok);
                boxes[i].IsReadOnly = true;
                states[i].Text = ok ? "Right" : $"Real: {PackEvent.Label(ev.Year)}";
                states[i].SetResourceReference(TextBlock.ForegroundProperty, ok ? "FocusBrush" : "BreakBrush");
            }
            _asked += events.Count; _right += right;
            MarkDone("timeline");
            Redraw();
            finishedBtn.Visibility = Visibility.Collapsed;
            results.Children.Add(Text($"{right} of {events.Count} close enough", 18, bold: true, top: 10));
            foreach (var (i, year) in placed.Where(kv => Math.Abs(kv.Value - events[kv.Key].E.Year) > events[kv.Key].E.Within))
            {
                var (l, ev) = events[i];
                var box = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                box.Children.Add(Text($"{ev.Text} · {PackEvent.Label(ev.Year)}", 15, bold: true));
                if (ev.Note.Length > 0) box.Children.Add(Text(ev.Note, 14, brush: "Muted", top: 2));
                if (ev.Find.Length > 0) { var see = Link("See it in the book", () => { if (Course.EventBook.TryGetValue(ev, out var b)) Course.UseBookQuietly(b); ShowWhere(l, new PackRef { Find = ev.Find }); }); see.HorizontalAlignment = HorizontalAlignment.Left; see.Margin = new Thickness(0, 4, 0, 0); box.Children.Add(see); }
                results.Children.Add(box);
            }
            if (right < events.Count) results.Children.Add(Text("The ones you missed come back first in timeline practice.", 13, brush: "Muted", top: 10));
            var next = Primary("Next", Next);
            results.Children.Add(next);
            next.Focus();
        });
        finishedBtn.IsEnabled = false;
        finishedBtn.HorizontalAlignment = HorizontalAlignment.Left;
        finishedBtn.Margin = new Thickness(0, 6, 0, 0);

        _page.Children.Add(view);
        _page.Children.Add(list);
        _page.Children.Add(finishedBtn);
        _page.Children.Add(results);
        Dispatcher.BeginInvoke(new Action(() => boxes.FirstOrDefault()?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ================= Timeline practice: match dates to events =================

    private void ShowTimelineMatch(List<(Course.LessonRef Lesson, PackEvent E)> events)
    {
        SetStep("Timeline");
        _page.MaxWidth = 1100;
        var cfg = Course.Config;
        int per = Math.Clamp(cfg.TimelineMatchCount, 3, 6), extra = Math.Clamp(cfg.TimelineExtraDates, 0, 3);
        var rnd = new Random();

        // Sets of a few events; each set gets its own dates: theirs, plus extra years from other events you've learned
        // near them (so they can't be ruled out at a glance), never within a set event's margin.
        var sets = new List<List<int>>();
        for (int i = 0; i < events.Count; i += per) sets.Add(Enumerable.Range(i, Math.Min(per, events.Count - i)).ToList());
        if (sets.Count > 1 && sets[^1].Count == 1) { sets[^2].AddRange(sets[^1]); sets.RemoveAt(sets.Count - 1); }
        List<int> learnedYears;
        try { learnedYears = Course.LearnedEvents(Course.Config.Courses.Select(c => c.Id)).Select(x => x.E.Year).Distinct().ToList(); }
        catch { learnedYears = new(); }
        var options = new List<List<int>>();
        foreach (var set in sets)
        {
            var real = set.Select(i => events[i].E.Year).Distinct().ToList();
            bool Free(int y, List<int> taken) => set.All(i => Math.Abs(y - events[i].E.Year) > Math.Max(events[i].E.Within, 0)) && !taken.Contains(y);
            var decoys = new List<int>();
            int lo = real.Min(), hi = real.Max(), span = Math.Max(20, hi - lo);
            foreach (var y in learnedYears.Where(y => y >= lo - span && y <= hi + span).OrderBy(_ => rnd.Next()))
                if (decoys.Count < extra && Free(y, decoys)) decoys.Add(y);
            for (int tries = 0; decoys.Count < extra && tries < 200; tries++)
            {
                var basis = real[rnd.Next(real.Count)];
                int off = (rnd.Next(2) == 0 ? -1 : 1) * (rnd.Next(1, 9) * Math.Max(3, span / 8));
                int y = basis + off;
                if (y == 0) y = 1;
                if (Free(y, decoys)) decoys.Add(y);
            }
            options.Add(real.Concat(decoys).OrderBy(y => y).ToList());
        }

        var view = new TimelineView(events.Select(e => e.E.Year).Concat(options.SelectMany(o => o)));
        var rightAt = new Dictionary<int, int>();   // event → year, matched right (stays on the timeline all round)
        var revealed = new HashSet<int>();          // missed three times: shown, counted wrong
        var wrongAt = new Dictionary<int, int>();   // event → the wrong year from the last check (shown until re-paired)
        var pairs = new Dictionary<int, int>();     // event → year, paired but not checked
        var misses = new Dictionary<int, int>();
        var recorded = new HashSet<int>();
        int setNo = 0, pickedEvent = -1;
        int? pickedDate = null; // a year (BCE years are negative, so no -1 for "none")

        var head = Text("", 13, brush: "Muted");
        var evPanel = new StackPanel();
        var datePanel = new StackPanel();
        var result = Text("", 15, top: 10);
        var check = Primary("Check ↵", () => { });

        Border Box(string label, UIElement content)
        {
            var sp = new StackPanel();
            sp.Children.Add(Text(label, 11, bold: true, brush: "Muted"));
            ((FrameworkElement)content).Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(content);
            var b = new Border { Child = sp, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top };
            b.SetResourceReference(Border.BackgroundProperty, "Panel");
            b.SetResourceReference(Border.BorderBrushProperty, "Line");
            return b;
        }
        Border Opt(string key, string text, string border, string? tag, string tagBrush, bool dim, Action? click)
        {
            var g = new DockPanel();
            var k = new TextBlock { Text = key, FontSize = 12, FontWeight = FontWeights.Bold, Width = 20, VerticalAlignment = VerticalAlignment.Center };
            k.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            DockPanel.SetDock(k, Dock.Left); g.Children.Add(k);
            if (tag != null)
            {
                var t = new TextBlock { Text = tag, FontSize = 12.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                t.SetResourceReference(TextBlock.ForegroundProperty, tagBrush);
                DockPanel.SetDock(t, Dock.Right); g.Children.Add(t);
            }
            var main = new TextBlock { Text = text, FontSize = 14.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            main.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            g.Children.Add(main);
            var b = new Border { Child = g, Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(border == "Line" ? 1 : 2), Opacity = dim ? 0.45 : 1 };
            b.SetResourceReference(Border.BackgroundProperty, border == "FocusBrush" && click != null && !dim && tag == null ? "FocusTint" : "Panel2");
            b.SetResourceReference(Border.BorderBrushProperty, border);
            if (click != null) { b.Cursor = Cursors.Hand; b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); }; }
            return b;
        }

        List<int> Set() => sets[setNo];
        List<int> Open() => Set().Where(i => !rightAt.ContainsKey(i)).ToList();
        // A date can be used as many times as events in the set share that year (usually once).
        int Uses(int year) => pairs.Count(p => p.Value == year && Set().Contains(p.Key)) + Set().Count(i => rightAt.TryGetValue(i, out var y) && y == year);
        int Room(int year) => Math.Max(1, Set().Count(i => events[i].E.Year == year)) - Uses(year);

        void Pair()
        {
            if (pickedEvent < 0 || pickedDate is not int year) return;
            pairs[pickedEvent] = year;
            wrongAt.Remove(pickedEvent);
            pickedEvent = -1; pickedDate = null;
            Refresh();
        }
        void PickEvent(int i)
        {
            if (rightAt.ContainsKey(i)) return;
            if (pairs.Remove(i)) { pickedEvent = -1; Refresh(); return; } // a paired event: unpair it
            pickedEvent = pickedEvent == i ? -1 : i;
            Pair(); Refresh();
        }
        void PickDate(int y)
        {
            if (Room(y) <= 0) return;
            pickedDate = pickedDate == y ? null : y;
            Pair(); Refresh();
        }

        void Refresh()
        {
            head.Text = sets.Count > 1 ? $"Set {setNo + 1} of {sets.Count}" : "";
            view.Marks.Clear();
            foreach (var (i, y) in rightAt)
                view.Marks.Add(revealed.Contains(i) ? new TlMark(events[i].E.Text, y, "BreakBrush", $"It's {PackEvent.Label(y)}", "BreakBrush")
                                                    : new TlMark(events[i].E.Text, y, "FocusBrush", PackEvent.Label(y), "FocusBrush"));
            foreach (var (i, y) in wrongAt) view.Marks.Add(new TlMark(events[i].E.Text, y, "PriorityHigh", $"✕ not {PackEvent.Label(y)}", "PriorityHigh"));
            foreach (var (i, y) in pairs) { int idx = i; view.Marks.Add(new TlMark(events[i].E.Text, y, "Line", PackEvent.Label(y), Click: () => PickEvent(idx))); }
            view.Redraw();

            evPanel.Children.Clear();
            int n = 0;
            foreach (var i in Set())
            {
                n++; int idx = i;
                var ev = events[i].E;
                if (rightAt.TryGetValue(i, out var ry)) evPanel.Children.Add(Opt($"{n}", ev.Text, revealed.Contains(i) ? "BreakBrush" : "FocusBrush", (revealed.Contains(i) ? "" : "✓ ") + PackEvent.Label(ry), revealed.Contains(i) ? "BreakBrush" : "FocusBrush", false, null));
                else if (pairs.TryGetValue(i, out var py)) evPanel.Children.Add(Opt($"{n}", ev.Text, "Line", PackEvent.Label(py), "Text", false, () => PickEvent(idx)));
                else if (wrongAt.ContainsKey(i)) evPanel.Children.Add(Opt($"{n}", ev.Text, pickedEvent == i ? "FocusBrush" : "PriorityHigh", "try again", "PriorityHigh", false, () => PickEvent(idx)));
                else evPanel.Children.Add(Opt($"{n}", ev.Text, pickedEvent == i ? "FocusBrush" : "Line", null, "Muted", false, () => PickEvent(idx)));
            }
            datePanel.Children.Clear();
            int k = 0;
            foreach (var y in options[setNo])
            {
                int year = y;
                bool full = Room(y) <= 0;
                datePanel.Children.Add(Opt(((char)('A' + k++)).ToString(), PackEvent.Label(y), pickedDate == y ? "FocusBrush" : "Line", null, "Muted", full, full ? null : () => PickDate(year)));
            }
            bool setDone = Open().Count == 0;
            check.Content = setDone ? (setNo + 1 < sets.Count ? "Next set ↵" : "Finish ↵") : "Check ↵";
            check.IsEnabled = setDone || Open().All(pairs.ContainsKey);
        }

        void CheckOrNext()
        {
            if (!check.IsEnabled) return;
            if (Open().Count == 0)
            {
                if (setNo + 1 < sets.Count) { setNo++; result.Text = ""; pairs.Clear(); wrongAt.Clear(); Refresh(); return; }
                MarkDone("timeline");
                Next();
                return;
            }
            int good = 0, bad = 0;
            foreach (var i in Open())
            {
                int y = pairs[i];
                var ev = events[i].E;
                bool ok = Math.Abs(y - ev.Year) <= ev.Within;
                if (recorded.Add(i)) { RecordTimeline(events[i], y, ok); _asked++; if (ok) _right++; }
                if (ok) { rightAt[i] = y; good++; }
                else
                {
                    bad++;
                    misses[i] = misses.GetValueOrDefault(i) + 1;
                    if (misses[i] >= 3) { rightAt[i] = ev.Year; revealed.Add(i); }
                    else wrongAt[i] = y;
                }
                pairs.Remove(i);
            }
            result.Text = bad == 0 ? (Open().Count == 0 ? "All right." : "")
                        : $"{good} right{(good > 0 ? ", staying on the timeline" : "")}. {(Open().Count > 0 ? $"Pair the {(Open().Count == 1 ? "red one" : $"{Open().Count} red ones")} again." : "")}";
            if (Open().Count == 0 && revealed.Overlaps(Set())) result.Text = "Done. The amber ones were missed three times, so their dates are shown; they come back soon.";
            Refresh();
        }
        check.Focusable = false; // Enter checks through the window's keys, never twice
        check.Click += (_, _) => CheckOrNext();

        _keys = key =>
        {
            if (key == Key.Enter) { CheckOrNext(); return; }
            int d = key >= Key.D1 && key <= Key.D9 ? key - Key.D1 : key >= Key.NumPad1 && key <= Key.NumPad9 ? key - Key.NumPad1 : -1;
            if (d >= 0 && d < Set().Count) { PickEvent(Set()[d]); return; }
            int a = key >= Key.A && key <= Key.Z ? key - Key.A : -1;
            if (a >= 0 && a < options[setNo].Count) PickDate(options[setNo][a]);
        };

        _page.Children.Add(head);
        _page.Children.Add(view);
        var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var evBox = Box("EVENTS", evPanel);
        var dateBox = Box("DATES", datePanel); Grid.SetColumn(dateBox, 2);
        grid.Children.Add(evBox); grid.Children.Add(dateBox);
        _page.Children.Add(grid);
        _page.Children.Add(result);
        check.HorizontalAlignment = HorizontalAlignment.Left;
        check.Margin = new Thickness(0, 10, 0, 0);
        _page.Children.Add(check);
        _page.Children.Add(Text("Pick an event (or press 1–" + Math.Min(9, per) + "), then a date (A, B, …). Click a pair to undo it.", 12.5, brush: "Muted", top: 8));
        Refresh();
    }
}
