using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FocusApp;

/// <summary>
/// Practice timelines: one row of draggable round times for each practice (Words, Alphabet, Concepts),
/// on one shared time line with today's meetings and lunch, plus an "All practice" row showing every
/// round together. Rounds snap to 15 minutes. "Use for today" changes only today; "Save as default"
/// makes the times every day's. A round that falls in a meeting still waits until the meeting ends.
/// Every practice follows its timeline, in or out of focus sessions.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] Practices = { "words", "alphabet", "concepts" };
    private const int SnapMinutes = 15;
    private const int MaxRounds = 12;
    private Window? _timelineWindow;

    // ---------- The schedule ----------

    private static List<TimeSpan>? ParseTimes(string? text)
    {
        if (text == null) return null;
        var list = new List<TimeSpan>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (TimeSpan.TryParse(part, CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)) list.Add(t);
        return list.OrderBy(t => t).ToList();
    }

    private static string FormatTimes(IEnumerable<TimeSpan> times) =>
        string.Join(",", times.OrderBy(t => t).Select(t => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture)));

    private static string TodayKey => DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The practice's timeline times for today (today's own, then your default), or null for even spacing.</summary>
    private List<TimeSpan>? CustomPracticeTimes(string practice)
    {
        if (_settings.PracticeTodayDate == TodayKey && _settings.PracticeTodayTimes.TryGetValue(practice, out var today))
            return ParseTimes(today);
        return _settings.PracticeTimes.TryGetValue(practice, out var def) ? ParseTimes(def) : null;
    }

    private int PerDayFor(string practice) => practice switch
    {
        "alphabet" => _settings.AlphaPerDay,
        "concepts" => _settings.ConceptPerDay,
        _ => _settings.VocabPerDay
    };

    private Dictionary<string, string> PracticeScheduleSignatures() => new()
    {
        ["words"] = $"{_settings.VocabPerDay}|{_settings.VocabFrom}|{_settings.VocabTo}",
        ["alphabet"] = $"{_settings.AlphaPerDay}|{_settings.AlphaFrom}|{_settings.AlphaTo}",
        ["concepts"] = $"{_settings.ConceptPerDay}|{_settings.ConceptFrom}|{_settings.ConceptTo}"
    };

    /// <summary>
    /// Formerly: changing a practice's rounds a day or hours in Settings cleared its timeline. Since 1.7.1 the timelines
    /// are the only schedule (those settings are hidden), so nothing is cleared.
    /// </summary>
    private void ForgetTimelinesChangedInSettings(Dictionary<string, string> before)
    {
        if (_settings.PracticeOnTimelines) return;
        var after = PracticeScheduleSignatures();
        foreach (var p in Practices)
        {
            if (before[p] == after[p]) continue;
            bool had = _settings.PracticeTimes.Remove(p) | _settings.PracticeTodayTimes.Remove(p);
            if (had) Log.Write($"Practice timeline for {p} cleared: its rounds or hours were changed in Settings");
        }
    }

    private string DoneRecordFor(string practice) => practice switch
    {
        "alphabet" => _settings.AlphaDone,
        "concepts" => _settings.ConceptDone,
        _ => _settings.VocabDone
    };

    private void SetDoneRecordFor(string practice, string record)
    {
        switch (practice)
        {
            case "alphabet": _settings.AlphaDone = record; break;
            case "concepts": _settings.ConceptDone = record; break;
            default: _settings.VocabDone = record; break;
        }
    }

    /// <summary>Changes today's times, keeping rounds you've already done marked done (matched by time).</summary>
    private void ReplaceTodaysTimes(string practice, List<TimeSpan> times, bool asDefault)
    {
        var oldSlots = SlotsFor(PerDayFor(practice), practice);
        var doneTimes = DoneToday(DoneRecordFor(practice)).Where(i => i < oldSlots.Count).Select(i => oldSlots[i].TimeOfDay).ToHashSet();
        times = times.OrderBy(t => t).ToList();

        if (asDefault)
        {
            _settings.PracticeTimes[practice] = FormatTimes(times);
            _settings.PracticeTodayTimes.Remove(practice);
            // Keep Settings' rounds a day and hours in step, so they show what the timeline does.
            int count = times.Count;
            string from = count > 0 ? times[0].ToString(@"hh\:mm", CultureInfo.InvariantCulture) : "";
            string to = count > 0 ? (times[^1] > times[0] ? times[^1] : times[0] + TimeSpan.FromHours(1)).ToString(@"hh\:mm", CultureInfo.InvariantCulture) : "";
            switch (practice)
            {
                case "alphabet": _settings.AlphaPerDay = count; if (count > 0) { _settings.AlphaFrom = from; _settings.AlphaTo = to; } break;
                case "concepts": _settings.ConceptPerDay = count; if (count > 0) { _settings.ConceptFrom = from; _settings.ConceptTo = to; } break;
                default: _settings.VocabPerDay = Math.Max(1, count); if (count > 0) { _settings.VocabFrom = from; _settings.VocabTo = to; } break;
            }
        }
        else
        {
            if (_settings.PracticeTodayDate != TodayKey) { _settings.PracticeTodayTimes.Clear(); _settings.PracticeTodayDate = TodayKey; }
            _settings.PracticeTodayTimes[practice] = FormatTimes(times);
        }

        var done = new HashSet<int>();
        for (int i = 0; i < times.Count; i++) if (doneTimes.Contains(times[i])) done.Add(i);
        SetDoneRecordFor(practice, DoneRecord(done));
        if (practice == "words") _vocabLaterSlot = -1;
        if (practice == "alphabet") _alphaLaterSlot = -1;
        if (practice == "concepts") _conceptLaterSlot = -1;
    }

    /// <summary>
    /// Starts a round for the first practice whose timeline time has come (in a focus session or not): done rounds
    /// don't repeat, Later brings it back in 20 minutes, and meetings, breaks and lunch make it wait.
    /// Returns true when a round started or is waiting, so no other round starts.
    /// </summary>
    private bool TryTimelineRounds()
    {
        if (VocabWindow.IsShowing || Application.Current.Windows.OfType<FlashWindow>().Any()) return true;
        var now = DateTime.Now;
        foreach (var p in Practices)
        {
            bool on = p switch { "alphabet" => _settings.On("alphabet"), "concepts" => _settings.On("concepts") && Concepts.Cards.Count >= 2, _ => _settings.On("greek") };
            var laterUntil = p switch { "alphabet" => _alphaLaterUntil, "concepts" => _conceptLaterUntil, _ => _vocabLaterUntil };
            int laterSlot = p switch { "alphabet" => _alphaLaterSlot, "concepts" => _conceptLaterSlot, _ => _vocabLaterSlot };
            if (!on || now < laterUntil) continue;
            var slots = SlotsFor(PerDayFor(p), p);
            var done = DoneToday(DoneRecordFor(p));
            int due = -1;
            for (int i = 0; i < slots.Count; i++)
                if (!done.Contains(i) && slots[i] <= now && now - slots[i] < TimeSpan.FromHours(3)) { due = i; break; }
            if (due < 0 && laterSlot >= 0 && !done.Contains(laterSlot)) due = laterSlot;
            if (due < 0) continue;
            if (PracticeMustWait()) return true; // waits until the meeting, break or lunch is over
            switch (p)
            {
                case "alphabet": StartAlphaRound(due); break;
                case "concepts": StartConceptRound(due); break;
                default: StartVocabRound(due); break;
            }
            return true;
        }
        return false;
    }

    /// <summary>Today's meetings and lunch (calendar and the running or planned day), for drawing behind the rounds.</summary>
    private List<(DateTime Start, DateTime End, string Name)> MeetingSpansToday()
    {
        var spans = new List<(DateTime, DateTime, string)>();
        try
        {
            foreach (var e in CalendarService.BusyEventsOn(DateTime.Today)) spans.Add((e.Start, e.End, e.Title));
            var (blocks, start) = TodaysSchedule();
            var t = DateTime.Today + start;
            foreach (var b in blocks)
            {
                var end = t + TimeSpan.FromMinutes(b.Minutes);
                if (b.Kind == BlockKind.Meeting || b.Name.Contains("lunch", StringComparison.OrdinalIgnoreCase)) spans.Add((t, end, b.Name));
                t = end;
            }
        }
        catch (Exception ex) { Log.Error("MeetingSpansToday", ex); }
        return spans;
    }

    // ---------- The window ----------

    private void PracticeTimelines_Click(object sender, RoutedEventArgs e) => OpenPracticeTimelines();

    private void OpenPracticeTimelines()
    {
        if (_timelineWindow != null) { _timelineWindow.Activate(); return; }

        var names = new Dictionary<string, string> { ["words"] = "Words", ["alphabet"] = "Alphabet", ["concepts"] = "Concepts", ["lesson"] = "Book lesson", ["allbooks"] = "All-books", ["cardreview"] = "Card review" };
        var rows = Practices.Concat(new[] { "lesson", "cardreview", "allbooks" }).ToArray();
        var colors = new Dictionary<string, string> { ["words"] = "FocusBrush", ["alphabet"] = "BreakBrush", ["concepts"] = "PriorityHigh", ["lesson"] = "MeetingBrush", ["cardreview"] = "PriorityNormal", ["allbooks"] = "Muted" };
        var features = new Dictionary<string, string> { ["words"] = "greek", ["alphabet"] = "alphabet", ["concepts"] = "concepts" };
        var work = rows.ToDictionary(p => p, p => p switch
        {
            "lesson" => TrainingTimes(),
            "allbooks" => ParseTimes(string.Join(",", Course.Config.AllBooksTimes)) ?? new List<TimeSpan>(),
            "cardreview" => ParseTimes(string.Join(",", Course.Config.CardReviewTimes)) ?? new List<TimeSpan>(),
            _ => SlotsFor(PerDayFor(p), p).Select(s => s.TimeOfDay).ToList()
        });
        var doneAt = rows.ToDictionary(p => p, p =>
        {
            if (!Practices.Contains(p)) return new HashSet<TimeSpan>();
            var slots = SlotsFor(PerDayFor(p), p);
            return DoneToday(DoneRecordFor(p)).Where(i => i < slots.Count).Select(i => slots[i].TimeOfDay).ToHashSet();
        });
        var meetings = MeetingSpansToday();
        bool dirty = false;

        // The time line: 6 AM to 9 PM, widened to fit anything outside it.
        var allTimes = work.Values.SelectMany(t => t).Concat(meetings.Where(m => m.Start.Date == DateTime.Today).Select(m => m.Start.TimeOfDay)).Concat(meetings.Where(m => m.End.Date == DateTime.Today).Select(m => m.End.TimeOfDay)).ToList();
        int firstHour = Math.Min(6, allTimes.Count > 0 ? (int)allTimes.Min().TotalHours : 6);
        int lastHour = Math.Max(21, allTimes.Count > 0 ? (int)Math.Ceiling(allTimes.Max().TotalHours) : 21);
        lastHour = Math.Min(24, Math.Max(lastHour, firstHour + 4));
        var axisStart = TimeSpan.FromHours(firstHour);
        var axisEnd = TimeSpan.FromHours(lastHour);

        var grid = new Grid { Margin = new Thickness(24, 8, 24, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var canvases = new List<(Canvas Canvas, string? Practice)>();

        int Limit(string p) => p == "lesson" ? 3 : p == "cardreview" ? 1 : p == "allbooks" ? 6 : MaxRounds;
        double X(TimeSpan t, double width) => (t - axisStart).TotalMinutes / (axisEnd - axisStart).TotalMinutes * width;
        TimeSpan TimeAt(double x, double width)
        {
            double minutes = Math.Clamp(x / Math.Max(1, width), 0, 1) * (axisEnd - axisStart).TotalMinutes;
            int snapped = (int)Math.Round(minutes / SnapMinutes) * SnapMinutes;
            var t = axisStart + TimeSpan.FromMinutes(snapped);
            return t >= TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) - TimeSpan.FromMinutes(SnapMinutes) : t;
        }
        string TimeLabel(TimeSpan t) => DateTime.Today.Add(t).ToString("t");

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        status.SetResourceReference(StyleProperty, "Caption");
        void Changed() { dirty = true; status.Text = "Not saved yet: choose Use for today or Save as default."; RedrawAll(); }

        void AddRow(int row, string label, string? practice, double height)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
            var labelPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 14 };
            labelPanel.Children.Add(name);
            if (practice != null)
            {
                bool on = practice switch
                {
                    "lesson" => Course.Config.On && Course.Packs.Count > 0,
                    "allbooks" => Course.Finished && Course.Config.AllBooksAuto,
                    "cardreview" => Course.Config.CardReviewOn && Course.Packs.Count > 0,
                    _ => _settings.On(features[practice])
                };
                string offText = practice switch { "lesson" => "Not set up (Training page)", "cardreview" => "Off (Training settings)", "allbooks" => "After you finish a book", _ => "Off in Settings" };
                if (!on) { var off = new TextBlock { Text = offText }; off.SetResourceReference(StyleProperty, "Caption"); labelPanel.Children.Add(off); name.Opacity = 0.6; }
                var actions = new WrapPanel();
                Button Small(string text, string tip, Action click)
                {
                    var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 2, 10, 0), FontSize = 11.5 };
                    b.SetResourceReference(StyleProperty, "LinkButton");
                    b.Click += (_, _) => click();
                    return b;
                }
                actions.Children.Add(Small("Add", "Add a round in the biggest free gap", () =>
                {
                    var list = work[practice];
                    if (list.Count >= Limit(practice)) { status.Text = $"{names[practice]} can have up to {Limit(practice)} a day."; return; }
                    var points = new List<TimeSpan> { axisStart }.Concat(list.OrderBy(t => t)).Append(axisEnd).ToList();
                    int best = 0;
                    for (int i = 1; i < points.Count - 1; i++) if (points[i + 1] - points[i] > points[best + 1] - points[best]) best = i;
                    var mid = points[best] + TimeSpan.FromTicks((points[best + 1] - points[best]).Ticks / 2);
                    list.Add(TimeAt(X(mid, 1000), 1000));
                    Changed();
                }));
                actions.Children.Add(Small("Even", "Spread this practice's rounds evenly across its hours", () =>
                {
                    int n = Math.Max(practice == "words" ? 1 : 0, work[practice].Count);
                    work[practice] = EvenSlots(n, practice).Select(s => s.TimeOfDay).Select(t => TimeAt(X(t, 1000), 1000)).ToList();
                    Changed();
                }));
                labelPanel.Children.Add(actions);
            }
            Grid.SetRow(labelPanel, row);
            grid.Children.Add(labelPanel);

            var canvas = new Canvas { ClipToBounds = false, Background = Brushes.Transparent, Margin = new Thickness(0, 6, 0, 6) };
            Grid.SetRow(canvas, row);
            Grid.SetColumn(canvas, 1);
            grid.Children.Add(canvas);
            canvases.Add((canvas, practice));
            canvas.SizeChanged += (_, _) => Redraw(canvas, practice);
            if (practice != null)
            {
                canvas.ToolTip = "Double-click to add a round. Drag a round to move it; right-click it to remove it.";
                canvas.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount != 2 || e.OriginalSource != canvas) return;
                    var list = work[practice];
                    if (list.Count >= Limit(practice)) { status.Text = $"{names[practice]} can have up to {Limit(practice)} a day."; return; }
                    list.Add(TimeAt(e.GetPosition(canvas).X, canvas.ActualWidth));
                    Changed();
                };
            }
        }

        void RedrawAll() { foreach (var (c, p) in canvases) Redraw(c, p); }

        void Redraw(Canvas canvas, string? practice)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var track = new Rectangle { Width = w, Height = h, RadiusX = 8, RadiusY = 8, Opacity = 0.5, IsHitTestVisible = false };
            track.SetResourceReference(Shape.FillProperty, "Panel2");
            canvas.Children.Add(track);

            foreach (var (start, end, name) in meetings)
            {
                double x1 = Math.Max(0, X(start.Date < DateTime.Today ? axisStart : start.TimeOfDay, w)), x2 = Math.Min(w, X(end.Date > DateTime.Today ? axisEnd : end.TimeOfDay, w));
                if (x2 <= x1) continue;
                var band = new Rectangle { Width = x2 - x1, Height = h, Opacity = 0.45, ToolTip = $"{name}  {start:t} to {end:t}. A round here waits until this ends.", IsHitTestVisible = practice == null };
                band.SetResourceReference(Shape.FillProperty, "MeetingTint");
                Canvas.SetLeft(band, x1);
                canvas.Children.Add(band);
                if (practice == null && x2 - x1 > 46)
                {
                    var t = new TextBlock { Text = name, FontSize = 10.5, Width = x2 - x1 - 6, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
                    t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                    Canvas.SetLeft(t, x1 + 4); Canvas.SetTop(t, 2);
                    canvas.Children.Add(t);
                }
            }

            // Hour marks
            for (int hour = firstHour; hour <= lastHour; hour++)
            {
                double x = X(TimeSpan.FromHours(hour), w);
                var tick = new Rectangle { Width = 1, Height = h, Opacity = 0.35, IsHitTestVisible = false };
                tick.SetResourceReference(Shape.FillProperty, "Line");
                Canvas.SetLeft(tick, x);
                canvas.Children.Add(tick);
            }

            var now = DateTime.Now.TimeOfDay;
            if (now > axisStart && now < axisEnd)
            {
                var nowLine = new Rectangle { Width = 2, Height = h + 6, IsHitTestVisible = false, ToolTip = "Now" };
                nowLine.SetResourceReference(Shape.FillProperty, "Text");
                Canvas.SetLeft(nowLine, X(now, w) - 1); Canvas.SetTop(nowLine, -3);
                canvas.Children.Add(nowLine);
            }

            if (practice == null)
            {
                // Every practice's rounds together, as dots, so you can spread them apart or put them back to back.
                int lane = 0;
                foreach (var p in rows)
                {
                    double y = 8 + lane * (h - 16) / (rows.Length - 1.0);
                    foreach (var t in work[p])
                    {
                        var dot = new Ellipse { Width = 12, Height = 12, ToolTip = $"{names[p]}  {TimeLabel(t)}" };
                        dot.SetResourceReference(Shape.FillProperty, colors[p]);
                        Canvas.SetLeft(dot, X(t, w) - 6); Canvas.SetTop(dot, y - 6);
                        canvas.Children.Add(dot);
                    }
                    lane++;
                }
                return;
            }

            var list = work[practice];
            foreach (var time in list.OrderBy(t => t).ToList())
            {
                bool done = doneAt[practice].Contains(time);
                var text = new TextBlock { Text = (done ? "✓ " : "") + TimeLabel(time), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 2, 8, 2) };
                text.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
                var handle = new Border { CornerRadius = new CornerRadius(10), Child = text, Cursor = Cursors.SizeWE, Opacity = done ? 0.6 : 1,
                                          ToolTip = done ? "Done today. Drag to move, right-click to remove." : "Drag to move, right-click to remove." };
                handle.SetResourceReference(Border.BackgroundProperty, colors[practice]);
                handle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double hw = handle.DesiredSize.Width, hh = handle.DesiredSize.Height;
                Canvas.SetLeft(handle, X(time, w) - hw / 2);
                Canvas.SetTop(handle, (h - hh) / 2);
                canvas.Children.Add(handle);

                var current = time;
                bool dragging = false;
                handle.MouseLeftButtonDown += (_, e) => { dragging = true; handle.CaptureMouse(); e.Handled = true; };
                handle.MouseMove += (_, e) =>
                {
                    if (!dragging) return;
                    var t = TimeAt(e.GetPosition(canvas).X, w);
                    Canvas.SetLeft(handle, X(t, w) - hw / 2);
                    text.Text = TimeLabel(t);
                    handle.Tag = t;
                };
                handle.MouseLeftButtonUp += (_, e) =>
                {
                    if (!dragging) return;
                    dragging = false;
                    handle.ReleaseMouseCapture();
                    if (handle.Tag is TimeSpan moved && moved != current)
                    {
                        int i = list.IndexOf(current);
                        if (i >= 0) list[i] = moved;
                        doneAt[practice].Remove(current);
                        Changed();
                    }
                    e.Handled = true;
                };
                var menu = new ContextMenu();
                var remove = new MenuItem { Header = "Remove this round", IsEnabled = !((practice == "words" || practice == "lesson") && list.Count <= 1) };
                remove.Click += (_, _) => { list.Remove(current); Changed(); };
                menu.Items.Add(remove);
                handle.ContextMenu = menu;
            }
        }

        // Rows: hour labels, All practice, then each practice.
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        var hours = new Canvas();
        Grid.SetColumn(hours, 1);
        grid.Children.Add(hours);
        hours.SizeChanged += (_, _) =>
        {
            hours.Children.Clear();
            for (int hour = firstHour; hour <= lastHour; hour += hours.ActualWidth < 700 ? 2 : 1)
            {
                var t = new TextBlock { Text = DateTime.Today.AddHours(hour).ToString("h tt").ToLowerInvariant().Replace(" ", ""), FontSize = 11 };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                Canvas.SetLeft(t, X(TimeSpan.FromHours(hour), hours.ActualWidth) - 10);
                Canvas.SetTop(t, 4);
                hours.Children.Add(t);
            }
        };
        AddRow(1, "All practice", null, 74);
        int r = 2;
        foreach (var p in rows) AddRow(r++, names[p], p, 70);

        var title = new TextBlock { Text = "Practice timelines", Margin = new Thickness(24, 20, 24, 0) };
        title.SetResourceReference(StyleProperty, "Heading");
        var intro = new TextBlock
        {
            Text = "Today's rounds for each practice, with your meetings and lunch shaded. Drag a round to move it (it snaps to 15 minutes), " +
                   "double-click a row to add one, right-click a round to remove it. Each round comes up at its time whether or not a focus session is running (Focus just needs to be open, even in the tray); " +
                   "one that lands in a meeting, break or lunch waits until it's over. The Book lesson row holds your three lesson times (the last is required); Card review is the evening pop-up with the day's hardest memory cards. " +
                   "The All practice row shows every round together, so you can spread them apart or put them back to back.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 6, 24, 6)
        };
        intro.SetResourceReference(StyleProperty, "Caption");

        var today = new Button { Content = "Use for today", Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(16, 6, 16, 6) };
        var save = new Button { Content = "Save as default", Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(16, 6, 16, 6) };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        var close = new Button { Content = "Close", Padding = new Thickness(16, 6, 16, 6) };
        var buttons = new DockPanel { Margin = new Thickness(24, 10, 24, 20), LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(save); right.Children.Add(today); right.Children.Add(close);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(status);

        var layout = new DockPanel();
        var weekends = new CheckBox { Content = "Pause all practice on weekends", IsChecked = _settings.PauseWeekends, Margin = new Thickness(24, 2, 24, 8),
                                      ToolTip = "On Saturdays and Sundays nothing on these timelines pops up by itself; you can still start any of it yourself." };
        weekends.Checked += (_, _) => SetPauseWeekends(true);
        weekends.Unchecked += (_, _) => SetPauseWeekends(false);
        var top = new StackPanel(); top.Children.Add(title); top.Children.Add(intro); top.Children.Add(weekends);
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(top); layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var window = new Window
        {
            Title = "Practice timelines", Width = 1000, Height = 680, MinWidth = 640, MinHeight = 400, Content = layout,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = IsVisible ? this : null
        };
        window.SetResourceReference(BackgroundProperty, "Panel");
        window.SetResourceReference(ForegroundProperty, "Text");
        _timelineWindow = window;

        void Apply(bool asDefault)
        {
            foreach (var p in Practices) ReplaceTodaysTimes(p, work[p].Distinct().ToList(), asDefault);
            SaveSettingsQuietly();
            // The book lesson's three times and All-books practice times (these are always every day's).
            Course.Config.Times = work["lesson"].Distinct().OrderBy(t => t).Select(t => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture)).ToList();
            Course.Config.CardReviewTimes = work["cardreview"].Distinct().OrderBy(t => t).Select(t => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture)).ToList();
            Course.Config.AllBooksTimes = work["allbooks"].Distinct().OrderBy(t => t).Select(t => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture)).ToList();
            Course.SaveConfig();
            try { FillVocabSettings(); } catch (Exception ex) { Log.Error("Timelines.Fill", ex); } // Settings shows the new rounds a day and hours
            dirty = false;
            status.Text = asDefault ? "Saved. These are your times every day." : "Saved for today. Tomorrow goes back to your default times.";
        }
        today.Click += (_, _) => Apply(asDefault: false);
        save.Click += (_, _) => Apply(asDefault: true);
        close.Click += (_, _) => window.Close();
        window.Closing += (_, e) =>
        {
            if (!dirty) return;
            var answer = MessageBox.Show(window, "Save these practice times as your default before closing?\n\nYes saves them for every day, No closes without saving.",
                "Practice timelines", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) e.Cancel = true;
            else if (answer == MessageBoxResult.Yes) Apply(asDefault: true);
        };
        window.Closed += (_, _) => _timelineWindow = null;
        window.Show();
    }
}
