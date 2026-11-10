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
/// Today's practice on the Training page: one lane per practice, each round a dot in that practice's color at its exact
/// time, with the time in small type beside it. Filled dots are done, rings are still to do, and a white ring marks the
/// next round. It is also the practice timeline editor: drag a dot to move it (5-minute steps), click a lane to add a
/// round, right-click a dot to remove it. Changes save straight away as your daily times.
/// </summary>
public partial class MainWindow
{
    private static readonly (string Key, string Name, string Color)[] PracticeLanes =
    {
        ("words", "Greek words", "#6FB5A8"), ("alphabet", "Greek alphabet", "#8FA8E0"), ("concepts", "Greek concepts", "#C79BE0"),
        ("lesson", "Book lesson", "#F0D27A"), ("allbooks", "Books practice", "#E5A65E"), ("cardreview", "Book flashcards", "#E58FB0"),
        ("timeline", "Timeline", "#A9CC72")
    };

    /// <summary>Each practice's color (the Day plan and Dashboard markers use these too).</summary>
    public static Brush PracticeBrush(string key)
    {
        var hex = PracticeLanes.FirstOrDefault(l => l.Key == key).Color ?? "#9AA8A6";
        return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    }

    private bool LaneOn(string key) => key switch
    {
        "words" => _settings.On("greek"),
        "alphabet" => _settings.On("alphabet"),
        "concepts" => _settings.On("concepts"),
        "lesson" => Course.Config.On && Course.Config.Courses.Count > 0,
        "allbooks" => Course.Config.Courses.Count > 0,
        "cardreview" => Course.Config.CardReviewOn && Course.Config.Courses.Count > 0,
        "timeline" => Course.Config.TimelineOn && Course.Config.Courses.Count > 0,
        _ => false
    };

    private static int LaneLimit(string key) => key switch { "lesson" => 3, "cardreview" => 1, "allbooks" => 6, "timeline" => 4, _ => MaxRounds };

    /// <summary>Every practice's times today (the working copy the lanes edit).</summary>
    private Dictionary<string, List<TimeSpan>> PracticeWork() => PracticeLanes.ToDictionary(l => l.Key, l => l.Key switch
    {
        "lesson" => TrainingTimes(),
        "allbooks" => ParseTimes(string.Join(",", Course.Config.AllBooksTimes)) ?? new List<TimeSpan>(),
        "cardreview" => ParseTimes(string.Join(",", Course.Config.CardReviewTimes)) ?? new List<TimeSpan>(),
        "timeline" => ParseTimes(string.Join(",", Course.Config.TimelineTimes)) ?? new List<TimeSpan>(),
        _ => SlotsFor(PerDayFor(l.Key), l.Key).Select(s => s.TimeOfDay).ToList()
    });

    /// <summary>Which of a practice's times today are done.</summary>
    private HashSet<TimeSpan> LaneDone(string key, List<TimeSpan> times)
    {
        var sorted = times.OrderBy(t => t).ToList();
        bool Since(string kind, TimeSpan at) => Course.Events.Any(e => e.Kind == kind && e.Day == Course.Today && e.At.ToLocalTime().TimeOfDay >= at);
        switch (key)
        {
            case "lesson": return sorted.Take(Course.DoneTodayCount()).ToHashSet();
            case "allbooks": return sorted.Where(t => Since("allBooksDone", t)).ToHashSet();
            case "cardreview": return sorted.Where(t => Since("cardReviewDone", t)).ToHashSet();
            case "timeline": return sorted.Where(t => Since("timelineDone", t)).ToHashSet();
            default:
                try
                {
                    var slots = SlotsFor(PerDayFor(key), key);
                    return DoneToday(DoneRecordFor(key)).Where(i => i < slots.Count).Select(i => slots[i].TimeOfDay).ToHashSet();
                }
                catch { return new(); }
        }
    }

    private Border BuildPracticeLanes()
    {
        var work = PracticeWork();
        var lanes = PracticeLanes.Where(l => LaneOn(l.Key)).ToList();
        var panel = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var hint = TT("Drag to move · click a row to add · right-click to remove", 11.5, brush: "Muted");
        hint.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(hint, Dock.Right);
        head.Children.Add(hint);
        head.Children.Add(TT("Today's practice", 16, bold: true));
        panel.Children.Add(head);
        if (PracticePausedToday()) panel.Children.Add(TT("Practice is paused today (weekend). These are your daily times.", 12, brush: "Muted", top: 0));
        if (lanes.Count == 0) { panel.Children.Add(TT("No practices are switched on.", 13, brush: "Muted", top: 6)); }
        else
        {
            const double laneH = 34, top = 26;
            var canvas = new Canvas { Height = top + lanes.Count * laneH + 4, Background = Brushes.Transparent, ClipToBounds = true };
            canvas.SizeChanged += (_, _) => DrawLanes(canvas, lanes, work, laneH, top);
            panel.Children.Add(canvas);
        }
        var card = TBox(panel, padding: new Thickness(22, 16, 22, 12));
        card.Margin = new Thickness(0, 16, 0, 0);
        return card;
    }

    private void DrawLanes(Canvas canvas, List<(string Key, string Name, string Color)> lanes, Dictionary<string, List<TimeSpan>> work, double laneH, double top)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth;
        if (w < 200) return;
        const double left = 140, right = 52;
        var all = lanes.SelectMany(l => work[l.Key]).ToList();
        double startH = Math.Min(6, all.Count > 0 ? Math.Floor(all.Min().TotalHours) : 6);
        double endH = Math.Max(22, all.Count > 0 ? Math.Ceiling(all.Max().TotalHours) : 22);
        double span = (endH - startH) * 60;
        double X(TimeSpan t) => left + (t.TotalMinutes - startH * 60) / span * (w - left - right);
        TimeSpan At(double x)
        {
            double minutes = startH * 60 + Math.Clamp((x - left) / (w - left - right), 0, 1) * span;
            int snapped = (int)Math.Round(minutes / 5) * 5;
            return TimeSpan.FromMinutes(Math.Clamp(snapped, 0, 24 * 60 - 5));
        }
        Brush Res(string key) => (Brush)FindResource(key);
        double bottom = top + lanes.Count * laneH;

        // Hours across the top, with faint guides down through the lanes.
        for (double h = Math.Ceiling(startH / 3) * 3; h <= endH; h += 3)
        {
            var guide = new Line { X1 = X(TimeSpan.FromHours(h)), X2 = X(TimeSpan.FromHours(h)), Y1 = top - 6, Y2 = bottom, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 4 }, Stroke = Res("Line") };
            canvas.Children.Add(guide);
            var lab = TT(h == 12 ? "Noon" : DateTime.Today.AddHours(h).ToString("h tt"), 11, brush: "Muted");
            lab.Width = 60; lab.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(lab, X(TimeSpan.FromHours(h)) - 30); Canvas.SetTop(lab, 0);
            canvas.Children.Add(lab);
        }

        // Meetings today: faint bands behind the lanes.
        try
        {
            foreach (var m in MeetingSpansToday().Where(m => m.Start.Date == DateTime.Today))
            {
                double x1 = X(m.Start.TimeOfDay), x2 = X(m.End.Date > DateTime.Today ? TimeSpan.FromHours(endH) : m.End.TimeOfDay);
                var band = new Rectangle { Width = Math.Max(2, x2 - x1), Height = bottom - top + 6, Opacity = 0.12, ToolTip = $"{m.Name} · {m.Start:t}–{m.End:t}", Fill = Res("MeetingBrush") };
                Canvas.SetLeft(band, x1); Canvas.SetTop(band, top - 3);
                canvas.Children.Add(band);
            }
        }
        catch (Exception ex) { Log.Error("Practice lanes meetings", ex); }

        // The next round of the day: the earliest not done from now on.
        var now = DateTime.Now.TimeOfDay;
        var doneByLane = lanes.ToDictionary(l => l.Key, l => LaneDone(l.Key, work[l.Key]));
        (string Key, TimeSpan At)? next = lanes.SelectMany(l => work[l.Key].Where(t => t >= now && !doneByLane[l.Key].Contains(t)).Select(t => (l.Key, t)))
                                               .OrderBy(x => x.t).Select(x => ((string, TimeSpan)?)(x.Key, x.t)).FirstOrDefault();

        for (int i = 0; i < lanes.Count; i++)
        {
            var (key, name, color) = lanes[i];
            var brush = PracticeBrush(key);
            double y = top + i * laneH + laneH / 2;
            var times = work[key].OrderBy(t => t).ToList();
            var done = doneByLane[key];

            // The lane: its name on the left, a thin line, and the count on the right. Click the line to add a round.
            var mark = new Ellipse { Width = 9, Height = 9, Fill = brush };
            Canvas.SetLeft(mark, 2); Canvas.SetTop(mark, y - 4.5); canvas.Children.Add(mark);
            var label = TT(name, 12.5); label.TextTrimming = TextTrimming.CharacterEllipsis; label.Width = left - 26;
            Canvas.SetLeft(label, 18); Canvas.SetTop(label, y - 9); canvas.Children.Add(label);
            var count = TT(times.Count == 0 ? "none" : $"{times.Count(done.Contains)} of {times.Count}", 11.5, brush: "Muted");
            count.Width = right - 6; count.TextAlignment = TextAlignment.Right;
            Canvas.SetLeft(count, w - right + 6); Canvas.SetTop(count, y - 8); canvas.Children.Add(count);
            var line = new Rectangle { Width = w - left - right, Height = 1, Fill = Res("Line") };
            Canvas.SetLeft(line, left); Canvas.SetTop(line, y); canvas.Children.Add(line);
            var hit = new Rectangle { Width = w - left - right, Height = laneH - 4, Fill = Brushes.Transparent, Cursor = Cursors.Hand,
                                      ToolTip = times.Count >= LaneLimit(key) ? $"{name}: at most {LaneLimit(key)} a day" : $"Click to add a {name} round" };
            Canvas.SetLeft(hit, left); Canvas.SetTop(hit, y - (laneH - 4) / 2); canvas.Children.Add(hit);
            string laneKey = key;
            hit.MouseLeftButtonUp += (_, e) =>
            {
                if (work[laneKey].Count >= LaneLimit(laneKey)) return;
                var t = At(e.GetPosition(canvas).X);
                if (work[laneKey].Contains(t)) return;
                work[laneKey].Add(t);
                SavePracticeWork(work);
                DrawLanes(canvas, lanes, work, laneH, top);
            };

            for (int k = 0; k < times.Count; k++)
            {
                var t = times[k];
                bool isDone = done.Contains(t), isNext = next is { } n && n.Key == key && n.At == t;
                double x = X(t);
                // The time: to the right of the dot, or above it when the next round in this lane is too close.
                bool roomy = k == times.Count - 1 || X(times[k + 1]) - x > 46;
                var time = TT(DateTime.Today.Add(t).ToString("h:mm"), 9.5, brush: isNext ? "Text" : "Muted");
                time.Opacity = isDone ? 0.7 : 1;
                time.IsHitTestVisible = false;
                time.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (roomy) { Canvas.SetLeft(time, x + (isNext ? 12 : 9)); Canvas.SetTop(time, y - 7); }
                else { Canvas.SetLeft(time, x - time.DesiredSize.Width / 2); Canvas.SetTop(time, y - 20); }
                canvas.Children.Add(time);

                if (isNext)
                {
                    var ring = new Ellipse { Width = 19, Height = 19, StrokeThickness = 1.5, Stroke = Res("Text"), IsHitTestVisible = false };
                    Canvas.SetLeft(ring, x - 9.5); Canvas.SetTop(ring, y - 9.5); canvas.Children.Add(ring);
                }
                var dot = new Ellipse { Width = 11, Height = 11, StrokeThickness = 2, Stroke = brush, Fill = isDone ? brush : Res("Panel"), Cursor = Cursors.SizeWE };
                dot.ToolTip = $"{name} · {DateTime.Today.Add(t):t}" + (isDone ? " · done" : isNext ? " · next" : "") + "\nDrag to move · right-click to remove";
                Canvas.SetLeft(dot, x - 5.5); Canvas.SetTop(dot, y - 5.5); canvas.Children.Add(dot);

                // Drag to move (5-minute steps); the time follows the dot while dragging.
                var from = t;
                bool dragging = false;
                dot.MouseLeftButtonDown += (_, e) => { dragging = true; dot.CaptureMouse(); e.Handled = true; };
                dot.MouseMove += (_, e) =>
                {
                    if (!dragging) return;
                    var nt = At(e.GetPosition(canvas).X);
                    Canvas.SetLeft(dot, X(nt) - 5.5);
                    time.Text = DateTime.Today.Add(nt).ToString("h:mm");
                    Canvas.SetLeft(time, X(nt) + 9); Canvas.SetTop(time, y - 7);
                };
                dot.MouseLeftButtonUp += (_, e) =>
                {
                    if (!dragging) return;
                    dragging = false;
                    dot.ReleaseMouseCapture();
                    e.Handled = true;
                    var nt = At(e.GetPosition(canvas).X);
                    if (nt == from || work[laneKey].Contains(nt)) { DrawLanes(canvas, lanes, work, laneH, top); return; }
                    work[laneKey].Remove(from);
                    work[laneKey].Add(nt);
                    SavePracticeWork(work);
                    DrawLanes(canvas, lanes, work, laneH, top);
                };
                dot.LostMouseCapture += (_, _) => { if (dragging) { dragging = false; DrawLanes(canvas, lanes, work, laneH, top); } };
                // Right-click: remove.
                dot.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var menu = new ContextMenu { PlacementTarget = dot };
                    var remove = new MenuItem { Header = $"Remove the {DateTime.Today.Add(from):t} {name} round" };
                    remove.Click += (_, _) => { work[laneKey].Remove(from); SavePracticeWork(work); DrawLanes(canvas, lanes, work, laneH, top); };
                    menu.Items.Add(remove);
                    menu.IsOpen = true;
                };
            }
        }

        // Now.
        if (now.TotalHours > startH && now.TotalHours < endH)
        {
            var nowLine = new Rectangle { Width = 2, Height = bottom - top + 10, Fill = (Brush)new BrushConverter().ConvertFromString("#E0705F")!, ToolTip = "Now", IsHitTestVisible = false };
            Canvas.SetLeft(nowLine, X(now) - 1); Canvas.SetTop(nowLine, top - 6); canvas.Children.Add(nowLine);
        }
    }
}
