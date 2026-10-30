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

/// <summary>
/// A one-day calendar: half-hour lines down the side, with focus sessions, breaks and meetings
/// drawn at their times. Used for planning and on the dashboard.
/// </summary>
public class DayTimeline : UserControl
{
    private const double LabelWidth = 64;
    private readonly Canvas _canvas = new();
    private readonly ScrollViewer _scroll;
    private readonly Line _nowLine = new() { StrokeThickness = 2 };
    private readonly Ellipse _nowDot = new() { Width = 10, Height = 10 };
    private IReadOnlyList<PlanBlock> _blocks = Array.Empty<PlanBlock>();
    private DateTime _dayStart = DateTime.Today.AddHours(8);
    private TimeSpan _from, _to;
    private bool _renderQueued;

    public double PixelsPerMinute { get; set; } = 1.8;
    public TimeSpan RangeStart { get; set; } = new(6, 30, 0);
    public TimeSpan RangeEnd { get; set; } = new(17, 0, 0);
    public bool ShowNowLine { get; set; }
    public PlanBlock? Selected { get; set; }

    /// <summary>Calendar events to show too. Ones already in the plan as meetings aren't repeated.</summary>
    public IReadOnlyList<CalendarEvent>? Events { get; set; }

    /// <summary>Builds the right-click menu for a block (or returns null for none).</summary>
    public Func<PlanBlock, ContextMenu?>? MenuFor { get; set; }

    public event Action<PlanBlock>? BlockClicked;

    /// <summary>Which blocks can be dragged to a new spot (null: none).</summary>
    public Func<PlanBlock, bool>? CanDrag { get; set; }

    /// <summary>A block was dragged and dropped; the number is where it goes (0 = first).</summary>
    public event Action<PlanBlock, int>? BlockDropped;

    /// <summary>A task from the task list was dropped onto a block.</summary>
    public event Action<PlanBlock, BinTask>? TaskDropped;

    /// <summary>A block was right-clicked (selected for its menu, without opening anything else).</summary>
    public event Action<PlanBlock>? BlockRightClicked;

    /// <summary>Show each focus session's tasks on its block, as a small numbered list.</summary>
    public bool ShowTasks { get; set; } = true;

    /// <summary>While true, dragging across the calendar chooses a time range (for filling part of the day).</summary>
    public bool SelectingRange
    {
        get => _selectingRange;
        set { _selectingRange = value; Cursor = value ? Cursors.Cross : null; if (!value) _rangeRect.Visibility = Visibility.Collapsed; }
    }
    private bool _selectingRange;
    private readonly Rectangle _rangeRect = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false, RadiusX = 6, RadiusY = 6, StrokeThickness = 2 };
    private double? _rangeStartY;

    /// <summary>A time range was chosen by dragging (only while <see cref="SelectingRange"/> is on).</summary>
    public event Action<TimeSpan, TimeSpan>? RangeSelected;

    private TimeSpan TimeAt(double y)
    {
        var t = _from + TimeSpan.FromMinutes((y - 12) / PixelsPerMinute);
        return TimeSpan.FromMinutes(Math.Round(t.TotalMinutes / 5) * 5);
    }

    private readonly List<(double Top, double Bottom)> _blockSpans = new();
    private readonly Line _dropLine = new() { StrokeThickness = 3, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private PlanBlock? _dragBlock;
    private Point _dragStart;
    private bool _dragging;
    private bool _renderWaiting;

    public DayTimeline()
    {
        // Choosing a time range by dragging (for "Fill part of the day").
        _canvas.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!SelectingRange) return;
            _rangeStartY = e.GetPosition(_canvas).Y;
            _rangeRect.SetResourceReference(Shape.FillProperty, "FocusTint");
            _rangeRect.SetResourceReference(Shape.StrokeProperty, "FocusBrush");
            _rangeRect.Opacity = 0.85;
            if (!_canvas.Children.Contains(_rangeRect)) _canvas.Children.Add(_rangeRect);
            Canvas.SetLeft(_rangeRect, LabelWidth);
            _rangeRect.Width = Math.Max(10, _canvas.ActualWidth - LabelWidth - 4);
            _rangeRect.Height = 0;
            _rangeRect.Visibility = Visibility.Visible;
            _canvas.CaptureMouse();
            e.Handled = true;
        };
        _canvas.PreviewMouseMove += (_, e) =>
        {
            if (!SelectingRange || _rangeStartY is not double y0) return;
            double y = e.GetPosition(_canvas).Y;
            Canvas.SetTop(_rangeRect, Math.Min(y0, y));
            _rangeRect.Height = Math.Abs(y - y0);
            e.Handled = true;
        };
        _canvas.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!SelectingRange || _rangeStartY is not double y0) return;
            _rangeStartY = null;
            _canvas.ReleaseMouseCapture();
            double y = e.GetPosition(_canvas).Y;
            var a = TimeAt(Math.Min(y0, y));
            var b = TimeAt(Math.Max(y0, y));
            e.Handled = true;
            if (b - a >= TimeSpan.FromMinutes(10)) RangeSelected?.Invoke(a, b);
            else _rangeRect.Visibility = Visibility.Collapsed;
        };

        // Safety nets so a drag can never get stuck (which made the calendar stop responding).
        PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (_dragBlock != null && !_dragging)
                Dispatcher.BeginInvoke(new Action(() => { if (_dragBlock != null && Mouse.LeftButton != MouseButtonState.Pressed) EndDrag(); }),
                    DispatcherPriority.Background);
        };
        IsVisibleChanged += (_, _) => { if (_dragBlock != null) EndDrag(); };

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _canvas
        };
        Content = _scroll;
        _nowLine.SetResourceReference(Shape.StrokeProperty, "MeetingBrush");
        _nowDot.SetResourceReference(Shape.FillProperty, "MeetingBrush");
        _dropLine.SetResourceReference(Shape.StrokeProperty, "Text");
        SizeChanged += (_, e) => { if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 0.5) RequestRender(); };
    }

    /// <summary>Shows these blocks, the first one starting at <paramref name="dayStart"/>.</summary>
    public void Show(IReadOnlyList<PlanBlock> blocks, DateTime dayStart)
    {
        _blocks = blocks;
        _dayStart = dayStart;
        RequestRender();
    }

    /// <summary>Redraws soon, once, even if asked many times in a row.</summary>
    public void RequestRender()
    {
        // A drag can only be in progress while the mouse button is down. If it isn't, the drag was
        // cut short somewhere (the release happened elsewhere): forget it, or the calendar would
        // stop redrawing and look frozen.
        if (_dragBlock != null && Mouse.LeftButton != MouseButtonState.Pressed) ResetDrag();
        if (_dragBlock != null)
        {
            _renderWaiting = true; // don't redraw under the mouse mid-drag
            return;
        }
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _renderQueued = false;
            try { Render(); }
            catch (Exception ex) { Log.Error("DayTimeline", ex); }
        }), DispatcherPriority.Render);
    }

    private double Y(TimeSpan t) => 12 + (t - _from).TotalMinutes * PixelsPerMinute;

    private void Render()
    {
        _canvas.Children.Clear();
        double width = Math.Max(200, ActualWidth - 18);

        TimeSpan planStart = _dayStart.TimeOfDay;
        TimeSpan planEnd = planStart + TimeSpan.FromMinutes(_blocks.Sum(b => b.Minutes));
        _from = Floor30(planStart < RangeStart && _blocks.Count > 0 ? planStart : RangeStart);
        _to = Ceil30(planEnd > RangeEnd ? planEnd : RangeEnd);
        foreach (var ev in ExtraEvents())
        {
            var (s, e) = ClipToDay(ev);
            if (s < _from) _from = Floor30(s);
            if (e > _to) _to = Ceil30(e);
        }
        if (_to > TimeSpan.FromHours(24)) _to = TimeSpan.FromHours(24);
        _canvas.Height = Y(_to) + 16;
        _canvas.Width = width;

        // Half-hour grid with times.
        for (var t = _from; t <= _to; t += TimeSpan.FromMinutes(30))
        {
            bool hour = t.Minutes == 0;
            var line = new Line { X1 = LabelWidth - 6, X2 = width, Y1 = Y(t), Y2 = Y(t), StrokeThickness = 1, Opacity = hour ? 0.9 : 0.45 };
            line.SetResourceReference(Shape.StrokeProperty, "Line");
            _canvas.Children.Add(line);
            if (hour || PixelsPerMinute >= 1.2)
            {
                var label = new TextBlock
                {
                    Text = DateTime.Today.Add(t).ToString("t"),
                    FontSize = hour ? 12 : 10.5,
                    Width = LabelWidth - 12,
                    TextAlignment = TextAlignment.Right
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, hour ? "Text" : "Muted");
                label.SetResourceReference(TextBlock.FontFamilyProperty, "HeadingFont");
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, Y(t) - 8);
                _canvas.Children.Add(label);
            }
        }

        // Blocks.
        _blockSpans.Clear();
        var start = planStart;
        foreach (var block in _blocks)
        {
            var end = start + TimeSpan.FromMinutes(block.Minutes);
            _blockSpans.Add((Y(start), Y(end)));
            _canvas.Children.Add(MakeBlock(block, start, end, width));
            start = end;
        }
        _canvas.Children.Add(_dropLine);

        // Calendar events that aren't already in the plan (for example after 4:30, or marked "free").
        foreach (var ev in ExtraEvents()) _canvas.Children.Add(MakeEvent(ev, width));

        _canvas.Children.Add(_nowLine);
        _canvas.Children.Add(_nowDot);
        UpdateNow();
    }

    private FrameworkElement MakeBlock(PlanBlock block, TimeSpan start, TimeSpan end, double width)
    {
        double top = Y(start), height = Math.Max(4, Y(end) - top - 2);
        string accent = block.Kind switch { BlockKind.Focus => "FocusBrush", BlockKind.Meeting => "MeetingBrush", _ => "BreakBrush" };
        string tint = block.Kind switch { BlockKind.Focus => "FocusTint", BlockKind.Meeting => "MeetingTint", _ => "BreakTint" };

        var title = new TextBlock
        {
            Text = block.Name,
            FontSize = height < 22 ? 11 : 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");

        string range = $"{DateTime.Today.Add(start):t} to {DateTime.Today.Add(end):t}, {block.Minutes} min" +
                       (block.IsAnchored ? ", fixed time" : "");
        var stack = new StackPanel();
        // A focus session's tasks (done ones drop off): shown in every block with room for even one line.
        var tasks = ShowTasks && block.IsFocus ? block.Tasks.Where(x => x.Text.Trim().Length > 0 && !x.Done).ToList() : new List<TaskItem>();
        double cardWidth = width - LabelWidth - 4 - 18;
        if (height < 14)
        {
            // Too thin for text: just the colored bar (hover for details).
        }
        else if (height < 22)
        {
            // Too short for two lines: one line with the name and length (and how many tasks).
            title.Text = $"{block.Name}  {block.Minutes} min" + (tasks.Count > 0 ? $"  ({tasks.Count} task{(tasks.Count == 1 ? "" : "s")})" : "");
            stack.Children.Add(title);
        }
        else if (tasks.Count > 0)
        {
            // Name and time on one line, then the tasks in small type; two columns when they don't fit in one.
            title.FontSize = 11.5;
            title.Text = $"{block.Name}   {DateTime.Today.Add(start):t} to {DateTime.Today.Add(end):t}";
            stack.Children.Add(title);
            const double lineHeight = 12.5;
            int rows = Math.Max(1, (int)((height - 19) / lineHeight));
            int columns = tasks.Count > rows && cardWidth >= 240 ? 2 : 1;
            int room = rows * columns;
            int shown = tasks.Count <= room ? tasks.Count : Math.Max(0, room - 1);
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = columns, Margin = new Thickness(0, 1, 0, 0) };
            if (columns == 2) grid.Rows = rows;
            for (int i = 0; i < shown; i++)
            {
                var line = new TextBlock { Text = $"{i + 1}. {tasks[i].Text}", FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
                line.SetResourceReference(TextBlock.ForegroundProperty, "PriorityHigh");
                grid.Children.Add(line);
            }
            if (shown < tasks.Count)
            {
                var more = new TextBlock { Text = $"+{tasks.Count - shown} more", FontSize = 10 };
                more.SetResourceReference(TextBlock.ForegroundProperty, "PriorityHigh");
                grid.Children.Add(more);
            }
            stack.Children.Add(grid);
        }
        else
        {
            stack.Children.Add(title);
            var when = new TextBlock { Text = range, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            when.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            stack.Children.Add(when);
            if (height >= 52 && block.IsFocus && block.HasBlockedApps)
            {
                var blocking = new TextBlock { Text = block.BlockedSummary, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
                blocking.SetResourceReference(TextBlock.ForegroundProperty, accent);
                stack.Children.Add(blocking);
            }
        }

        var card = new Border
        {
            Width = width - LabelWidth - 4,
            Height = height,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(5, 0, 0, 0),
            Padding = new Thickness(10, height < 22 ? 0 : 4, 8, 0),
            Child = stack,
            Cursor = CanDrag?.Invoke(block) == true ? Cursors.SizeNS : Cursors.Hand,
            ToolTip = $"{block.KindLabel}: {block.Name}\n{range}" + (block.IsFocus && block.HasBlockedApps ? "\n" + block.BlockedSummary : "") +
                      (CanDrag?.Invoke(block) == true ? "\nDrag to move it." : ""),
            ClipToBounds = true
        };
        card.SetResourceReference(Border.BackgroundProperty, tint);
        card.SetResourceReference(Border.BorderBrushProperty, accent);

        if (ReferenceEquals(block, Selected))
        {
            // Selected: a full outline in the text color.
            var outline = new Border
            {
                Width = card.Width + 4,
                Height = height + 4,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false
            };
            outline.SetResourceReference(Border.BorderBrushProperty, "Text");
            Canvas.SetLeft(outline, LabelWidth - 2);
            Canvas.SetTop(outline, top - 2);
            _canvas.Children.Add(outline);
        }

        // Click to select; press and drag (up or down) to move.
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (CanDrag?.Invoke(block) != true) return;
            _dragBlock = block;
            _dragStart = e.GetPosition(_canvas);
            _dragging = false;
            // Without the mouse, a drag can't be tracked safely: treat it as a plain click.
            if (!card.CaptureMouse()) _dragBlock = null;
            e.Handled = true;
        };
        card.MouseMove += (_, e) =>
        {
            if (ReferenceEquals(_dragBlock, block) && e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag(); // the button was let go somewhere we didn't hear about
                return;
            }
            if (!ReferenceEquals(_dragBlock, block) || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(_canvas);
            if (!_dragging && Math.Abs(p.Y - _dragStart.Y) > 6)
            {
                _dragging = true;
                card.Opacity = 0.55;
            }
            if (_dragging) ShowDropLine(DropIndex(p.Y), width);
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            bool wasDragging = _dragging && ReferenceEquals(_dragBlock, block);
            int target = DropIndex(e.GetPosition(_canvas).Y);
            if (ReferenceEquals(_dragBlock, block)) card.ReleaseMouseCapture();
            EndDrag();
            if (wasDragging) BlockDropped?.Invoke(block, target);
            else BlockClicked?.Invoke(block);
            e.Handled = true;
        };
        // Tasks dragged from the task list can be dropped on focus sessions.
        if (block.IsFocus)
        {
            card.AllowDrop = true;
            card.DragEnter += (_, e) =>
            {
                if (e.Data.GetDataPresent(typeof(BinTask))) card.BorderThickness = new Thickness(5, 2, 2, 2);
            };
            card.DragLeave += (_, _) => card.BorderThickness = new Thickness(5, 0, 0, 0);
            card.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(typeof(BinTask)) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            card.Drop += (_, e) =>
            {
                card.BorderThickness = new Thickness(5, 0, 0, 0);
                if (e.Data.GetData(typeof(BinTask)) is BinTask task) TaskDropped?.Invoke(block, task);
                e.Handled = true;
            };
        }
        card.LostMouseCapture += (_, _) =>
        {
            if (ReferenceEquals(_dragBlock, block)) EndDrag();
        };
        card.MouseRightButtonUp += (_, e) =>
        {
            if (BlockRightClicked != null) BlockRightClicked.Invoke(block);
            else BlockClicked?.Invoke(block);
            var menu = MenuFor?.Invoke(block);
            if (menu != null)
            {
                menu.PlacementTarget = card;
                menu.IsOpen = true;
            }
            e.Handled = true;
        };

        Canvas.SetLeft(card, LabelWidth);
        Canvas.SetTop(card, top);
        return card;
    }

    /// <summary>Where a block dropped at this height would go.</summary>
    private int DropIndex(double y)
    {
        for (int i = 0; i < _blockSpans.Count; i++)
            if (y < (_blockSpans[i].Top + _blockSpans[i].Bottom) / 2) return i;
        return _blockSpans.Count;
    }

    private void ShowDropLine(int index, double width)
    {
        double y = index < _blockSpans.Count ? _blockSpans[index].Top - 1 : (_blockSpans.Count > 0 ? _blockSpans[^1].Bottom : 0);
        _dropLine.X1 = LabelWidth;
        _dropLine.X2 = width - 4;
        _dropLine.Y1 = _dropLine.Y2 = y;
        _dropLine.Visibility = Visibility.Visible;
    }

    /// <summary>Forgets a drag without redrawing (used just before a redraw).</summary>
    private void ResetDrag()
    {
        if (_dragBlock != null && Mouse.Captured is UIElement held && _canvas.IsAncestorOf(held)) held.ReleaseMouseCapture();
        _dragBlock = null;
        _dragging = false;
        _dropLine.Visibility = Visibility.Collapsed;
        _renderWaiting = false;
    }

    private void EndDrag()
    {
        _dragBlock = null;
        _dragging = false;
        _dropLine.Visibility = Visibility.Collapsed;
        if (_renderWaiting)
        {
            _renderWaiting = false;
            RequestRender();
        }
        else RequestRender(); // put the dragged card back to normal
    }

    private (TimeSpan Start, TimeSpan End) ClipToDay(CalendarEvent ev)
    {
        var day = _dayStart.Date;
        var s = ev.Start < day ? TimeSpan.Zero : ev.Start - day;
        var e = ev.End > day.AddDays(1) ? TimeSpan.FromHours(24) : ev.End - day;
        return (s, e);
    }

    /// <summary>Timed events on this day that no meeting block in the plan already covers.</summary>
    private List<CalendarEvent> ExtraEvents()
    {
        var result = new List<CalendarEvent>();
        if (Events == null) return result;
        var meetings = new List<(TimeSpan, TimeSpan)>();
        var t = _dayStart.TimeOfDay;
        foreach (var b in _blocks)
        {
            var end = t + TimeSpan.FromMinutes(b.Minutes);
            if (b.IsMeeting) meetings.Add((t, end));
            t = end;
        }
        foreach (var ev in Events)
        {
            if (ev.AllDay || ev.End <= _dayStart.Date || ev.Start >= _dayStart.Date.AddDays(1)) continue;
            var (s, e) = ClipToDay(ev);
            bool covered = ev.Busy && meetings.Any(m => m.Item1 < e && m.Item2 > s &&
                (Min(m.Item2, e) - Max(m.Item1, s)).TotalMinutes >= (e - s).TotalMinutes / 2);
            if (!covered) result.Add(ev);
        }
        return result;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private FrameworkElement MakeEvent(CalendarEvent ev, double width)
    {
        var (s, e) = ClipToDay(ev);
        double top = Y(s), height = Math.Max(4, Y(e) - top - 2);
        double laneWidth = (width - LabelWidth) * 0.48;

        var text = new TextBlock
        {
            Text = ev.Busy ? ev.Title : ev.Title + " (free)",
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = height < 14 ? Visibility.Collapsed : Visibility.Visible
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var card = new Border
        {
            Width = laneWidth,
            Height = height,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, height < 22 ? 0 : 3, 6, 0),
            Child = text,
            Opacity = 0.95,
            ClipToBounds = true,
            ToolTip = $"From your calendar: {ev.Title}\n{ev.Start:t} to {ev.End:t}" + (ev.Busy ? "" : " (free)")
        };
        card.SetResourceReference(Border.BackgroundProperty, ev.Busy ? "MeetingTint" : "Panel2");
        card.SetResourceReference(Border.BorderBrushProperty, ev.Busy ? "MeetingBrush" : "Line");
        Canvas.SetLeft(card, width - laneWidth - 4);
        Canvas.SetTop(card, top);
        return card;
    }

    /// <summary>Moves the red "now" line. Cheap; call as often as you like.</summary>
    public void UpdateNow()
    {
        var now = DateTime.Now;
        bool visible = ShowNowLine && now.Date == _dayStart.Date && now.TimeOfDay >= _from && now.TimeOfDay <= _to;
        _nowLine.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _nowDot.Visibility = _nowLine.Visibility;
        if (!visible) return;
        double y = Y(now.TimeOfDay);
        _nowLine.X1 = LabelWidth - 6;
        _nowLine.X2 = _canvas.Width;
        _nowLine.Y1 = _nowLine.Y2 = y;
        Canvas.SetLeft(_nowDot, LabelWidth - 11);
        Canvas.SetTop(_nowDot, y - 5);
    }

    /// <summary>Scrolls so this time of day sits a little below the top.</summary>
    /// <summary>Scrolls so a block is in view (used when moving through blocks with the arrow keys).</summary>
    public void ScrollToBlock(PlanBlock block)
    {
        var t = _dayStart.TimeOfDay;
        foreach (var b in _blocks)
        {
            if (ReferenceEquals(b, block)) { ScrollTo(t - TimeSpan.FromMinutes(30)); return; }
            t += TimeSpan.FromMinutes(b.Minutes);
        }
    }

    public void ScrollTo(TimeSpan time)
    {
        Dispatcher.BeginInvoke(new Action(() =>
            _scroll.ScrollToVerticalOffset(Math.Max(0, Y(time) - 80))), DispatcherPriority.Loaded);
    }

    private static TimeSpan Floor30(TimeSpan t) => TimeSpan.FromMinutes(Math.Floor(t.TotalMinutes / 30) * 30);
    private static TimeSpan Ceil30(TimeSpan t) => TimeSpan.FromMinutes(Math.Ceiling(t.TotalMinutes / 30) * 30);
}
