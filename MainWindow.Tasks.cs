using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace FocusApp;

/// <summary>Tasks on focus sessions, the dashboard's task list, and your task list.</summary>
public partial class MainWindow
{
    private PlanBlock? _previousRunBlock;
    private PlanBlock? _binTarget;
    private List<ListChoice> _binChoices = new();

    private void UpdateBinEmptyText()
    {
        BinEmptyText.Visibility = TaskBin.All.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BinCountText.Text = TaskBin.All.Count.ToString();
    }

    /// <summary>Resolved tasks for one day, for the Resolved list.</summary>
    private sealed class ResolvedDay
    {
        public string Header { get; init; } = "";
        public List<ResolvedTask> Items { get; init; } = new();
    }

    private void BinView_Checked(object sender, RoutedEventArgs e)
    {
        if (BinDoneView == null || DoneScroll == null || BoardGrid == null) return;
        bool todo = BinTodoView.IsChecked == true || BinUndatedView.IsChecked == true || BinTomorrowView.IsChecked == true, upcoming = BinUpcomingView.IsChecked == true,
             board = BinBoardView.IsChecked == true, done = BinDoneView.IsChecked == true;
        _settings.LastBinView = BinUndatedView.IsChecked == true ? "undated" : BinTomorrowView.IsChecked == true ? "tomorrow" : upcoming ? "upcoming" : board ? "board" : done ? "done" : "list";
        SaveSettingsQuietly();
        ClearSelection();
        BinTodoHeader.Visibility = todo ? Visibility.Visible : Visibility.Collapsed;
        BinTodoScroll.Visibility = todo ? Visibility.Visible : Visibility.Collapsed;
        UpcomingScroll.Visibility = upcoming ? Visibility.Visible : Visibility.Collapsed;
        BoardGrid.Visibility = board ? Visibility.Visible : Visibility.Collapsed;
        DoneScroll.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        RefreshTasksTab();
    }

    private void DeleteDoneTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResolvedTask task) return;
        ResolvedTasks.All.Remove(task);
        ResolvedTasks.Save();
    }

    private static bool IsEnter(KeyEventArgs e) => e.Key == Key.Enter;

    // ---------- Tasks in the session editor ----------

    private void RemoveTask_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || (sender as FrameworkElement)?.DataContext is not TaskItem task) return;
        SetTaskAside(_selected, task);
    }

    private void BinPickerDone_Click(object sender, RoutedEventArgs e) => BinPickerPopup.IsOpen = false;

    // ---------- Tasks on the dashboard ----------

    /// <summary>Shows the running focus session's tasks under the ring timer.</summary>
    /// <summary>
    /// The dashboard's task card: this session's tasks (the default), or your task list for today, or all of it.
    /// </summary>
    private void RefreshDashTasks()
    {
        if (DashTasksSession == null || DashListTasks == null) return;
        var cur = _runner.Current;
        bool inFocus = cur != null && cur.IsFocus;
        bool session = DashTasksSession.IsChecked == true;

        // The session view needs a focus session; the list views work any time.
        DashTasksPanel.Visibility = Visibility.Visible;
        DashTasksList.Visibility = session ? Visibility.Visible : Visibility.Collapsed;
        DashListTasks.Visibility = session ? Visibility.Collapsed : Visibility.Visible;
        DashFromBinBtn.Visibility = session && inFocus && _settings.On("dashTaskEditing") ? Visibility.Visible : Visibility.Collapsed;

        if (session)
        {
            DashTasksTitle.Text = inFocus ? "Tasks for this session" : "Tasks";
            if (inFocus && cur != null)
            {
                if (!ReferenceEquals(DashTasksList.ItemsSource, cur.Tasks)) DashTasksList.ItemsSource = cur.Tasks;
                DashNoTasks.Text = "No tasks for this session yet. Add one below, or switch to Today or All and use + Session.";
                DashNoTasks.Visibility = cur.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                DashTasksList.ItemsSource = null;
                DashNoTasks.Text = "Session tasks show here during a focus session. Switch to Today or All to see your list.";
                DashNoTasks.Visibility = Visibility.Visible;
            }
            return;
        }

        bool todayOnly = DashTasksToday.IsChecked == true, undatedOnly = DashTasksUndated.IsChecked == true;
        DashTasksTitle.Text = todayOnly ? "Today's tasks" : undatedOnly ? "Undated tasks" : "All tasks";
        var (blocks, _) = TodaysSchedule();
        var placed = new HashSet<string>(blocks.SelectMany(b => b.Tasks).Select(t => t.BinId).Where(id => id.Length > 0));
        var tasks = TaskBin.All
            .Where(t => !(t.IsSnoozed && _settings.On("upcoming")))
            .Where(t => !todayOnly || t.IsPlannedToday || t.IsDueToday || t.IsOverdue || placed.Contains(t.Id))
            .Where(t => !undatedOnly || t.IsUndated)
            .OrderBy(TaskBase.Rank).ThenBy(t => t.Due ?? DateTime.MaxValue).ThenBy(t => TaskBin.All.IndexOf(t))
            .ToList();
        SetItemsIfChanged(DashListTasks, tasks);
        DashNoTasks.Text = todayOnly ? "Nothing set for today. Switch to All, or add one below."
            : undatedOnly ? "Every task has a date. Nice." : "No tasks. Add one below.";
        DashNoTasks.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DashTasksView_Checked(object sender, RoutedEventArgs e) => RefreshDashTasks();

    /// <summary>"+ Session" on a list task: adds it to the focus session you're in.</summary>
    private void DashAddToSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BinTask task) return;
        if (_runner.Current is not PlanBlock cur || !cur.IsFocus)
        {
            _toast.ShowMessage("No focus session running", "Start a session first, then add tasks to it.");
            return;
        }
        if (cur.Tasks.Any(t => t.BinId == task.Id))
        {
            _toast.ShowMessage("Already in this session", task.Text);
            return;
        }
        PushUndo(includePlan: true);
        cur.Tasks.Add(LinkedCopy(task));
        SaveRunState();
        RefreshPlanTasks();
        _toast.ShowMessage("Added to this session", task.Text);
    }

    private void RemoveDashTask_Click(object sender, RoutedEventArgs e)
    {
        var cur = _runner.Current;
        if (cur == null || (sender as FrameworkElement)?.DataContext is not TaskItem task) return;
        SetTaskAside(cur, task);
        RefreshDashTasks();
        SaveRunState();
    }

    // ---------- Unfinished tasks go to the bin ----------

    // ---------- The Task list tab ----------

    private void DeleteBinTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BinTask task) return;
        PushUndo(includePlan: true);
        TaskBin.All.Remove(task);
        TaskBin.Save();
        RemoveLinkedCopies(task.Id);
        _toast.ShowMessage("Task deleted", task.Text, "Undo", Undo);
    }

    // ---------- Dashboard divider ----------

    private void DashSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _settings.DashRightWidth = DashRightColumn.ActualWidth;
        SaveSettingsQuietly();
    }

    // ---------- Printing ----------

    /// <summary>Prints the To do list (with boxes to tick by hand), or the Resolved list, whichever is showing.</summary>
    private void PrintTasks_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;

        bool resolved = BinDoneView.IsChecked == true;
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(60),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13
        };
        doc.Blocks.Add(new Paragraph(new Run(resolved ? "Resolved tasks" : "Tasks to do"))
        {
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        doc.Blocks.Add(new Paragraph(new Run($"Printed {DateTime.Now:dddd, MMMM d, yyyy}")) { Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 18) });

        if (!resolved)
        {
            if (TaskBin.All.Count == 0) doc.Blocks.Add(new Paragraph(new Run("Your task list is empty.")));
            foreach (var t in TaskBin.All)
                doc.Blocks.Add(new Paragraph(new Run("☐  " + t.Text)) { FontSize = 14, Margin = new Thickness(0, 0, 0, 8) });
        }
        else
        {
            if (ResolvedTasks.All.Count == 0) doc.Blocks.Add(new Paragraph(new Run("Nothing resolved yet.")));
            foreach (var day in ResolvedTasks.All.GroupBy(t => t.ResolvedAt.Date).OrderByDescending(g => g.Key))
            {
                doc.Blocks.Add(new Paragraph(new Run(day.Key.ToString("dddd, MMMM d, yyyy")))
                {
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 12, 0, 6)
                });
                foreach (var t in day.OrderBy(t => t.ResolvedAt))
                    doc.Blocks.Add(new Paragraph(new Run("☑  " + t.Text)) { Margin = new Thickness(0, 0, 0, 6) });
            }
        }

        try
        {
            doc.PageWidth = dialog.PrintableAreaWidth;
            doc.PageHeight = dialog.PrintableAreaHeight;
            dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, "Focus tasks");
        }
        catch (Exception ex)
        {
            Log.Error("PrintTasks", ex);
            Info("Couldn't print: " + ex.Message, "Print");
        }
    }

    // ---------- Setting up ----------

    private string _binFilter = "all";
    // One live view of each list (Work and Personal); the page shows the one for its button.
    private ListCollectionView? _binViewMain, _binViewPersonal;
    private ListCollectionView? _binView => ReferenceEquals(TasksPageSet, TaskStores.Personal) ? _binViewPersonal : _binViewMain;

    private void SetUpTasks()
    {
        TaskBin.Load();
        ResolvedTasks.Load();
        HookBin();
        ResolvedTasks.Changed += RefreshTasksTab;
        RefreshTasksTab();
    }

    /// <summary>Connects the To do list to the bin, sorted and filtered.</summary>
    private void HookBin()
    {
        _binViewMain = new ListCollectionView(TaskStores.Main.Bin) { Filter = o => o is BinTask t && MatchesFilter(t) };
        _binViewPersonal = new ListCollectionView(TaskStores.Personal.Bin) { Filter = o => o is BinTask t && MatchesFilter(t) };
        ApplyBinOrder();
        BinList.ItemsSource = _binView;
        TaskBin.All.CollectionChanged += (_, _) => RefreshTasksTab();
    }

    private void ApplyBinOrder()
    {
        if (_binViewMain != null) _binViewMain.CustomSort = SmartOrderCheck.IsChecked == true ? new SmartTaskOrder(TaskStores.Main.Bin) : null;
        if (_binViewPersonal != null) _binViewPersonal.CustomSort = SmartOrderCheck.IsChecked == true ? new SmartTaskOrder(TaskStores.Personal.Bin) : null;
    }

    /// <summary>Overdue, due today, picked for today, high priority, by due date, then the bin's own order.</summary>
    private sealed class SmartTaskOrder : System.Collections.IComparer
    {
        private readonly IList<BinTask> _all;
        public SmartTaskOrder(IList<BinTask> all) => _all = all;

        public int Compare(object? x, object? y)
        {
            if (x is not BinTask a || y is not BinTask b) return 0;
            int c = TaskBase.Rank(a).CompareTo(TaskBase.Rank(b));
            if (c != 0) return c;
            c = (a.Due ?? DateTime.MaxValue).CompareTo(b.Due ?? DateTime.MaxValue);
            return c != 0 ? c : _all.IndexOf(a).CompareTo(_all.IndexOf(b));
        }
    }

    private bool MatchesFilter(BinTask t) =>
        MatchesSearch(t) && !(t.IsSnoozed && _settings.On("upcoming") && BinTomorrowView?.IsChecked != true) && MatchesFilterButton(t) &&
        (BinUndatedView?.IsChecked != true || t.IsUndated) &&
        (BinTomorrowView?.IsChecked != true || IsForTomorrow(t));

    /// <summary>For the Tomorrow tab: picked for tomorrow, due tomorrow, or snoozed until tomorrow.</summary>
    private static bool IsForTomorrow(BinTask t)
    {
        var tomorrow = DateTime.Today.AddDays(1);
        return t.PlannedDate == tomorrow || t.Due?.Date == tomorrow || t.SnoozedUntil?.Date == tomorrow;
    }

    private bool MatchesFilterButton(BinTask t) => _binFilter switch
    {
        "all" => true,
        "today" => t.IsPlannedToday || t.IsDueToday || t.IsOverdue,
        "overdue" => t.IsOverdue,
        "high" => t.IsHigh,
        "nodate" => t.Due == null,
        "age7" => t.AgeDays >= 7,
        "age10" => t.AgeDays >= 10,
        _ when _binFilter.StartsWith("project:") => t.Project == _binFilter[8..],
        _ => _binFilter.StartsWith("#") && t.Tags.Any(tag => string.Equals(tag, _binFilter[1..], StringComparison.CurrentCultureIgnoreCase))
    };

    private void SmartOrder_Changed(object sender, RoutedEventArgs e)
    {
        if (_binView == null) return;
        ApplyBinOrder();
    }

    /// <summary>Updates the counts, filters, today's capacity, and the Resolved list.</summary>
    private void RefreshTasksTab()
    {
        using var pageSet = TaskStores.Use(TasksPageSet); // the set this page shows (Work, or Personal from its own button)
        if (BinCountText == null) return;
        UpdateBinEmptyText();
        int sinceMonday = ((int)DateTime.Today.DayOfWeek + 6) % 7;
        ResolvedTodayText.Text = ResolvedTasks.CountSince(DateTime.Today).ToString();
        ResolvedWeekText.Text = ResolvedTasks.CountSince(DateTime.Today.AddDays(-sinceMonday)).ToString();
        ResolvedAllText.Text = ResolvedTasks.All.Count.ToString();

        DoneList.ItemsSource = ResolvedTasks.All
            .Where(t => _search.Length == 0 || t.Text.Contains(_search, StringComparison.CurrentCultureIgnoreCase))
            .GroupBy(t => t.ResolvedAt.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new ResolvedDay
            {
                Header = g.Key == DateTime.Today ? "Today" : g.Key == DateTime.Today.AddDays(-1) ? "Yesterday" : g.Key.ToString("dddd, MMMM d, yyyy"),
                Items = g.OrderByDescending(t => t.ResolvedAt).ToList()
            })
            .ToList();
        DoneEmptyText.Visibility = BinDoneView.IsChecked == true && ResolvedTasks.All.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        BuildBinFilters();
        _binView?.Refresh();
        UpdateTodayCapacity();
        if (BinUpcomingView.IsChecked == true) RefreshUpcoming();
        if (BinBoardView.IsChecked == true) RefreshBoard();
        RefreshPlanTasks();
        RefreshHome();
        if (DashTasksSession != null && DashTasksSession.IsChecked != true) RefreshDashTasks();
    }

    /// <summary>The filter buttons: All, Today, Overdue, High priority, No due date, and one per tag.</summary>
    private void BuildBinFilters()
    {
        var filters = new List<(string Key, string Label)>
        {
            ("all", $"All ({TaskBin.All.Count})"),
            ("today", $"Today ({TaskBin.All.Count(t => t.IsPlannedToday || t.IsDueToday || t.IsOverdue)})"),
            ("overdue", $"Overdue ({TaskBin.All.Count(t => t.IsOverdue)})"),
            ("high", $"High priority ({TaskBin.All.Count(t => t.IsHigh)})"),
            ("nodate", "No due date")
        };
        if (_settings.On("ageFilters"))
        {
            filters.Add(("age7", $"+ one week old ({TaskBin.All.Count(t => t.AgeDays >= 7)})"));
            filters.Add(("age10", $"+ ten days old ({TaskBin.All.Count(t => t.AgeDays >= 10)})"));
        }
        if (_settings.On("projects"))
            foreach (var p in Projects.All)
                filters.Add(("project:" + p.Id, $"● {p.Name} ({TaskBin.All.Count(t => t.Project == p.Id)})"));
        foreach (var tag in TaskBin.All.SelectMany(t => t.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(t => t))
            filters.Add(("#" + tag, $"#{tag} ({TaskBin.All.Count(t => t.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase))})"));
        var same = filters.FirstOrDefault(f => string.Equals(f.Key, _binFilter, StringComparison.CurrentCultureIgnoreCase));
        if (same.Key != null) _binFilter = same.Key;
        else if (_binFilter.StartsWith("#")) filters.Add((_binFilter, $"{_binFilter} (0)"));
        else if (_binFilter.StartsWith("project:") && Projects.Find(_binFilter[8..]) is Project gone) filters.Add((_binFilter, $"● {gone.Name} (0)"));
        else if (_binFilter is "age7" or "age10" && _settings.On("ageFilters")) { }
        else _binFilter = "all";
        if (_settings.LastBinFilter != _binFilter) { _settings.LastBinFilter = _binFilter; SaveSettingsQuietly(); }

        // Views, projects and tags each get their own section, as simple rows with counts on the right.
        BinFilters.Children.Clear();
        ProjectFilters.Children.Clear();
        TagFilters.Children.Clear();
        foreach (var (key, label) in filters)
        {
            // Labels look like "Overdue (3)": the count goes on the right.
            string text = label, count = "";
            int open = label.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0 && label.EndsWith(")")) { text = label[..open]; count = label[(open + 2)..^1]; }
            if (key.StartsWith("project:"))
            {
                var project = Projects.Find(key[8..]);
                ProjectFilters.Children.Add(FilterRow(key, project?.Name ?? text.TrimStart('●', ' '), count, project?.Color, project));
            }
            else if (key.StartsWith("#")) TagFilters.Children.Add(FilterRow(key, "@" + key[1..], count, null, null));
            else BinFilters.Children.Add(FilterRow(key, text, count, null, null));
        }
        NoProjectsHint.Visibility = ProjectFilters.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TagsSection.Visibility = TagFilters.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>One clickable row in the sidebar: a name, an optional color dot, and a count. Projects get ⋯ to edit.</summary>
    private FrameworkElement FilterRow(string key, string text, string count, string? color, Project? project)
    {
        bool selected = key == _binFilter;
        var dock = new DockPanel();
        var countText = new TextBlock { Text = count, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        countText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        if (project != null)
        {
            var more = new Button { Content = "⋯", FontSize = 13, Padding = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                                    ToolTip = "Edit project: name, color, shortcuts, sections", DataContext = project };
            more.SetResourceReference(StyleProperty, "IconButton");
            more.Click += (s, e) => { e.Handled = true; ProjectRow_Click(s, null!); };
            DockPanel.SetDock(more, Dock.Right);
            dock.Children.Add(more);
        }
        DockPanel.SetDock(countText, Dock.Right);
        dock.Children.Add(countText);
        if (color != null)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            try { dot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!; } catch { }
            dock.Children.Add(dot);
        }
        var name = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13.5 };
        if (selected) { name.FontWeight = FontWeights.SemiBold; name.SetResourceReference(TextBlock.ForegroundProperty, "FocusBrush"); }
        dock.Children.Add(name);
        var row = new Border { Child = dock, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 4, 5), Margin = new Thickness(0, 0, 0, 1),
                               Cursor = Cursors.Hand, Background = Brushes.Transparent, DataContext = project };
        if (selected) row.SetResourceReference(Border.BackgroundProperty, "NavSelected");
        row.MouseEnter += (_, _) => { if (key != _binFilter) row.SetResourceReference(Border.BackgroundProperty, "Panel2"); };
        row.MouseLeave += (_, _) => { if (key != _binFilter) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, _) =>
        {
            // Clicking the selected project or tag again goes back to All.
            _binFilter = key == _binFilter && key != "all" ? "all" : key;
            if (BinTodoView.IsChecked != true) BinTodoView.IsChecked = true;
            RefreshTasksTab();
        };
        if (project != null)
        {
            row.AllowDrop = true;
            row.Drop += ProjectRow_Drop; // drop tasks on a project to move them there
            row.ToolTip = "Show only this project's tasks. Drop tasks here to move them.";
        }
        return row;
    }

    // ---------- Today's tasks and capacity ----------

    /// <summary>Tasks that belong to today: picked with ☀, due today, or overdue.</summary>
    private static List<BinTask> TodaysBinTasks() =>
        TaskBin.All.Where(t => (t.IsPlannedToday || t.IsDueToday || t.IsOverdue) && !t.IsSnoozed).ToList();

    /// <summary>A task's size in sessions for planning (unknown counts as one session).</summary>
    private static double Size(TaskBase t) => t.Estimate <= 0 ? 1 : t.Estimate;

    /// <summary>Today's focus sessions still ahead (or all of them, before the day starts).</summary>
    private List<PlanBlock> RemainingFocusToday(out List<PlanBlock> plan)
    {
        if (_mode == RunMode.DayPlan && _runner.IsRunning)
        {
            plan = _runner.Blocks.ToList();
            int ci = _runner.Index;
            return plan.Where((b, i) => i > ci && b.IsFocus).ToList();
        }
        var (blocks, start) = PlanFor(DateTime.Today);
        plan = blocks.ToList();
        var now = DateTime.Now.TimeOfDay;
        var result = new List<PlanBlock>();
        var t = start;
        foreach (var b in plan)
        {
            if (b.IsFocus && t >= now - TimeSpan.FromMinutes(5)) result.Add(b);
            t += TimeSpan.FromMinutes(b.Minutes);
        }
        return result;
    }

    private void UpdateTodayCapacity()
    {
        try
        {
            var sessions = RemainingFocusToday(out _);
            double planned = sessions.Sum(s => s.Tasks.Where(t => !t.Done).Sum(Size));
            var placed = new HashSet<string>(sessions.SelectMany(s => s.Tasks).Select(t => t.BinId).Where(id => id.Length > 0));
            double waiting = TodaysBinTasks().Where(t => !placed.Contains(t.Id)).Sum(Size);
            double needed = planned + waiting;
            string need = needed < 1 && needed > 0 ? "under 1" : $"about {Math.Ceiling(needed):0}";
            TodayCapacityText.Text = needed == 0
                ? $"Nothing picked for today yet. {sessions.Count} focus sessions left today. Pick tasks with ☀."
                : $"Today's tasks need {need} focus sessions; {sessions.Count} are left today." +
                  (needed > sessions.Count ? " That's more than fits: consider moving some to another day." : "");
            TodayCapacityText.SetResourceReference(TextBlock.ForegroundProperty, needed > sessions.Count ? "MeetingBrush" : "Text");
        }
        catch (Exception ex)
        {
            Log.Error("UpdateTodayCapacity", ex);
        }
    }

    /// <summary>
    /// Puts today's tasks into today's focus sessions, most important first. Quick tasks share a
    /// session; longer ones get a session to themselves and carry on into the next ones.
    /// </summary>
    private void FillToday_Click(object sender, RoutedEventArgs e)
    {
        if (_planDate != DateTime.Today && !(_mode == RunMode.DayPlan && _runner.IsRunning)) LoadPlanForDate(DateTime.Today);
        var sessions = RemainingFocusToday(out var plan);
        if (sessions.Count == 0)
        {
            Info("There are no focus sessions left today to fill.", "Fill today's sessions");
            return;
        }
        var alreadyPlaced = new HashSet<string>(sessions.SelectMany(s => s.Tasks).Select(t => t.BinId).Where(id => id.Length > 0));
        var tasks = TodaysBinTasks().Where(t => !alreadyPlaced.Contains(t.Id))
            .OrderBy(TaskBase.Rank).ThenBy(t => t.Due ?? DateTime.MaxValue).ThenBy(t => TaskBin.All.IndexOf(t)).ToList();
        if (tasks.Count == 0)
        {
            Info("No tasks are picked for today. In your task list, click ☀ on the tasks you want to do today (due and overdue tasks count too).",
                "Fill today's sessions");
            return;
        }

        PushUndo(includePlan: _planDate == DateTime.Today);
        var load = sessions.ToDictionary(s => s, s => s.Tasks.Where(t => !t.Done).Sum(Size));
        int placed = 0;
        foreach (var task in tasks)
        {
            double size = Size(task);
            int at = -1;
            for (int i = 0; i < sessions.Count && at < 0; i++)
                if (size < 1 ? load[sessions[i]] + size <= 1.01 : load[sessions[i]] == 0) at = i;
            if (at < 0) continue;

            sessions[at].Tasks.Add(LinkedCopy(task));
            load[sessions[at]] += Math.Min(size, 1);
            // A longer task also reserves the sessions after it (it carries on into them).
            for (int extra = 1; extra < Math.Ceiling(size) && at + extra < sessions.Count; extra++)
                load[sessions[at + extra]] = Math.Max(load[sessions[at + extra]], 1);
            placed++;
        }

        // Save wherever those sessions live.
        if (_mode == RunMode.DayPlan && _runner.IsRunning) SaveRunState();
        if (_planDate == DateTime.Today) { _planDirty = true; SavePlanNow(); }
        else if (!_runner.IsRunning)
        {
            var (_, start) = PlanFor(DateTime.Today);
            Storage.Save(DayPlanFile.FileFor(DateTime.Today), new DayPlanFile
            {
                Date = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                DayStart = start.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture),
                Blocks = plan.Select(b => b.Clone()).ToList()
            });
        }
        RefreshTasksTab();
        RefreshDashTasks();
        int left = tasks.Count - placed;
        Info(left == 0
                ? $"Placed all {placed} of today's tasks into your focus sessions, most important first. They stay in your task list until you finish them."
                : $"Placed {placed} tasks. {left} didn't fit today; they're still in your task list.",
            "Fill today's sessions");
    }

    // ---------- Adding tasks ----------

    private static TaskItem NewSessionTask(string input)
    {
        var task = new TaskItem();
        TaskParser.Apply(task, input);
        return task;
    }

    private void NewTaskBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsEnter(e)) return;
        e.Handled = true;
        if (NewTaskBox.Text.Trim().Length == 0 || _selected == null) return;
        _selected.Tasks.Add(NewSessionTask(NewTaskBox.Text.Trim()));
        NewTaskBox.Text = "";
    }

    private void DashTaskBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsEnter(e)) return;
        e.Handled = true;
        var cur = _runner.Current;
        if (DashTaskBox.Text.Trim().Length == 0) return;
        if (DashTasksSession.IsChecked != true)
        {
            // Today or All: it goes to your task list (picked for today in the Today view).
            var task = new BinTask { From = $"Added {DateTime.Now:ddd MMM d}" };
            TaskParser.Apply(task, DashTaskBox.Text.Trim());
            if (task.Text.Length == 0) return;
            if (DashTasksToday.IsChecked == true && task.PlannedFor.Length == 0)
                task.PlannedFor = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            PushUndo();
            TaskBin.All.Add(task);
            TaskBin.Save();
            DashTaskBox.Text = "";
            return;
        }
        if (cur == null || !cur.IsFocus) return;
        cur.Tasks.Add(NewSessionTask(DashTaskBox.Text.Trim()));
        DashTaskBox.Text = "";
        RefreshDashTasks();
        SaveRunState();
    }

    private void BinNewBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; BinNewBox.Text = ""; AddRow.Visibility = Visibility.Collapsed; return; }
        if (!IsEnter(e)) return;
        e.Handled = true;
        if (BinNewBox.Text.Trim().Length == 0) return;
        AddToBin(BinNewBox.Text.Trim(), $"Added {DateTime.Now:ddd MMM d}");
        // Added on the Tomorrow tab: it's for tomorrow (unless you gave it a day yourself).
        if (BinTomorrowView.IsChecked == true && TaskBin.All.LastOrDefault() is BinTask added && added.PlannedFor.Length == 0 && added.Due == null)
        {
            added.PlannedFor = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            TaskBin.Save();
            RefreshTasksTab();
        }
        BinNewBox.Text = "";
    }

    /// <summary>Adds a task to the bin (used by the Tasks tab and the quick-add key).</summary>
    private void AddToBin(string input, string from)
    {
        var task = new BinTask { From = from };
        TaskParser.Apply(task, input);
        if (task.Text.Length == 0) return;
        PushUndo();
        TaskBin.All.Add(task);
        TaskBin.Save();
    }

    // ---------- Moving tasks between the bin and sessions ----------

    private void FromBin_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) OpenBinPicker((UIElement)sender, _selected);
    }

    private void DashFromBin_Click(object sender, RoutedEventArgs e)
    {
        if (_runner.Current is PlanBlock cur && cur.IsFocus) OpenBinPicker((UIElement)sender, cur);
    }

    private void OpenBinPicker(UIElement anchor, PlanBlock target)
    {
        KeepBlockEditorOpen();
        _binTarget = target;
        _binChoices = TaskBin.All.OrderBy(TaskBase.Rank).ThenBy(t => t.Due ?? DateTime.MaxValue)
            .Select(t => new ListChoice { Id = t.Id, Name = t.Text, Detail = t.HasDetails ? t.Details : t.From }).ToList();
        BinPickerList.ItemsSource = _binChoices;
        BinPickerEmpty.Visibility = _binChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BinPickerPopup.PlacementTarget = anchor;
        BinPickerPopup.IsOpen = true;
    }

    /// <summary>Moves the checked tasks out of the bin and onto the session (keeping their details).</summary>
    private void BinPickerPopup_Closed(object? sender, EventArgs e)
    {
        var target = _binTarget;
        _binTarget = null;
        if (target == null) return;
        var chosen = new HashSet<string>(_binChoices.Where(c => c.IsChecked).Select(c => c.Id));
        if (chosen.Count == 0) return;
        PushUndo(includePlan: true);
        foreach (var task in TaskBin.All.Where(t => chosen.Contains(t.Id)).ToList())
            if (!target.Tasks.Any(t => t.BinId == task.Id)) target.Tasks.Add(LinkedCopy(task));
        RefreshDashTasks();
        RefreshPlanTasks();
        if (_runner.IsRunning) SaveRunState();
    }

    /// <summary>
    /// Taking a task off a session never loses it: a checked one is recorded as resolved,
    /// an unchecked one goes to your task list with its details.
    /// </summary>
    private void SetTaskAside(PlanBlock block, TaskItem task)
    {
        PushUndo(includePlan: true);
        block.Tasks.Remove(task);
        if (task.Text.Trim().Length == 0) return;
        string from = $"From {block.Name}, {DateTime.Now:ddd MMM d}";
        if (FindLinked(task) is BinTask original)
        {
            // From the task list: finishing it finishes the list task; otherwise it simply stays in the list.
            if (task.Done) FinishListTask(original, from, task.Spent);
            RefreshPlanTasks();
            return;
        }
        if (task.Done) ResolveTask(task, from);
        else
        {
            var back = new BinTask { From = from };
            back.CopyDetailsFrom(task);
            TaskBin.All.Add(back);
            TaskBin.Save();
            _toast.ShowMessage("Moved to your task list", task.Text);
        }
    }

    /// <summary>
    /// When a focus session ends: checked tasks are recorded as resolved (with how many sessions they
    /// took). An unchecked task estimated at more sessions than it has had carries on into the next
    /// focus session; anything else unfinished goes to your task list.
    /// </summary>
    private void MoveUnfinishedToBin(PlanBlock? ended)
    {
        if (ended == null || !ended.IsFocus) return;
        foreach (var t in ended.Tasks.Where(t => t.Text.Trim().Length > 0)) t.Spent++;

        var finished = ended.Tasks.Where(t => t.Done && t.Text.Trim().Length > 0).ToList();
        foreach (var t in finished)
        {
            ended.Tasks.Remove(t);
            if (FindLinked(t) is BinTask original) FinishListTask(original, $"From {ended.Name}", t.Spent);
            else if (t.BinId.Length == 0) ResolveTask(t, $"From {ended.Name}");
        }

        // The next focus session in this run, for tasks that continue.
        var blocks = _runner.Blocks.ToList();
        int at = blocks.IndexOf(ended);
        var next = at < 0 ? null : blocks.Skip(at + 1).FirstOrDefault(b => b.IsFocus);

        var carried = new List<TaskItem>();
        var unfinished = ended.Tasks.Where(t => !t.Done && t.Text.Trim().Length > 0).ToList();
        foreach (var t in unfinished)
        {
            ended.Tasks.Remove(t);
            if (t.BinId.Length > 0)
            {
                // From the task list: it's still there, so nothing goes to the bin. Count the session worked.
                if (FindLinked(t) is BinTask original) original.Spent = Math.Max(original.Spent, t.Spent);
                if (_settings.On("carryOver") && next != null && !ReferenceEquals(next, ended) && t.Estimate >= 1 &&
                    t.Spent < Math.Ceiling(t.Estimate) && !next.Tasks.Any(x => x.BinId == t.BinId))
                    next.Tasks.Insert(Math.Min(carried.Count, next.Tasks.Count), t);
                continue;
            }
            if (_settings.On("carryOver") && next != null && !ReferenceEquals(next, ended) && t.Estimate >= 1 && t.Spent < Math.Ceiling(t.Estimate))
            {
                next.Tasks.Insert(Math.Min(carried.Count, next.Tasks.Count), t);
                carried.Add(t);
            }
            else
            {
                var back = new BinTask { From = $"From {ended.Name}, {DateTime.Now:ddd MMM d}" };
                back.CopyDetailsFrom(t);
                TaskBin.All.Add(back);
            }
        }
        int binned = unfinished.Count(t => t.BinId.Length == 0) - carried.Count;
        TaskBin.Save();
        if (binned > 0)
        {
            TaskBin.Save();
            string plural = binned == 1 ? "task" : "tasks";
            _toast.ShowMessage($"{binned} unfinished {plural} moved to your task list", "Add them to another focus session, or pick them for another day.");
        }
        if (carried.Count > 0 && binned == 0)
            _toast.ShowMessage("Carrying on", $"{string.Join(", ", carried.Select(c => c.Text))} continues in the next session.");
    }

    // ---------- Your task list's buttons ----------

    private void ResolveBinTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BinTask task) return;
        PushUndo(includePlan: true);
        FinishListTask(task, task.From, task.Spent);
        _toast.ShowMessage("Task finished", task.Text, "Undo", Undo);
    }

    private void ToggleTodayBinTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BinTask task) return;
        PushUndo();
        task.PlannedFor = task.IsPlannedToday ? "" : DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        TaskBin.Save();
        RefreshTasksTab();
    }

    private void ReopenDoneTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResolvedTask task) return;
        PushUndo();
        ResolvedTasks.All.Remove(task);
        ResolvedTasks.Save();
        TaskBin.All.Add(new BinTask { Text = task.Text, From = $"Reopened {DateTime.Now:ddd MMM d}" });
        TaskBin.Save();
    }

    // ---------- Task details (click a task) ----------

    private TaskBase? _editingTask;
    private Action? _afterTaskSave;
    private Action? _deleteTask;

    private void EditorTaskText_Click(object sender, MouseButtonEventArgs e)
    {
        var block = _selected;
        if (!_settings.On("taskDetails") || block == null || (sender as FrameworkElement)?.DataContext is not TaskItem task) return;
        OpenTaskDetails((UIElement)sender, task, () => { if (ViewingRunningDay) SaveRunState(); }, () => SetTaskAside(block, task));
    }

    private void DashTaskText_Click(object sender, MouseButtonEventArgs e)
    {
        var block = _runner.Current;
        if (!_settings.On("taskDetails") || !_settings.On("dashTaskEditing") || block == null ||
            (sender as FrameworkElement)?.DataContext is not TaskItem task) return;
        OpenTaskDetails((UIElement)sender, task, () => { SaveRunState(); RefreshDashTasks(); }, () =>
        {
            block.Tasks.Remove(task);
            RefreshDashTasks();
            SaveRunState();
        });
    }

    private void BinTaskText_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BinTask task) return;
        if (HandleSelectionClick(task)) return;
        if (e.ClickCount >= 2) { CancelPendingOpen(); StartRename(task, sender as FrameworkElement); return; }
        if (!_settings.On("taskDetails")) return;
        var anchor = (UIElement)sender;
        // Wait a moment in case this is the first half of a double-click (which renames instead).
        DelayOpen(() => OpenBinTaskDetails(anchor, task));
    }

    private void OpenBinTaskDetails(UIElement anchor, BinTask task)
    {
        if (!TaskBin.All.Contains(task)) return;
        OpenTaskDetails(anchor, task, () => { TaskBin.Save(); RefreshTasksTab(); }, () =>
        {
            PushUndo(includePlan: true);
            TaskBin.All.Remove(task);
            TaskBin.Save();
            RemoveLinkedCopies(task.Id);
        });
    }

    private void OpenTaskDetails(UIElement anchor, TaskBase task, Action afterSave, Action delete)
    {
        KeepBlockEditorOpen();
        _editingTask = task;
        _afterTaskSave = afterSave;
        _deleteTask = delete;
        TaskTextBox.Text = task.Text;
        TaskPrioHigh.IsChecked = task.Priority == TaskPriority.High;
        TaskPrioNormal.IsChecked = task.Priority == TaskPriority.Normal;
        TaskPrioLow.IsChecked = task.Priority == TaskPriority.Low;
        TaskDueBox.Text = task.Due?.ToString("d") ?? "";
        TaskEstNone.IsChecked = task.Estimate <= 0;
        TaskEstQuick.IsChecked = task.Estimate > 0 && task.Estimate < 1;
        TaskEst1.IsChecked = task.Estimate == 1;
        TaskEst2.IsChecked = task.Estimate == 2;
        TaskEst3.IsChecked = task.Estimate == 3;
        TaskEst4.IsChecked = task.Estimate >= 4;
        TaskTagsBox.Text = string.Join(", ", task.Tags);
        TaskTodayCheck.IsChecked = task.IsPlannedToday;
        FillExtraDetails(task);
        TaskPopupNote.Text = "";
        TaskPopup.PlacementTarget = anchor;
        TaskPopup.IsOpen = true;
        TaskTextBox.Focus();
        TaskTextBox.SelectAll();
    }

    private void TaskDueToday_Click(object sender, RoutedEventArgs e) => TaskDueBox.Text = DateTime.Today.ToString("d");
    private void TaskDueTomorrow_Click(object sender, RoutedEventArgs e) => TaskDueBox.Text = DateTime.Today.AddDays(1).ToString("d");
    private void TaskDueNone_Click(object sender, RoutedEventArgs e) => TaskDueBox.Text = "";

    private void TaskPopupSave_Click(object sender, RoutedEventArgs e)
    {
        var task = _editingTask;
        if (task == null) return;
        string text = TaskTextBox.Text.Trim();
        if (text.Length == 0)
        {
            TaskPopupNote.Text = "The task needs some text.";
            return;
        }
        DateTime? due = null;
        if (TaskDueBox.Text.Trim().Length > 0)
        {
            due = TaskParser.ParseDate(TaskDueBox.Text);
            if (due == null)
            {
                TaskPopupNote.Text = "Couldn't read that date. Try 10/14, Friday, or tomorrow, or leave it empty for no due date.";
                return;
            }
        }

        PushUndo(includePlan: !(task is BinTask));
        if (SaveExtraDetails(task) is string problem)
        {
            TaskPopupNote.Text = problem;
            return;
        }
        task.Text = text;
        if (TaskProjectRow.Visibility == Visibility.Visible) task.Project = ChosenProject();
        task.Priority = TaskPrioHigh.IsChecked == true ? TaskPriority.High : TaskPrioLow.IsChecked == true ? TaskPriority.Low : TaskPriority.Normal;
        task.Due = due;
        task.Estimate = TaskEstQuick.IsChecked == true ? 0.34 : TaskEst1.IsChecked == true ? 1 : TaskEst2.IsChecked == true ? 2
            : TaskEst3.IsChecked == true ? 3 : TaskEst4.IsChecked == true ? 4 : 0;
        task.Tags = TaskTagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (task is BinTask && TaskTodayCheck.Visibility == Visibility.Visible)
            task.PlannedFor = TaskTodayCheck.IsChecked == true ? DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "";

        TaskPopup.IsOpen = false;
        _afterTaskSave?.Invoke();
    }

    private void TaskPopupDelete_Click(object sender, RoutedEventArgs e)
    {
        TaskPopup.IsOpen = false;
        _deleteTask?.Invoke();
    }

    // ---------- Quick-add key (Ctrl+Shift+Space) ----------

    private const int HotkeyId = 0x4643; // "FC"
    private string _quickAddKeys = "";

    /// <summary>Registers a key you can press anywhere in Windows to add a task to the bin.</summary>
    private void SetUpQuickAdd()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(QuickAddHook);
            const uint control = 0x2, shift = 0x4, alt = 0x1, noRepeat = 0x4000, space = 0x20;
            if (RegisterHotKey(handle, HotkeyId, control | shift | noRepeat, space)) _quickAddKeys = "Ctrl+Shift+Space";
            else if (RegisterHotKey(handle, HotkeyId, control | alt | noRepeat, space)) _quickAddKeys = "Ctrl+Alt+Space";
        }
        catch (Exception ex)
        {
            Log.Error("SetUpQuickAdd", ex);
        }
        QuickAddInfo.Text = _quickAddKeys.Length > 0
            ? $"Press {_quickAddKeys} anywhere in Windows to add a task to your task list without switching apps."
            : "The quick-add key couldn't be set up, because another app already uses it.";
    }

    private IntPtr QuickAddHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmHotkey = 0x0312;
        if (msg == WmHotkey && wParam.ToInt32() == FocusKeyId)
        {
            handled = true;
            FocusKeyPressed();
            return IntPtr.Zero;
        }
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            // The main quick-add box always adds to your work tasks (Personal has its own).
            var box = new QuickAddWindow(text => { using (TaskStores.Use(TaskStores.Main)) AddToBin(text, $"Quick add, {DateTime.Now:ddd MMM d}"); });
            box.Show();
        }
        return IntPtr.Zero;
    }

    private void RemoveQuickAdd()
    {
        try { UnregisterHotKey(new System.Windows.Interop.WindowInteropHelper(this).Handle, HotkeyId); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------- Tasks from the list, placed in sessions ----------

    /// <summary>A copy of a list task for a session, linked back to it.</summary>
    private static TaskItem LinkedCopy(BinTask task)
    {
        var item = new TaskItem { BinId = task.Id };
        item.CopyDetailsFrom(task);
        return item;
    }

    private static BinTask? FindLinked(TaskItem item) =>
        item.BinId.Length == 0 ? null : TaskBin.All.FirstOrDefault(t => t.Id == item.BinId);

    /// <summary>Finishes a task in your list: it's resolved, leaves the list, and comes off every session it was in.</summary>
    private void FinishListTask(BinTask task, string from, int spent)
    {
        task.Spent = Math.Max(task.Spent, spent);
        TaskBin.All.Remove(task);
        TaskBin.Save();
        ResolveTask(task, from);
        RemoveLinkedCopies(task.Id);
    }

    /// <summary>Takes a list task off every session it was placed in (today's run and the plan on screen).</summary>
    private void RemoveLinkedCopies(string binId)
    {
        bool changed = false;
        var blocks = _plan.Concat(_runner.IsRunning ? _runner.Blocks : Array.Empty<PlanBlock>()).Distinct();
        foreach (var block in blocks)
            foreach (var t in block.Tasks.Where(t => t.BinId == binId).ToList())
            {
                block.Tasks.Remove(t);
                changed = true;
            }
        if (!changed) return;
        if (_runner.IsRunning) SaveRunState();
        RefreshDashTasks();
        RefreshPlanTasks();
    }

    // ---------- The task list in the Day plan ----------

    /// <summary>A task in the Day plan's list, with how many of this day's sessions it's in.</summary>
    public sealed class PlanTaskRow
    {
        public BinTask Task { get; init; } = null!;
        public string Sessions { get; init; } = "";
    }

    private string _planTaskSearch = "";

    private void RefreshPlanTasks()
    {
        if (PlanTaskList == null) return;
        var counts = _plan.SelectMany(b => b.Tasks).Where(t => t.BinId.Length > 0)
            .GroupBy(t => t.BinId).ToDictionary(g => g.Key, g => g.Count());

        // "Today" (or the day on screen): picked for it, due on it, already in its sessions, or overdue when it's today.
        bool dayOnly = PlanTasksDay.IsChecked == true, undatedOnly = PlanTasksUndated?.IsChecked == true;
        PlanTasksDay.Content = _planDate == DateTime.Today ? "Today" : _planDate == DateTime.Today.AddDays(1) ? "Tomorrow" : _planDate.ToString("ddd MMM d");
        bool ForThisDay(BinTask t) =>
            t.PlannedDate == _planDate || t.Due == _planDate || counts.ContainsKey(t.Id) ||
            (_planDate == DateTime.Today && t.IsOverdue);

        var rows = TaskBin.All
            .Where(t => !dayOnly || ForThisDay(t))
            .Where(t => !undatedOnly || t.IsUndated)
            .Where(t => !(t.IsSnoozed && _settings.On("upcoming") && !(dayOnly && t.SnoozedUntil == _planDate)))
            .Where(t => _planTaskSearch.Length == 0 || t.Text.Contains(_planTaskSearch, StringComparison.CurrentCultureIgnoreCase) ||
                        t.Tags.Any(tag => tag.Contains(_planTaskSearch.TrimStart('#'), StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(TaskBase.Rank).ThenBy(t => t.Due ?? DateTime.MaxValue).ThenBy(t => TaskBin.All.IndexOf(t))
            .Select(t => new PlanTaskRow
            {
                Task = t,
                Sessions = counts.TryGetValue(t.Id, out int n) ? (n == 1 ? "In 1 session this day" : $"In {n} sessions this day") : ""
            })
            .ToList();
        if (!(PlanTaskList.ItemsSource is List<PlanTaskRow> old && old.Count == rows.Count &&
              old.Zip(rows).All(p => ReferenceEquals(p.First.Task, p.Second.Task) && p.First.Sessions == p.Second.Sessions)))
            PlanTaskList.ItemsSource = rows;
        PlanTasksEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlanTasksEmpty.Text = dayOnly
            ? $"Nothing set for {(_planDate == DateTime.Today ? "today" : "this day")}. Switch to All, or pick tasks for a day with ☀ or a due date."
            : "No tasks. Add one above, or in the Tasks tab.";
    }

    private void PlanTasksView_Checked(object sender, RoutedEventArgs e)
    {
        if (PlanTaskList == null || PlanTasksAll == null) return;
        _settings.PlanTasksDayOnly = PlanTasksDay.IsChecked == true;
        _settings.PlanTasksView = PlanTasksDay.IsChecked == true ? "day" : PlanTasksUndated.IsChecked == true ? "undated" : "all";
        SaveSettingsQuietly();
        RefreshPlanTasks();
    }

    private void PlanTaskSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _planTaskSearch = PlanTaskSearch.Text.Trim();
        PlanTaskSearchHint.Visibility = PlanTaskSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshPlanTasks();
    }

    private void PlanTaskAdd_TextChanged(object sender, TextChangedEventArgs e) =>
        PlanTaskAddHint.Visibility = PlanTaskAdd.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void PlanTaskAdd_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsEnter(e)) return;
        e.Handled = true;
        if (PlanTaskAdd.Text.Trim().Length == 0) return;
        var task = new BinTask { From = $"Added {DateTime.Now:ddd MMM d}" };
        TaskParser.Apply(task, PlanTaskAdd.Text.Trim());
        if (task.Text.Length == 0) return;
        // Added while showing one day's tasks: it's for that day.
        if (PlanTasksDay.IsChecked == true && task.PlannedFor.Length == 0)
            task.PlannedFor = _planDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        PushUndo();
        TaskBin.All.Add(task);
        TaskBin.Save();
        PlanTaskAdd.Text = "";
    }

    private void BinNewBox_TextChanged(object sender, TextChangedEventArgs e) =>
        BinNewHint.Visibility = BinNewBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    // Dragging a task from the list: press, move a little, and it starts dragging. A plain click edits it.
    private Point _planTaskDown;
    private PlanTaskRow? _planTaskPressed;

    private void PlanTask_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _planTaskPressed = (sender as FrameworkElement)?.DataContext as PlanTaskRow;
        _planTaskDown = e.GetPosition(this);
    }

    private void PlanTask_MouseMove(object sender, MouseEventArgs e)
    {
        if (_planTaskPressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _planTaskDown.X) + Math.Abs(p.Y - _planTaskDown.Y) < 8) return;
        var row = _planTaskPressed;
        _planTaskPressed = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(BinTask), row.Task), DragDropEffects.Copy);
    }

    private void PlanTask_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var row = _planTaskPressed;
        _planTaskPressed = null;
        if (row == null || !_settings.On("taskDetails")) return;
        OpenTaskDetails((UIElement)sender, row.Task, () => { TaskBin.Save(); RefreshTasksTab(); }, () =>
        {
            PushUndo(includePlan: true);
            TaskBin.All.Remove(row.Task);
            TaskBin.Save();
            RemoveLinkedCopies(row.Task.Id);
        });
    }

    /// <summary>A task was dropped on a focus session: a linked copy goes into it; the list keeps the task.</summary>
    private void AddTaskToSession(PlanBlock block, BinTask task)
    {
        if (!block.IsFocus) return;
        int running = RunningIndex;
        if (running >= 0 && _plan.IndexOf(block) < running)
        {
            _toast.ShowMessage("That session is over", "Drop the task on the current session or a later one.");
            return;
        }
        if (block.Tasks.Any(t => t.BinId == task.Id))
        {
            _toast.ShowMessage("Already there", $"\"{task.Text}\" is already in {block.Name}.");
            return;
        }
        PushUndo(includePlan: true);
        block.Tasks.Add(LinkedCopy(task));
        if (ReferenceEquals(block, _runner.Current)) RefreshDashTasks();
        if (_runner.IsRunning) SaveRunState();
        RefreshPlanTasks();
        _toast.ShowMessage($"Added to {block.Name}", task.Text);
    }

    // ---------- Saved layouts (in a popup now) ----------

    private void Layouts_Click(object sender, RoutedEventArgs e)
    {
        LayoutsPopup.PlacementTarget = (UIElement)sender;
        LayoutsPopup.IsOpen = true;
    }
}
