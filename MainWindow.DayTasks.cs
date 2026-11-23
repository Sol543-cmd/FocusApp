using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FocusApp;

/// <summary>
/// The Calendar's tasks chip: each day's tasks (picked for it or due that day; today also takes overdue ones) show as
/// "8 pending tasks" in the all-day strip. Clicking it opens a small window of that day's tasks by project, where you
/// can tick them off. Meetings aren't listed (they're on the calendar). Click anywhere else to close it.
/// </summary>
public partial class MainWindow
{
    private Popup? _dayTasksPopup;

    /// <summary>Your work tasks for a day (not meetings, not snoozed past it).</summary>
    private static List<BinTask> TasksOnDay(DateTime day)
    {
        day = day.Date;
        return TaskStores.Main.Bin.Where(t => !t.IsMeetingTask &&
                (t.PlannedDate == day || t.Due?.Date == day || (day == DateTime.Today && t.IsOverdue)) &&
                !(t.SnoozedUntil is DateTime s && s.Date > day))
            .ToList();
    }

    private void SetUpDayTasksChips()
    {
        CalWeek.TaskCountFor = d => TasksOnDay(d).Count;
        CalDay.TaskCountFor = d => TasksOnDay(d).Count;
        CalWeek.TasksClicked += ShowDayTasks;
        CalDay.TasksClicked += ShowDayTasks;
    }

    private void ShowDayTasks(DateTime day, FrameworkElement target)
    {
        if (_dayTasksPopup != null) _dayTasksPopup.IsOpen = false;
        var list = new StackPanel();
        var card = new Border
        {
            Width = 400, MaxHeight = 480, CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 14, 14, 14), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.3 },
            Margin = new Thickness(10, 4, 10, 14),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list }
        };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        var popup = new Popup
        {
            Child = card, PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };
        _dayTasksPopup = popup;
        FillDayTasks(list, day, popup);
        popup.IsOpen = true;
        popup.KeyDown += (_, e) => { if (e.Key == Key.Escape) popup.IsOpen = false; };
    }

    private void FillDayTasks(StackPanel list, DateTime day, Popup popup)
    {
        list.Children.Clear();
        var tasks = TasksOnDay(day);
        var head = new TextBlock { Text = (day.Date == DateTime.Today ? "Today, " : "") + day.ToString("dddd, MMMM d"), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        list.Children.Add(head);
        var sub = new TextBlock { Text = tasks.Count == 0 ? "No tasks left for this day." : tasks.Count == 1 ? "1 pending task" : $"{tasks.Count} pending tasks", FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        list.Children.Add(sub);

        using (TaskStores.Use(TaskStores.Main))
        {
            // Project order as in your project list, No Project last.
            var order = Projects.All.Select((p, i) => (p.Name, i)).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().i);
            foreach (var group in tasks.GroupBy(t => t.ProjectHeading)
                                       .OrderBy(g => g.Key == "No Project" ? int.MaxValue : order.TryGetValue(g.Key, out var i) ? i : int.MaxValue - 1))
            {
                list.Children.Add(GroupHeader(group.Key, group.First().ProjectColor));
                foreach (var task in group.OrderByDescending(t => t.IsHigh).ThenBy(t => t.Due ?? DateTime.MaxValue))
                    list.Children.Add(DayTaskRow(task, day, list, popup));
            }
        }
    }

    private static FrameworkElement GroupHeader(string name, string color)
    {
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 4) };
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        try { dot.Fill = (Brush)new BrushConverter().ConvertFromString(color == "Transparent" ? "#9AA0A6" : color)!; } catch { dot.Fill = Brushes.Gray; }
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        var text = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        row.Children.Add(line);
        return row;
    }

    /// <summary>One task: a circle to tick it off (with Undo), then its text.</summary>
    private FrameworkElement DayTaskRow(BinTask task, DateTime day, StackPanel list, Popup popup)
    {
        var circle = new Ellipse { Width = 19, Height = 19, StrokeThickness = 1.6, Fill = Brushes.Transparent };
        circle.SetResourceReference(Shape.StrokeProperty, "Muted");
        var tick = new Path { Data = Geometry.Parse("M5.5,10 L8.5,13 L14,7"), StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Hidden };
        tick.SetResourceReference(Shape.StrokeProperty, "Muted");
        var check = new Grid { Width = 19, Height = 19, Margin = new Thickness(0, 1, 12, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, ToolTip = "Finish this task" };
        check.Children.Add(circle); check.Children.Add(tick);
        check.MouseEnter += (_, _) => tick.Visibility = Visibility.Visible;
        check.MouseLeave += (_, _) => tick.Visibility = Visibility.Hidden;
        check.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            using (TaskStores.Use(TaskStores.Main))
            {
                if (!TaskBin.All.Contains(task)) return;
                PushUndo(includePlan: true);
                FinishListTask(task, task.From, task.Spent);
            }
            _toast.ShowMessage("Task finished", task.Text, "Undo", () => { Undo(); RefreshCalendarTab(); });
            FillDayTasks(list, day, popup);
            RefreshTasksTab();
            RefreshCalendarTab();
            if (TasksOnDay(day).Count == 0) popup.IsOpen = false;
        };
        var text = new TextBlock { Text = task.Text, FontSize = 14.5, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var stack = new StackPanel();
        stack.Children.Add(text);
        string note = task.IsOverdue ? $"Overdue, due {task.Due:ddd MMM d}" : task.Due?.Date == day.Date && task.PlannedDate != day.Date ? "Due this day" : "";
        if (note.Length > 0)
        {
            var small = new TextBlock { Text = note, FontSize = 11.5 };
            small.SetResourceReference(TextBlock.ForegroundProperty, task.IsOverdue ? "PriorityHigh" : "Muted");
            stack.Children.Add(small);
        }
        var row = new DockPanel { Margin = new Thickness(2, 5, 0, 5) };
        DockPanel.SetDock(check, Dock.Left);
        row.Children.Add(check);
        row.Children.Add(stack);
        return row;
    }
}
