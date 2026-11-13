using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// Each list's view (List, Undated, Today and Tomorrow), chosen with the caret beside the list's tab on the Tasks page:
/// Default (Focus's usual order), Ordered (your own drag order) or By project (project headings, No Project last).
/// Wherever a list shows (the Dashboard, the floating timer, the task window), it follows the choice made in Tasks.
/// Also the task window itself.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] ViewLists = { "list", "undated", "today", "tomorrow" };

    /// <summary>A list's view: "default", "ordered" or "project" (Today's older single setting carries over).</summary>
    private string ListMode(string list)
    {
        if (_settings.ListViews.TryGetValue(list, out var m) && m is "ordered" or "project" or "default") return m;
        return list == "today" && _settings.TodayView is "ordered" or "project" ? _settings.TodayView : "default";
    }

    private List<string> ListOrderOf(string list)
    {
        if (_settings.ListOrders.TryGetValue(list, out var o)) return o;
        return list == "today" ? _settings.TodayOrder : new List<string>();
    }

    private string TodayMode => ListMode("today");

    /// <summary>The Tasks tab that's showing, if it has views ("list", "undated", "today", "tomorrow"), else "".</summary>
    private string CurrentList =>
        BinTodoView?.IsChecked == true ? "list" : BinUndatedView?.IsChecked == true ? "undated" :
        BinTodayView?.IsChecked == true ? "today" : BinTomorrowView?.IsChecked == true ? "tomorrow" : "";

    private RadioButton? TabFor(string list) => list switch { "list" => BinTodoView, "undated" => BinUndatedView, "today" => BinTodayView, "tomorrow" => BinTomorrowView, _ => null };

    private static string ListName(string list) => list switch { "list" => "List", "undated" => "Undated", "today" => "Today", "tomorrow" => NextDay.Name, _ => "" };

    private void TodayViewBtn_Click(object sender, RoutedEventArgs e)
    {
        string list = CurrentList;
        if (list.Length == 0) return;
        var menu = new ContextMenu { PlacementTarget = TodayViewBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (key, name, note) in new[] { ("default", "Default", "Focus's usual order"), ("ordered", "Ordered", "Your own order: drag tasks into place"), ("project", "By project", "Under project headings") })
        {
            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
            var sub = new TextBlock { Text = note, FontSize = 12 }; sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            head.Children.Add(sub);
            var item = new MenuItem { Header = head, IsCheckable = true, IsChecked = ListMode(list) == key };
            item.Click += (_, _) => SetListView(list, key);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void SetListView(string list, string mode)
    {
        _settings.ListViews[list] = mode;
        if (list == "today") _settings.TodayView = mode; // older versions read this
        SaveSettingsQuietly();
        ApplyBinOrder();
        RefreshTasksTab();
        RefreshDashTasks();
        UpdateOverlayTasks();
        _taskWindow?.Refresh();
    }

    /// <summary>Each tab's label shows its view when it isn't Default ("List · By project"); the caret sits beside the open tab.</summary>
    private void PaintListTabs()
    {
        foreach (var list in ViewLists)
            if (TabFor(list) is RadioButton tab)
                tab.Content = ListMode(list) switch { "ordered" => $"{ListName(list)} · Ordered", "project" => $"{ListName(list)} · By project", _ => ListName(list) };
        // The caret goes right after the open tab (hidden for tabs without views).
        if (TodayViewBtn.Parent is Panel bar && TabFor(CurrentList) is RadioButton open)
        {
            bar.Children.Remove(TodayViewBtn);
            bar.Children.Insert(bar.Children.IndexOf(open) + 1, TodayViewBtn);
            TodayViewBtn.Visibility = Visibility.Visible;
            TodayViewBtn.ToolTip = $"How {ListName(CurrentList)} is listed: Default, Ordered or By project";
        }
        else TodayViewBtn.Visibility = Visibility.Collapsed;
    }

    /// <summary>Sorts and groups the Tasks page list for the open tab's view (Upcoming, Board and Resolved keep the usual order).</summary>
    private void ApplyTodayViewOrder(ListCollectionView? view, IList<BinTask> all, System.Collections.IComparer? usual)
    {
        if (view == null) return;
        string list = CurrentList, mode = list.Length > 0 ? ListMode(list) : "default";
        view.GroupDescriptions.Clear();
        if (mode == "ordered") view.CustomSort = new ListOrder(all, usual, ListOrderOf(list), project: false);
        else if (mode == "project")
        {
            view.CustomSort = new ListOrder(all, usual, ListOrderOf(list), project: true);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BinTask.ProjectHeading)));
            if (BinList.GroupStyle.Count == 0) BinList.GroupStyle.Add(BinGroupStyle());
        }
        else view.CustomSort = usual;
        PaintListTabs();
    }

    private static GroupStyle? _binGroupStyle;
    private static GroupStyle BinGroupStyle() => _binGroupStyle ??= new GroupStyle
    {
        HeaderTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<DockPanel Margin='4,14,0,6'>" +
            "<Ellipse Width='9' Height='9' Margin='0,0,8,0' VerticalAlignment='Center' Fill='{Binding Items[0].ProjectColor}'/>" +
            "<TextBlock Text='{Binding Name}' FontSize='12.5' FontWeight='SemiBold' Foreground='{DynamicResource Muted}' VerticalAlignment='Center' Margin='0,0,10,0'/>" +
            "<Rectangle Height='1' Fill='{DynamicResource Line}' VerticalAlignment='Center'/>" +
            "</DockPanel></DataTemplate>")
    };

    /// <summary>Ordered: your order first (new tasks after, in the usual order). By project: the project order (No Project last), then the usual order.</summary>
    private sealed class ListOrder : System.Collections.IComparer
    {
        private readonly IList<BinTask> _all; private readonly System.Collections.IComparer? _usual;
        private readonly Dictionary<string, int> _pos; private readonly bool _project; private readonly List<string> _projects;
        public ListOrder(IList<BinTask> all, System.Collections.IComparer? usual, List<string> order, bool project)
        {
            _all = all; _usual = usual; _project = project;
            _pos = order.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
            _projects = Projects.All.Select(p => p.Id).ToList();
        }
        public int Compare(object? x, object? y)
        {
            if (x is not BinTask a || y is not BinTask b) return 0;
            int c = _project ? Rank(a).CompareTo(Rank(b)) : Pos(a).CompareTo(Pos(b));
            if (c != 0) return c;
            return _usual?.Compare(x, y) ?? _all.IndexOf(a).CompareTo(_all.IndexOf(b));
        }
        private int Pos(BinTask t) => _pos.TryGetValue(t.Id, out int p) ? p : int.MaxValue;
        private int Rank(BinTask t) { int i = _projects.IndexOf(t.Project); return i < 0 ? int.MaxValue : i; }
    }

    /// <summary>Ordered: a task dropped on another moves just before it in that list's order.</summary>
    private bool TodayOrderDrop(List<BinTask> moving, BinTask target)
    {
        string list = CurrentList;
        if (list.Length == 0 || ListMode(list) != "ordered") return false;
        var ids = VisibleBinTasks().Select(t => t.Id).ToList();
        foreach (var t in moving) ids.Remove(t.Id);
        int at = ids.IndexOf(target.Id);
        ids.InsertRange(at < 0 ? ids.Count : at, moving.Select(t => t.Id));
        SetListOrder(list, ids);
        return true;
    }

    private void SetListOrder(string list, List<string> ids)
    {
        // Keep ids of tasks that are still around (a done or deleted task drops out of the order), plus the rest of the
        // old order for tasks not showing right now.
        var alive = TaskBin.All.Select(t => t.Id).ToHashSet();
        var merged = ids.Concat(ListOrderOf(list)).Where(alive.Contains).Distinct().ToList();
        _settings.ListOrders[list] = merged;
        if (list == "today") _settings.TodayOrder = merged;
        SaveSettingsQuietly();
        ApplyBinOrder();
        RefreshDashTasks();
        UpdateOverlayTasks();
        _taskWindow?.Refresh();
    }

    /// <summary>A list's tasks in its view.</summary>
    private List<BinTask> InView(string list, IEnumerable<BinTask> tasks) => TaskGroups.Order(tasks, ListMode(list), ListOrderOf(list));
    private List<BinTask> TodayInView(IEnumerable<BinTask> today) => InView("today", today);

    private List<OverlayTask> AsRows(IEnumerable<BinTask> tasks, bool groups) => tasks.Select(t => new OverlayTask
    {
        Source = t, Kind = "main", Id = t.Id, Key = t.Id, Text = t.Text, IsHigh = t.IsHigh,
        Group = groups ? TaskGroups.Heading(t) : "", GroupColor = t.HasProject ? t.ProjectColor : "#7A8886"
    }).ToList();

    private List<OverlayTask> ListRows(string list, IEnumerable<BinTask> tasks) => AsRows(InView(list, tasks), groups: ListMode(list) == "project");

    // ---------- The task window ----------

    private TaskWindow? _taskWindow;

    private void TaskWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_taskWindow != null)
        {
            if (_taskWindow.WindowState == WindowState.Minimized) _taskWindow.WindowState = WindowState.Normal;
            _taskWindow.Activate();
            return;
        }
        _taskWindow = new TaskWindow(_settings, SaveSettingsQuietly, TaskWindowRows, t => OverlayTaskToggled(t), SetListOrder, ListMode, TodayProgress);
        _taskWindow.Closed += (_, _) => _taskWindow = null;
        _taskWindow.Show();
        ShowOverlayIdleOrRunning();
    }

    /// <summary>What the task window lists: this session's tasks, or Today, Tomorrow, List or Undated in the view set in Tasks.</summary>
    private List<OverlayTask> TaskWindowRows(string view)
    {
        using var onWork = TaskStores.Use(TaskStores.Main);
        try
        {
            bool snoozedHidden = _settings.On("upcoming");
            var usual = TaskBin.All.OrderBy(TaskBase.Rank).ThenBy(t => t.Due ?? DateTime.MaxValue).ThenBy(t => TaskBin.All.IndexOf(t)).ToList();
            switch (view)
            {
                case "session":
                    var cur = _runner.Current is PlanBlock b && b.IsFocus ? b : _runner.Blocks.Skip(_runner.Index + 1).FirstOrDefault(x => x.IsFocus);
                    return cur == null ? new() : cur.Tasks.Where(t => t.Text.Trim().Length > 0)
                        .Select(t => new OverlayTask { Source = t, Kind = "session", Id = t.Id, Key = t.Id, Text = t.Text, IsHigh = t.IsHigh, Done = t.Done }).ToList();
                case "list":
                case "all":
                    return ListRows("list", usual.Where(t => !(t.IsSnoozed && snoozedHidden)));
                case "undated":
                    return ListRows("undated", usual.Where(t => t.IsUndated && !(t.IsSnoozed && snoozedHidden)));
                case "tomorrow":
                    return ListRows("tomorrow", usual.Where(IsForTomorrow));
                default:
                    return TodayOverlayTasks();
            }
        }
        catch (Exception ex) { Log.Error("Task window rows", ex); return new(); }
    }

    /// <summary>Today's progress for the task window: tasks done today of today's tasks, and focus time left in the plan.</summary>
    private (int Done, int Total, string Left) TodayProgress()
    {
        try
        {
            using var onWork = TaskStores.Use(TaskStores.Main);
            int open = TodayOverlayTasks().Count;
            int done = ResolvedTasks.All.Count(r => r.ResolvedAt.Date == DateTime.Today);
            string left = "";
            if (_runner.IsRunning)
            {
                var rest = _runner.Blocks.Skip(_runner.Index + 1).Where(x => x.IsFocus).Sum(x => x.Minutes);
                var now = _runner.Current is PlanBlock c && c.IsFocus ? _runner.Remaining.TotalMinutes : 0;
                var total = TimeSpan.FromMinutes(rest + now);
                left = total.TotalMinutes < 1 ? "" : total.TotalHours >= 1 ? $"{(int)total.TotalHours} h {total.Minutes:00} left" : $"{total.Minutes} min left";
            }
            return (done, done + open, left);
        }
        catch (Exception ex) { Log.Error("Task window progress", ex); return (0, 0, ""); }
    }

    private void ShowOverlayIdleOrRunning()
    {
        if (_taskWindow == null) return;
        if (!_runner.IsRunning) _taskWindow.UpdateTimer("Ready", "Nothing running", (Brush)FindResource("Muted"), 0, 0);
    }
}
