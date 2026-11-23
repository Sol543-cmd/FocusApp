using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>A Monday-to-Sunday calendar: each day's plan, plus calendar events not already in it.</summary>
public class WeekTimeline : UserControl
{
    private const double LabelWidth = 58;
    private const double HeaderHeight = 40;
    private readonly Canvas _header = new() { Height = HeaderHeight };
    private readonly Canvas _canvas = new();
    private readonly ScrollViewer _scroll;
    private DateTime _monday = DateTime.Today;
    private Func<DateTime, (IReadOnlyList<PlanBlock> Blocks, TimeSpan Start)>? _planFor;
    private TimeSpan _from, _to;
    private bool _renderQueued;

    public double PixelsPerMinute { get; set; } = 1.3;
    public TimeSpan RangeStart { get; set; } = new(6, 30, 0);
    public TimeSpan RangeEnd { get; set; } = new(17, 0, 0);

    /// <summary>False shows only calendar events (no focus sessions, breaks, lunch or Devotions).</summary>
    public bool ShowPlan { get; set; } = true;
    /// <summary>The Calendar page: hours shrink or grow so the whole day fits the space, like Google Calendar.</summary>
    public bool FitToHeight { get; set; }

    /// <summary>The Calendar page: a strip under the day names for all-day events, from every calendar.</summary>
    public bool ShowAllDay { get; set; }

    /// <summary>How many tasks a day has (shown as a chip in the all-day strip); null: none shown.</summary>
    public Func<DateTime, int>? TaskCountFor { get; set; }
    /// <summary>A day's tasks chip was clicked.</summary>
    public event Action<DateTime, FrameworkElement>? TasksClicked;

    /// <summary>Lightly gray out the time outside each day's working hours.</summary>
    public bool ShadeOffHours { get; set; }
    /// <summary>A day's working hours (null: not a working day, all grayed).</summary>
    public Func<DateTime, (TimeSpan Start, TimeSpan End)?>? WorkHoursFor { get; set; }

    /// <summary>A day to point out (one you jumped to with Go to date): its name is marked.</summary>
    public DateTime? Highlight { get; set; }

    /// <summary>Clicking a day's name opens that day.</summary>
    public event Action<DateTime>? DayClicked;

    public WeekTimeline()
    {
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _canvas
        };
        var dock = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        dock.Children.Add(_header);
        dock.Children.Add(_scroll);
        Content = dock;
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 0.5 || (FitToHeight && Math.Abs(e.PreviousSize.Height - e.NewSize.Height) > 0.5)) RequestRender();
        };
    }

    /// <summary>Shows the week containing <paramref name="anyDay"/>.</summary>
    public void Show(DateTime anyDay, Func<DateTime, (IReadOnlyList<PlanBlock> Blocks, TimeSpan Start)> planFor)
    {
        int sinceMonday = ((int)anyDay.DayOfWeek + 6) % 7;
        _monday = anyDay.Date.AddDays(-sinceMonday);
        _planFor = planFor;
        RequestRender();
    }

    public void RequestRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _renderQueued = false;
            try { Render(); }
            catch (Exception ex) { Log.Error("WeekTimeline", ex); }
        }), DispatcherPriority.Render);
    }

    private double Y(TimeSpan t) => 8 + (t - _from).TotalMinutes * PixelsPerMinute;
    private const double AllDayRow = 22;

    private void Render()
    {
        _canvas.Children.Clear();
        _header.Children.Clear();
        if (_planFor == null) return;

        double width = Math.Max(400, ActualWidth - 18);
        double colWidth = (width - LabelWidth) / 7;

        var days = Enumerable.Range(0, 7).Select(i => _monday.AddDays(i)).ToList();
        var plans = days.Select(d => ShowPlan ? _planFor(d) : ((IReadOnlyList<PlanBlock>)System.Array.Empty<PlanBlock>(), RangeStart)).ToList();
        var events = days.Select(d => CalendarService.EventsOn(d).Where(e => !e.AllDay).ToList()).ToList();

        _from = RangeStart;
        _to = RangeEnd;
        for (int i = 0; i < 7; i++)
        {
            var (blocks, start) = plans[i];
            if (blocks.Count > 0)
            {
                if (start < _from) _from = start;
                var end = start + TimeSpan.FromMinutes(blocks.Sum(b => b.Minutes));
                if (end > _to) _to = end;
            }
            foreach (var ev in events[i])
            {
                var s = ev.Start < days[i] ? TimeSpan.Zero : ev.Start - days[i];
                var e = ev.End > days[i].AddDays(1) ? TimeSpan.FromHours(24) : ev.End - days[i];
                if (s < _from) _from = s;
                if (e > _to) _to = e;
            }
        }
        _from = TimeSpan.FromMinutes(Math.Floor(_from.TotalMinutes / 60) * 60);
        _to = TimeSpan.FromMinutes(Math.Min(1440, Math.Ceiling(_to.TotalMinutes / 60) * 60));

        // All-day events (every calendar), each on a row in the strip, spanning its days.
        var allDay = new List<(CalendarEvent Ev, int From, int To, int Row)>();
        var taskChips = new List<(int Day, int Count, int Row)>();
        double band = 0;
        if (ShowAllDay)
        {
            var rows = new List<bool[]>();
            foreach (var ev in days.SelectMany(d => CalendarService.EventsOn(d).Where(e => e.AllDay)).Distinct()
                                   .OrderBy(e => e.Start).ThenByDescending(e => e.End - e.Start))
            {
                int a = Math.Max(0, (ev.Start.Date - _monday).Days);
                int b = Math.Min(6, (ev.End.AddTicks(-1).Date - _monday).Days);
                if (b < a) b = a;
                int row = rows.FindIndex(r => Enumerable.Range(a, b - a + 1).All(i => !r[i]));
                if (row < 0) { rows.Add(new bool[7]); row = rows.Count - 1; }
                for (int i = a; i <= b; i++) rows[row][i] = true;
                allDay.Add((ev, a, b, row));
            }
            // Each day's tasks: a chip in the strip, or right under that day's all-day events.
            for (int i = 0; i < 7; i++)
            {
                int count = TaskCountFor?.Invoke(days[i]) ?? 0;
                if (count <= 0) continue;
                int below = rows.FindLastIndex(r => r[i]) + 1;
                taskChips.Add((i, count, below));
            }
            int rowCount = Math.Max(rows.Count, taskChips.Count == 0 ? 0 : taskChips.Max(c => c.Row) + 1);
            band = Math.Max(30, rowCount * AllDayRow + 8); // about half an hour tall, more if a day has several
        }
        _header.Height = HeaderHeight + band;

        if (FitToHeight && ActualHeight > 200)
        {
            // The whole day in view: spread the hours over the space under the day names (never cramped).
            double header = HeaderHeight + band + 4;
            PixelsPerMinute = Math.Max(0.75, (ActualHeight - header - 24) / Math.Max(60, (_to - _from).TotalMinutes));
        }
        _canvas.Height = Y(_to) + 12;
        _canvas.Width = width;
        _header.Width = width;

        // Day names.
        for (int i = 0; i < 7; i++)
        {
            var day = days[i];
            bool today = day == DateTime.Today;
            var name = new TextBlock
            {
                Text = day.ToString("ddd d"),
                FontSize = 14,
                FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal,
                Width = colWidth,
                TextAlignment = TextAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "Open this day"
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, today ? "FocusBrush" : "Text");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "HeadingFont");
            name.MouseLeftButtonUp += (_, _) => DayClicked?.Invoke(day);
            if (Highlight is DateTime h && h.Date == day)
            {
                // The day you went to: its name in a soft pill.
                name.FontWeight = FontWeights.Bold;
                name.SetResourceReference(TextBlock.ForegroundProperty, "FocusBrush");
                var pill = new Border { Width = Math.Min(colWidth - 8, 110), Height = 26, CornerRadius = new CornerRadius(13), IsHitTestVisible = false };
                pill.SetResourceReference(Border.BackgroundProperty, "FocusTint");
                Canvas.SetLeft(pill, LabelWidth + i * colWidth + (colWidth - pill.Width) / 2);
                Canvas.SetTop(pill, 6);
                _header.Children.Add(pill);
            }
            Canvas.SetLeft(name, LabelWidth + i * colWidth);
            Canvas.SetTop(name, 10);
            _header.Children.Add(name);
        }

        // Half-hour lines: the faintest, when there's room for them.
        if (PixelsPerMinute * 30 >= 12)
            for (var t = _from + TimeSpan.FromMinutes(30); t < _to; t += TimeSpan.FromHours(1))
            {
                var half = new Line { X1 = LabelWidth, X2 = width, Y1 = Y(t), Y2 = Y(t), StrokeThickness = 1, Opacity = 0.12 };
                half.SetResourceReference(Shape.StrokeProperty, "Muted");
                _canvas.Children.Add(half);
            }
        // Hour lines: a touch lighter than the day lines, so they don't distract.
        for (var t = _from; t <= _to; t += TimeSpan.FromHours(1))
        {
            var line = new Line { X1 = LabelWidth - 4, X2 = width, Y1 = Y(t), Y2 = Y(t), StrokeThickness = 1, Opacity = 0.26 };
            line.SetResourceReference(Shape.StrokeProperty, "Muted");
            _canvas.Children.Add(line);
            var label = new TextBlock { Text = DateTime.Today.Add(t).ToString("h tt"), FontSize = 11, Width = LabelWidth - 10, TextAlignment = TextAlignment.Right };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Canvas.SetTop(label, Y(t) - 8);
            _canvas.Children.Add(label);
        }
        for (int i = 1; i < 7; i++)
        {
            var sep = new Line { X1 = LabelWidth + i * colWidth, X2 = LabelWidth + i * colWidth, Y1 = 0, Y2 = _canvas.Height, StrokeThickness = 1, Opacity = 0.35 };
            sep.SetResourceReference(Shape.StrokeProperty, "Muted");
            _canvas.Children.Add(sep);
        }

        // The all-day strip under the day names.
        if (ShowAllDay)
        {
            var label = new TextBlock { Text = "All day", FontSize = 11, Width = LabelWidth - 10, TextAlignment = TextAlignment.Right };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Canvas.SetTop(label, HeaderHeight + 6);
            _header.Children.Add(label);
            for (int i = 1; i < 7; i++)
            {
                var sep = new Line { X1 = LabelWidth + i * colWidth, X2 = LabelWidth + i * colWidth, Y1 = HeaderHeight, Y2 = HeaderHeight + band, StrokeThickness = 1, Opacity = 0.35 };
                sep.SetResourceReference(Shape.StrokeProperty, "Muted");
                _header.Children.Add(sep);
            }
            var bottom = new Line { X1 = LabelWidth - 4, X2 = width, Y1 = HeaderHeight + band - 0.5, Y2 = HeaderHeight + band - 0.5, StrokeThickness = 1, Opacity = 0.35 };
            bottom.SetResourceReference(Shape.StrokeProperty, "Muted");
            _header.Children.Add(bottom);
            foreach (var (ev, a, b, row) in allDay)
            {
                var chip = CalendarLook.AllDayChip(ev, (b - a + 1) * colWidth - 6, AllDayRow - 3);
                Canvas.SetLeft(chip, LabelWidth + a * colWidth + 3);
                Canvas.SetTop(chip, HeaderHeight + 4 + row * AllDayRow);
                _header.Children.Add(chip);
            }
            foreach (var (dayIndex, count, row) in taskChips)
            {
                var day = days[dayIndex];
                var chip = CalendarLook.TasksChip(count, colWidth - 6, AllDayRow - 3, target => TasksClicked?.Invoke(day, target));
                Canvas.SetLeft(chip, LabelWidth + dayIndex * colWidth + 3);
                Canvas.SetTop(chip, HeaderHeight + 4 + row * AllDayRow);
                _header.Children.Add(chip);
            }
        }
        // Outside the working day: a light gray wash (just enough to see where the day starts and stops).
        if (ShadeOffHours && WorkHoursFor != null)
            for (int i = 0; i < 7; i++)
            {
                var hours = WorkHoursFor(days[i]);
                var spans = hours is (TimeSpan ws, TimeSpan we)
                    ? new[] { (_from, ws < _from ? _from : ws), (we > _to ? _to : we, _to) }
                    : new[] { (_from, _to) };
                foreach (var (a, b) in spans)
                {
                    if (b <= a) continue;
                    var off = new System.Windows.Shapes.Rectangle { Width = colWidth, Height = Y(b) - Y(a), IsHitTestVisible = false, Opacity = 0.07 };
                    off.SetResourceReference(Shape.FillProperty, "Muted");
                    Canvas.SetLeft(off, LabelWidth + i * colWidth);
                    Canvas.SetTop(off, Y(a));
                    _canvas.Children.Insert(0, off);
                }
            }
        // Today's column, lightly shaded (as in Outlook).
        int todayCol = (DateTime.Today - _monday).Days;
        if (todayCol >= 0 && todayCol < 7)
        {
            var shade = new System.Windows.Shapes.Rectangle { Width = colWidth, Height = _canvas.Height, IsHitTestVisible = false, Opacity = 0.35 };
            shade.SetResourceReference(Shape.FillProperty, "FocusTint");
            Canvas.SetLeft(shade, LabelWidth + todayCol * colWidth);
            _canvas.Children.Insert(0, shade);
        }

        // Each day's plan, then its calendar events. Meetings show as the calendar's own events (in their calendar's color,
        // with Join), and events that overlap sit side by side, as in Outlook.
        for (int i = 0; i < 7; i++)
        {
            double x = LabelWidth + i * colWidth + 3, full = colWidth - 6;
            var (blocks, start) = plans[i];
            var dayEvents = events[i].Select(ev => (Ev: ev,
                S: ev.Start < days[i] ? TimeSpan.Zero : ev.Start - days[i],
                E: ev.End > days[i].AddDays(1) ? TimeSpan.FromHours(24) : ev.End - days[i])).ToList();
            var drawn = new List<(TimeSpan S, TimeSpan E)>();
            var t = start;
            foreach (var b in blocks)
            {
                var end = t + TimeSpan.FromMinutes(b.Minutes);
                bool isCalendarMeeting = b.IsMeeting && dayEvents.Any(d => d.Ev.Busy && d.S < end && d.E > t);
                if (!isCalendarMeeting)
                {
                    AddCard(x, full, t, end, b.Name, b.Kind, true);
                    drawn.Add((t, end));
                }
                t = end;
            }
            var placed = CalendarLook.SideBySide(dayEvents, d => d.S.TotalMinutes, d => d.E.TotalMinutes);
            // A group that overlaps a focus session or break shares the column with it (on the right); otherwise it has the column.
            var besidePlan = placed.Where(p => drawn.Any(m => m.S < p.Item.E && m.E > p.Item.S)).Select(p => p.Group).ToHashSet();
            foreach (var (item, col, cols, group) in placed)
            {
                double laneX = besidePlan.Contains(group) ? x + full * 0.45 : x;
                double laneW = besidePlan.Contains(group) ? full * 0.55 : full;
                double w = laneW / cols;
                AddEvent(item.Ev, laneX + col * w, cols > 1 ? w - 2 : w, item.S, item.E);
            }
        }

        // Now line on today.
        var now = DateTime.Now;
        int todayIndex = (now.Date - _monday).Days;
        if (todayIndex >= 0 && todayIndex < 7 && now.TimeOfDay >= _from && now.TimeOfDay <= _to)
        {
            var nowLine = new Line
            {
                X1 = LabelWidth + todayIndex * colWidth, X2 = LabelWidth + (todayIndex + 1) * colWidth,
                Y1 = Y(now.TimeOfDay), Y2 = Y(now.TimeOfDay), StrokeThickness = 2
            };
            nowLine.SetResourceReference(Shape.StrokeProperty, "MeetingBrush");
            _canvas.Children.Add(nowLine);
        }
    }

    /// <summary>A calendar event in its calendar's color, with Join for online meetings (click for details).</summary>
    private void AddEvent(CalendarEvent ev, double x, double w, TimeSpan start, TimeSpan end)
    {
        double top = Y(start), height = Math.Max(3, Y(end) - top - 1);
        var card = CalendarLook.EventCard(ev, w, height, 11, null);
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, top);
        _canvas.Children.Add(card);
    }

    private void AddCard(double x, double w, TimeSpan start, TimeSpan end, string title, BlockKind kind, bool solid)
    {
        double top = Y(start), height = Math.Max(3, Y(end) - top - 1);
        string accent = kind switch { BlockKind.Focus => "FocusBrush", BlockKind.Meeting => "MeetingBrush", _ => "BreakBrush" };
        string tint = kind switch { BlockKind.Focus => "FocusTint", BlockKind.Meeting => "MeetingTint", _ => "BreakTint" };
        var text = new TextBlock
        {
            Text = title,
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = height < 13 ? Visibility.Collapsed : Visibility.Visible
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var card = new Border
        {
            Width = Math.Max(4, w),
            Height = height,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(4, 0, 2, 0),
            Child = text,
            ClipToBounds = true,
            Opacity = solid ? 1 : 0.6,
            ToolTip = $"{title}\n{DateTime.Today.Add(start):t} to {DateTime.Today.Add(end):t}"
        };
        card.SetResourceReference(Border.BackgroundProperty, tint);
        card.SetResourceReference(Border.BorderBrushProperty, accent);
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, top);
        _canvas.Children.Add(card);
    }
}
