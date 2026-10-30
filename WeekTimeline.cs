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
        SizeChanged += (_, e) => { if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 0.5) RequestRender(); };
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
            Canvas.SetLeft(name, LabelWidth + i * colWidth);
            Canvas.SetTop(name, 10);
            _header.Children.Add(name);
        }

        // Hour lines.
        for (var t = _from; t <= _to; t += TimeSpan.FromHours(1))
        {
            var line = new Line { X1 = LabelWidth - 4, X2 = width, Y1 = Y(t), Y2 = Y(t), StrokeThickness = 1, Opacity = 0.8 };
            line.SetResourceReference(Shape.StrokeProperty, "Line");
            _canvas.Children.Add(line);
            var label = new TextBlock { Text = DateTime.Today.Add(t).ToString("h tt"), FontSize = 11, Width = LabelWidth - 10, TextAlignment = TextAlignment.Right };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Canvas.SetTop(label, Y(t) - 8);
            _canvas.Children.Add(label);
        }
        for (int i = 1; i < 7; i++)
        {
            var sep = new Line { X1 = LabelWidth + i * colWidth, X2 = LabelWidth + i * colWidth, Y1 = 0, Y2 = _canvas.Height, StrokeThickness = 1, Opacity = 0.6 };
            sep.SetResourceReference(Shape.StrokeProperty, "Line");
            _canvas.Children.Add(sep);
        }

        // Each day's plan, then events that aren't in it.
        for (int i = 0; i < 7; i++)
        {
            double x = LabelWidth + i * colWidth + 3;
            var (blocks, start) = plans[i];
            var meetings = new List<(TimeSpan S, TimeSpan E)>();
            var t = start;
            foreach (var b in blocks)
            {
                var end = t + TimeSpan.FromMinutes(b.Minutes);
                if (b.IsMeeting) meetings.Add((t, end));
                AddCard(x, colWidth - 6, t, end, b.Name, b.Kind, true);
                t = end;
            }
            foreach (var ev in events[i])
            {
                var s = ev.Start < days[i] ? TimeSpan.Zero : ev.Start - days[i];
                var e = ev.End > days[i].AddDays(1) ? TimeSpan.FromHours(24) : ev.End - days[i];
                bool inPlan = ev.Busy && meetings.Any(m => m.S < e && m.E > s);
                if (!ShowPlan) AddCard(x, colWidth - 6, s, e, ev.Title, BlockKind.Meeting, ev.Busy); // calendar only: full width
                else if (!inPlan) AddCard(x + (colWidth - 6) * 0.45, (colWidth - 6) * 0.55, s, e, ev.Title, BlockKind.Meeting, ev.Busy);
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
