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

    private List<CalendarEvent> MeetingsThisWeek()
    {
        int sinceMonday = ((int)DateTime.Today.DayOfWeek + 6) % 7;
        return Meetings.Between(DateTime.Today, DateTime.Today.AddDays(7 - sinceMonday));
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
        bool byProject = list is "today" or "tomorrow" or "list" && ListMode(list) == "project" && _binFilter is "all" or "meetings" or "today";
        bool filter = _binFilter == "meetings";
        if (!work || (!byProject && !filter)) { MeetingsPanel.Visibility = Visibility.Collapsed; return; }

        if (filter)
        {
            // The Meetings filter: by day, for the range chosen here.
            var head = new DockPanel { Margin = new Thickness(4, 4, 0, 8) };
            var ranges = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (key, name) in new[] { ("today", "Today"), ("week", "This week"), ("next", "Next week") })
            {
                var b = new Button { Content = name, Margin = new Thickness(0, 0, 12, 0), FontWeight = _meetingRange == key ? FontWeights.SemiBold : FontWeights.Normal };
                b.SetResourceReference(StyleProperty, "LinkButton");
                if (_meetingRange == key) b.SetResourceReference(ForegroundProperty, "FocusBrush");
                b.Click += (_, _) => { _meetingRange = key; RefreshMeetingRows(); };
                ranges.Children.Add(b);
            }
            head.Children.Add(ranges);
            MeetingsPanel.Children.Add(head);
            int sinceMonday = ((int)DateTime.Today.DayOfWeek + 6) % 7;
            var monday = DateTime.Today.AddDays(-sinceMonday);
            var (from, to) = _meetingRange switch
            {
                "today" => (DateTime.Today, DateTime.Today.AddDays(1)),
                "next" => (monday.AddDays(7), monday.AddDays(14)),
                _ => (DateTime.Today, monday.AddDays(7))
            };
            var all = Meetings.Between(from, to);
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
        bool typed = _binView?.Cast<BinTask>().Any(t => t.IsMeetingTask) == true;
        if (meetings.Count == 0 && !typed) { MeetingsPanel.Visibility = Visibility.Collapsed; return; }
        MeetingsPanel.Children.Add(MeetingHeading(Meetings.Name, Meetings.Color));
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

    /// <summary>A meeting row, in the task rows' look: time, title, length, Join. Click for its details.</summary>
    private FrameworkElement MeetingRow(CalendarEvent e)
    {
        var dock = new DockPanel();
        var time = new TextBlock { Text = e.Start.ToString("t"), Width = 72, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        time.SetResourceReference(TextBlock.ForegroundProperty, "FocusBrush");
        time.SetResourceReference(TextBlock.FontSizeProperty, "TaskFontSize");
        DockPanel.SetDock(time, Dock.Left);
        dock.Children.Add(time);
        if (e.JoinUrl.Length > 0)
        {
            var pill = CalendarLook.JoinPill(e.JoinUrl, e.JoinName, 12);
            pill.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(pill, Dock.Right);
            dock.Children.Add(pill);
        }
        var length = new TextBlock { Text = Meetings.Length(e), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        length.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(length, Dock.Right);
        dock.Children.Add(length);
        var title = new TextBlock { Text = e.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        title.SetResourceReference(TextBlock.FontSizeProperty, "TaskFontSize");
        dock.Children.Add(title);
        var card = new Border { Child = dock, Cursor = Cursors.Hand, ToolTip = CalendarLook.Details(e) };
        card.SetResourceReference(Border.BackgroundProperty, "TaskRowBg");
        card.SetResourceReference(Border.CornerRadiusProperty, "TaskRowCorner");
        card.SetResourceReference(Border.PaddingProperty, "TaskRowPadding");
        card.SetResourceReference(Border.MarginProperty, "TaskRowMargin");
        card.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; CalendarLook.ShowDetails(e, card); };
        return card;
    }
}
