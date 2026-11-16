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
/// Meetings on the Tasks page: in a By project view of Today, Tomorrow or List, the day's calendar meetings lead the list
/// under Meetings (time, title, length, and Join for online meetings), followed by your #meeting tasks. The Meetings row
/// under Projects lists them by day: today, this week or next week. Over meetings drop off (a #meeting task is finished
/// an hour after its time); cancelled ones go when the calendar updates.
/// </summary>
public partial class MainWindow
{
    private string _meetingRange = "week";
    private DateTime _meetingsTicked;

    /// <summary>The working week's meetings still to come (on a weekend, the coming week's).</summary>
    private List<CalendarEvent> MeetingsThisWeek()
    {
        var (from, to) = Meetings.WorkWeek();
        return Meetings.Between(from, to);
    }

    /// <summary>Once a minute: finishes #meeting tasks that are over, and drops meetings that ended from every list.</summary>
    private void MeetingsTick()
    {
        if ((DateTime.Now - _meetingsTicked).TotalSeconds < 55) return;
        _meetingsTicked = DateTime.Now;
        try
        {
            using (TaskStores.Use(TaskStores.Main))
            {
                var over = TaskBin.All.Where(Meetings.IsOver).ToList();
                foreach (var t in over) FinishListTask(t, t.From, t.Spent);
                if (over.Count > 0) TaskBin.Save();
            }
            RefreshMeetingRows();
            RefreshDashTasks();
            UpdateOverlayTasks();
        }
        catch (Exception ex) { Log.Error("Meetings tick", ex); }
    }

    /// <summary>Fills the Meetings rows above the Tasks page list (or hides them).</summary>
    private void RefreshMeetingRows()
    {
        if (MeetingsPanel == null) return;
        MeetingsPanel.Children.Clear();
        bool work = !ReferenceEquals(TasksPageSet, TaskStores.Personal);
        string list = CurrentList;
        // Meetings show as tasks in Today, Tomorrow and List, in every view (under a Meetings heading By project).
        bool shown = list is "today" or "tomorrow" or "list" && _binFilter is "all" or "today";
        bool byProject = shown && ListMode(list) == "project";
        bool filter = _binFilter == "meetings";
        if (!work || (!shown && !filter)) { MeetingsPanel.Visibility = Visibility.Collapsed; return; }

        if (filter)
        {
            // The Meetings filter: by day, for the range chosen here.
            var head = new DockPanel { Margin = new Thickness(4, 4, 0, 8) };
            var ranges = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (key, name) in new[] { ("today", "Today"), ("week", DateTime.Today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "Coming week" : "This week"), ("next", "Week after") })
            {
                var b = new Button { Content = name, Margin = new Thickness(0, 0, 12, 0), FontWeight = _meetingRange == key ? FontWeights.SemiBold : FontWeights.Normal };
                b.SetResourceReference(StyleProperty, "LinkButton");
                if (_meetingRange == key) b.SetResourceReference(ForegroundProperty, "FocusBrush");
                b.Click += (_, _) => { _meetingRange = key; RefreshMeetingRows(); };
                ranges.Children.Add(b);
            }
            head.Children.Add(ranges);
            MeetingsPanel.Children.Add(head);
            var (from, to) = _meetingRange switch
            {
                "today" => (DateTime.Today, DateTime.Today.AddDays(1)),
                "next" => Meetings.WorkWeek(1),
                _ => Meetings.WorkWeek()
            };
            var all = Meetings.Between(from, to).Where(e => !Meetings.Dismissed.Contains(Meetings.KeyOf(e))).ToList();
            if (all.Count == 0) MeetingsPanel.Children.Add(MeetingCaption(CalendarService.Sources.Count == 0 ? "No calendar connected. Add your Outlook calendar on the Calendar page." : "No meetings."));
            foreach (var day in all.GroupBy(e => e.Start.Date))
            {
                MeetingsPanel.Children.Add(MeetingHeading(day.Key == DateTime.Today ? "Today" : day.Key == DateTime.Today.AddDays(1) ? "Tomorrow" : day.Key.ToString("dddd, MMMM d"), null));
                foreach (var e in day) MeetingsPanel.Children.Add(MeetingRow(e));
            }
            if (TaskBin.All.Any(t => t.IsMeetingTask)) MeetingsPanel.Children.Add(MeetingHeading("Typed in with #meeting", null));
            MeetingsPanel.Visibility = Visibility.Visible;
            return;
        }

        // By project: the day's meetings under Meetings, first.
        var dayOf = list == "tomorrow" ? NextDay.Date : DateTime.Today;
        var meetings = Meetings.On(dayOf);
        bool typed = byProject && _binView?.Cast<BinTask>().Any(t => t.IsMeetingTask) == true;
        if (meetings.Count == 0 && !typed) { MeetingsPanel.Visibility = Visibility.Collapsed; return; }
        if (byProject) MeetingsPanel.Children.Add(MeetingHeading(Meetings.Name, Meetings.Color));
        foreach (var e in meetings) MeetingsPanel.Children.Add(MeetingRow(e));
        MeetingsPanel.Visibility = Visibility.Visible;
    }

    private static TextBlock MeetingCaption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(4, 0, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    /// <summary>A heading like the project headings in By project views (dot, name, thin line).</summary>
    private static FrameworkElement MeetingHeading(string text, string? color)
    {
        var dock = new DockPanel { Margin = new Thickness(4, 14, 0, 6) };
        if (color != null)
        {
            var dot = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(CalendarLook.ColorOf(color)) };
            dock.Children.Add(dot);
        }
        var name = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        dock.Children.Add(name);
        var line = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Center };
        line.SetResourceReference(Shape.FillProperty, "Line");
        dock.Children.Add(line);
        return dock;
    }

    /// <summary>The small join dot beside a meeting: click to open the meeting (Teams, Zoom, Meet, Webex).</summary>
    public static FrameworkElement JoinDot(CalendarEvent e)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(CalendarLook.ColorOf(Meetings.Color)) };
        var hit = new Border { Child = dot, Background = Brushes.Transparent, Padding = new Thickness(6, 4, 4, 4), Cursor = Cursors.Hand,
                               VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Join {e.JoinName} meeting" };
        hit.MouseLeftButtonDown += (_, ev) => ev.Handled = true;
        hit.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; CalendarService.Join(e.JoinUrl); };
        return hit;
    }

    /// <summary>
    /// A meeting as a task row: a check (ticking it takes it off the lists; it also goes when it's over), the time, the
    /// title with its join dot, and the length. Click it for its details.
    /// </summary>
    private FrameworkElement MeetingRow(CalendarEvent e, bool compact = false)
    {
        var dock = new DockPanel();
        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Done (it also goes by itself when it's over)" };
        check.SetResourceReference(StyleProperty, "TaskCheck");
        check.Click += (_, _) => { Meetings.Dismissed.Add(Meetings.KeyOf(e)); RefreshMeetingRows(); RefreshDashTasks(); UpdateOverlayTasks(); };
        DockPanel.SetDock(check, Dock.Left);
        dock.Children.Add(check);
        var length = new TextBlock { Text = Meetings.Length(e), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        length.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(length, Dock.Right);
        dock.Children.Add(length);
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var time = new TextBlock { Text = e.Start.ToString("t") + "  ", VerticalAlignment = VerticalAlignment.Center };
        time.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var title = new TextBlock { Text = e.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        if (compact) { time.FontSize = 13; title.FontSize = 13; }
        else { time.SetResourceReference(TextBlock.FontSizeProperty, "TaskFontSize"); title.SetResourceReference(TextBlock.FontSizeProperty, "TaskFontSize"); }
        line.Children.Add(time); line.Children.Add(title);
        if (e.JoinUrl.Length > 0) line.Children.Add(JoinDot(e));
        dock.Children.Add(line);
        var card = new Border { Child = dock, Cursor = Cursors.Hand, ToolTip = CalendarLook.Details(e), Background = Brushes.Transparent };
        if (!compact)
        {
            card.SetResourceReference(Border.BackgroundProperty, "TaskRowBg");
            card.SetResourceReference(Border.CornerRadiusProperty, "TaskRowCorner");
            card.SetResourceReference(Border.PaddingProperty, "TaskRowPadding");
            card.SetResourceReference(Border.MarginProperty, "TaskRowMargin");
        }
        else card.Margin = new Thickness(0, 0, 0, 6);
        card.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; CalendarLook.ShowDetails(e, card); };
        return card;
    }

    /// <summary>The dashboard's task list: today's meetings first, as tasks (none for Undated or Personal).</summary>
    private void FillDashMeetings(DateTime? day)
    {
        if (DashMeetings == null) return;
        DashMeetings.Children.Clear();
        if (day is DateTime d && !TaskStores.IsPersonal)
            foreach (var e in Meetings.On(d)) DashMeetings.Children.Add(MeetingRow(e, compact: true));
        DashMeetings.Visibility = DashMeetings.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Upcoming: each day's meetings as small rows under the day's heading.</summary>
    private List<FrameworkElement> UpcomingMeetings(DateTime day) =>
        TaskStores.IsPersonal ? new() : Meetings.On(day).Select(e => MeetingRow(e)).ToList();
}
