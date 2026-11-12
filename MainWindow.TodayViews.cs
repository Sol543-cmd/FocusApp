using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// Today's three views, chosen with the caret beside Today on the Tasks page: Default (Focus's usual order), Ordered (your own
/// drag order) and By project (project headings). The Dashboard's task card, the floating timer's drop-down and the task window
/// follow the same choice. Also the task window itself.
/// </summary>
public partial class MainWindow
{
    private string TodayMode => _settings.TodayView is "ordered" or "project" ? _settings.TodayView : "default";

    private void TodayViewBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TodayViewBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (key, name, note) in new[] { ("default", "Default", "Focus's usual order"), ("ordered", "Ordered", "Your own order: drag tasks into place"), ("project", "By project", "Under project headings") })
        {
            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
            var sub = new TextBlock { Text = note, FontSize = 12 }; sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            head.Children.Add(sub);
            var item = new MenuItem { Header = head, IsCheckable = true, IsChecked = TodayMode == key };
            item.Click += (_, _) => SetTodayView(key);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void SetTodayView(string mode)
    {
        _settings.TodayView = mode;
        SaveSettingsQuietly();
        if (BinTodayView.IsChecked != true) BinTodayView.IsChecked = true; // show it
        ApplyBinOrder();
        RefreshTasksTab();
        RefreshDashTasks();
        UpdateOverlayTasks();
        _taskWindow?.Refresh();
    }

    /// <summary>The Today tab's label shows the view when it isn't Default ("Today · By project").</summary>
    private void PaintTodayTab() =>
        BinTodayView.Content = TodayMode switch { "ordered" => "Today · Ordered", "project" => "Today · By project", _ => "Today" };

    /// <summary>Sorts and groups the Tasks page list for Today's view (other tabs keep their usual order).</summary>
    private void ApplyTodayViewOrder(ListCollectionView? view, IList<BinTask> all, System.Collections.IComparer? usual)
    {
        if (view == null) return;
        bool today = BinTodayView?.IsChecked == true;
        view.GroupDescriptions.Clear();
        if (today && TodayMode == "ordered") view.CustomSort = new ListOrder(all, usual, _settings.TodayOrder, project: false);
        else if (today && TodayMode == "project")
        {
            view.CustomSort = new ListOrder(all, usual, _settings.TodayOrder, project: true);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BinTask.ProjectHeading)));
            if (BinList.GroupStyle.Count == 0) BinList.GroupStyle.Add(BinGroupStyle());
        }
        else view.CustomSort = usual;
        PaintTodayTab();
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

    /// <summary>Ordered: your order first (new tasks after, in the usual order). By project: the project order, then the usual order.</summary>
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

    /// <summary>Today, Ordered: a task dropped on another moves just before it in your order.</summary>
    private bool TodayOrderDrop(List<BinTask> moving, BinTask target)
    {
        if (BinTodayView.IsChecked != true || TodayMode != "ordered") return false;
        var ids = VisibleBinTasks().Select(t => t.Id).ToList();
        foreach (var t in moving) ids.Remove(t.Id);
        int at = ids.IndexOf(target.Id);
        ids.InsertRange(at < 0 ? ids.Count : at, moving.Select(t => t.Id));
        SetTodayOrder(ids);
        return true;
    }

    private void SetTodayOrder(List<string> ids)
    {
        // Keep ids of tasks that are still around (a done or deleted task drops out of the order).
        var alive = TaskBin.All.Select(t => t.Id).ToHashSet();
        _settings.TodayOrder = ids.Where(alive.Contains).Distinct().ToList();
        SaveSettingsQuietly();
        ApplyBinOrder();
        RefreshDashTasks();
        UpdateOverlayTasks();
        _taskWindow?.Refresh();
    }

    /// <summary>Today's tasks in today's view, for the Dashboard, the floating timer and the task window.</summary>
    private List<BinTask> TodayInView(IEnumerable<BinTask> today) => TaskGroups.Order(today, TodayMode, _settings.TodayOrder);

    private List<OverlayTask> AsRows(IEnumerable<BinTask> tasks, bool groups) => tasks.Select(t => new OverlayTask
    {
        Source = t, Kind = "main", Id = t.Id, Key = t.Id, Text = t.Text, IsHigh = t.IsHigh,
        Group = groups ? TaskGroups.Heading(t) : "", GroupColor = t.HasProject ? t.ProjectColor : "#7A8886"
    }).ToList();

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
        _taskWindow = new TaskWindow(_settings, SaveSettingsQuietly, TaskWindowRows, t => OverlayTaskToggled(t), SetTodayOrder, TodayProgress);
        _taskWindow.Closed += (_, _) => _taskWindow = null;
        _taskWindow.Show();
        ShowOverlayIdleOrRunning();
    }

    /// <summary>What the task window lists: this session's tasks, today's (in today's view), all, or undated.</summary>
    private List<OverlayTask> TaskWindowRows(string view)
    {
        using var onWork = TaskStores.Use(TaskStores.Main);
        try
        {
            switch (view)
            {
                case "session":
                    var cur = _runner.Current is PlanBlock b && b.IsFocus ? b : _runner.Blocks.Skip(_runner.Index + 1).FirstOrDefault(x => x.IsFocus);
                    return cur == null ? new() : cur.Tasks.Where(t => t.Text.Trim().Length > 0)
                        .Select(t => new OverlayTask { Source = t, Kind = "session", Id = t.Id, Key = t.Id, Text = t.Text, IsHigh = t.IsHigh, Done = t.Done }).ToList();
                case "all":
                    return AsRows(TaskBin.All.Where(t => !t.IsSnoozed).OrderBy(TaskBase.Rank).ThenBy(t => t.Due ?? DateTime.MaxValue), groups: false);
                case "undated":
                    return AsRows(TaskBin.All.Where(t => t.IsUndated).OrderBy(TaskBase.Rank), groups: false);
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
