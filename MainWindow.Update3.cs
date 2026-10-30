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
/// Update 3: the sidebar, resizable Day plan, command bar, start-focus key, website blocking,
/// projects, templates, goals and streaks, stats and workload, automations, and age filters.
/// </summary>
public partial class MainWindow
{
    private void SetUpUpdate3()
    {
        Projects.Load();
        Templates.Load();
        Projects.Changed += () => { RefreshProjects(); RefreshTasksTab(); };
        RefreshProjects();
        RefreshTemplates();
        PlanTasksColumn.Width = new GridLength(Math.Clamp(_settings.PlanTasksWidth, 220, 700));
        NewProjectBox.TextChanged += (_, _) => NewProjectHint.Visibility = NewProjectBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateGoals();
        if (_settings.SidebarCollapsed) SetSidebarCollapsed(true);

        // Left over from a crash: website blocking still on with nothing running. Offer to remove it.
        if (HostsBlocker.IsActive() && !Storage.TryLoad<RunState>("run-state.json", out var rs))
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Ask("Website blocking is still switched on from an earlier session. Turn it off now? (Windows will ask for permission.)", "Websites"))
                    HostsBlocker.Request(false, Array.Empty<string>());
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void SideAddTask_Click(object sender, RoutedEventArgs e)
    {
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        var box = new QuickAddWindow(text => { using (TaskStores.Use(TaskStores.Main)) AddToBin(text, $"Added {DateTime.Now:ddd MMM d}"); });
        if (IsVisible) box.Owner = this;
        box.Show();
    }

    private void PlanSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _settings.PlanTasksWidth = PlanTasksColumn.ActualWidth;
        SaveSettingsQuietly();
    }

    // ---------- Command bar (Ctrl+K) ----------

    private void OpenCommandBar()
    {
        if (!_settings.On("commandBar")) return;
        ShowFromTray();
        var bar = new CommandBarWindow(SearchCommands) { Owner = this };
        bar.Show();
    }

    private List<CommandItem> SearchCommands(string query)
    {
        var items = new List<CommandItem>();
        if (query.StartsWith("+"))
        {
            string text = query[1..].Trim();
            if (text.Length > 0)
                items.Add(new CommandItem { Title = $"Add task: {text}", Kind = "Task", Run = () => AddToBin(text, $"Added {DateTime.Now:ddd MMM d}") });
            return items;
        }

        void Go(string title, RadioButton nav) => items.Add(new CommandItem { Title = title, Kind = "Go to", Run = () => nav.IsChecked = true });
        if (_settings.On("home")) Go("Home", NavHome);
        Go("Dashboard", NavDashboard);
        Go("Day plan", NavPlan);
        Go("Calendar", NavCalendar);
        Go("Tasks", NavTasks);
        if (NavStats.Visibility == Visibility.Visible) Go("Stats", NavStats);
        Go("Block lists", NavLists);
        Go("Pomodoro", NavPomodoro);
        Go("Quick focus", NavFocus);
        Go("Settings", NavSettings);
        items.Add(new CommandItem { Title = "Start my day", Kind = "Action", Run = () => StartTodayPlan(automatic: false) });
        items.Add(new CommandItem { Title = "Start Pomodoro", Kind = "Action", Run = () => StartPomodoro_Click(this, new RoutedEventArgs()) });
        items.Add(new CommandItem { Title = "Start quick focus", Kind = "Action", Run = () => StartQuickFocus_Click(this, new RoutedEventArgs()) });
        items.Add(new CommandItem { Title = "Stop the current session", Kind = "Action", Run = () => Stop_Click(this, new RoutedEventArgs()) });
        items.Add(new CommandItem { Title = "Fill today's sessions", Kind = "Action", Run = () => FillToday_Click(this, new RoutedEventArgs()) });
        items.Add(new CommandItem { Title = "Auto-fill this day", Kind = "Action", Run = () => { NavPlan.IsChecked = true; AutoFill_Click(this, new RoutedEventArgs()); } });
        if (_settings.On("review")) items.Add(new CommandItem { Title = "Review today", Kind = "Action", Run = OpenReview });
        if (_settings.On("undo")) items.Add(new CommandItem { Title = "Undo", Kind = "Action", Run = Undo });
        items.Add(new CommandItem { Title = "Switch light or dark mode", Kind = "Action", Run = ToggleTheme });
        foreach (var list in BlockLists.All)
            items.Add(new CommandItem { Title = $"Quick focus blocking {list.Name}", Kind = "Action", Run = () => QuickFocusWithList(list) });
        foreach (var t in TaskBin.All)
            items.Add(new CommandItem
            {
                Title = t.Text,
                Kind = t.HasProject ? t.ProjectName : "Task",
                Run = () => { NavTasks.IsChecked = true; BinTodoView.IsChecked = true; SearchBox.Text = t.Text; }
            });

        if (query.Length == 0) return items.Where(i => i.Kind != "Task" && !TaskBin.All.Any(t => t.Text == i.Title)).ToList();
        // Best matches first: starts-with, then contains (every word must match somewhere).
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return items
            .Where(i => words.All(w => i.Title.ToLowerInvariant().Contains(w) || i.Kind.ToLowerInvariant().Contains(w)))
            .OrderBy(i => i.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => i.Kind == "Task" ? 1 : 0)
            .Concat(new[] { new CommandItem { Title = $"Add task: {query}", Kind = "Task", Run = () => AddToBin(query, $"Added {DateTime.Now:ddd MMM d}") } })
            .ToList();
    }

    private void ToggleTheme()
    {
        bool dark = _settings.Theme == "Dark" || (_settings.Theme == "System" && ((SolidColorBrush)FindResource("Bg")).Color.R < 128);
        _settings.Theme = dark ? "Light" : "Dark";
        ThemeManager.Apply(_settings.Theme);
        SaveSettingsQuietly();
        FillSettingFields();
    }

    private void QuickFocusWithList(BlockList list)
    {
        if (!_settings.QuickFocusLists.Contains(list.Id))
        {
            _settings.QuickFocusLists = new List<string> { list.Id };
            UpdateQuickBlockButtons();
            SaveSettingsQuietly();
        }
        StartQuickFocus_Click(this, new RoutedEventArgs());
    }

    // ---------- Start-focus key (Ctrl+Shift+F) ----------

    private const int FocusKeyId = 0x4644;
    private bool _focusKeyRegistered;

    private void SetUpFocusKey(bool on)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            if (on && !_focusKeyRegistered)
                _focusKeyRegistered = RegisterHotKey(handle, FocusKeyId, 0x2 | 0x4 | 0x4000, 0x46); // Ctrl+Shift+F
            else if (!on && _focusKeyRegistered)
            {
                UnregisterHotKey(handle, FocusKeyId);
                _focusKeyRegistered = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("SetUpFocusKey", ex);
        }
    }

    /// <summary>The start-focus key: starts a quick focus session, or stops one that's running (if your lock mode allows).</summary>
    private void FocusKeyPressed()
    {
        if (_runner.IsRunning)
        {
            if (_mode != RunMode.QuickFocus)
            {
                _toast.ShowMessage("A session is already running", "The start-focus key only starts and stops quick focus.");
                return;
            }
            if (LockedNow)
            {
                _toast.ShowMessage("Locked", "This focus session can't be stopped early.");
                return;
            }
            _mode = RunMode.None;
            _runner.Stop();
            _toast.ShowMessage("Focus stopped", "");
            return;
        }
        StartQuickFocus_Click(this, new RoutedEventArgs());
        if (_runner.IsRunning) _toast.ShowMessage("Focus started", $"{QuickMinutesBox.Value} minutes. Press Ctrl+Shift+F again to stop.");
    }

    private void UpdateKeysInfo()
    {
        var keys = new List<string>();
        if (_settings.On("commandBar")) keys.Add("Ctrl+K opens the command bar (while Focus is open).");
        if (_settings.On("focusKey"))
            keys.Add(_focusKeyRegistered ? "Ctrl+Shift+F anywhere starts or stops a quick focus session." : "Ctrl+Shift+F couldn't be set up; another app uses it.");
        if (_settings.On("quickAdd") && _quickAddKeys.Length > 0) keys.Add($"{_quickAddKeys} anywhere adds a task.");
        keys.Add("Ctrl+Z undoes, and Delete removes the selected block in the Day plan.");
        KeysInfo.Text = string.Join("\n", keys);
    }

    // ---------- Website blocking ----------

    private bool _sitesBlockedByUs;

    /// <summary>Turns website blocking on when a run starts (if any of its focus sessions block something).</summary>
    private void StartWebsiteBlocking()
    {
        // Retired: Focus no longer blocks websites (app blocking is unaffected). Stopping still clears any old
        // hosts-file entries left by earlier versions.
        return;
#pragma warning disable CS0162 // the old approach, kept below for reference
        if (!_settings.On("websites") || _settings.BlockedSites.Count == 0 || _sitesBlockedByUs) return;
        bool blocksAnything = _runner.Blocks.Any(b => b.IsFocus && b.HasBlockedApps);
        if (!blocksAnything) return;
        if (HostsBlocker.Request(true, _settings.BlockedSites)) _sitesBlockedByUs = true;
        else _toast.ShowMessage("Websites not blocked", "Windows permission wasn't given, so sites stay open this time.");
#pragma warning restore CS0162
    }

    private void StopWebsiteBlocking()
    {
        if (!_sitesBlockedByUs && !(_settings.On("websites") && HostsBlocker.IsActive() && !_runner.IsRunning)) return;
        if (!HostsBlocker.IsActive()) { _sitesBlockedByUs = false; return; }
        if (HostsBlocker.Request(false, Array.Empty<string>())) _sitesBlockedByUs = false;
        else _toast.ShowMessage("Websites are still blocked", "Windows permission wasn't given. Focus will ask again next time.");
    }

    // ---------- Projects ----------

    private void RefreshProjects()
    {
        using var pageSet = TaskStores.Use(TasksPageSet); // the set this page shows (Work, or Personal from its own button)
        if (BinFilters != null) BuildBinFilters();
    }

    private void NewProjectBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        string name = NewProjectBox.Text.Trim();
        if (name.Length == 0) return;
        if (Projects.All.Any(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            _toast.ShowMessage("That project already exists", name);
            return;
        }
        Projects.Add(name);
        NewProjectBox.Text = "";
        NewProjectRow.Visibility = Visibility.Collapsed;
    }

    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Project project) return;
        if (!Ask($"Delete the project \"{project.Name}\"? Its tasks stay in your list, without a project.", "Delete project", defaultNo: true)) return;
        foreach (var t in TaskBin.All.Where(t => t.Project == project.Id)) t.Project = "";
        TaskBin.Save();
        Projects.All.Remove(project);
        Projects.Save();
    }

    /// <summary>The project choices in the task panel.</summary>
    private void FillProjectPicker(TaskBase task)
    {
        TaskProjectPicker.Children.Clear();
        TaskProjectRow.Visibility = _settings.On("projects") && Projects.All.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        void Add(string id, string name, string color)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (id.Length > 0)
                content.Children.Add(new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 6, 0), Fill = (Brush)new BrushConverter().ConvertFromString(color)! });
            content.Children.Add(new TextBlock { Text = name });
            var r = new RadioButton { Content = content, GroupName = "taskproject", Tag = id, IsChecked = task.Project == id };
            r.SetResourceReference(StyleProperty, "NavButton");
            TaskProjectPicker.Children.Add(r);
        }
        Add("", "None", "");
        foreach (var p in Projects.All) Add(p.Id, p.Name, p.Color);
    }

    private string ChosenProject() =>
        TaskProjectPicker.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "";

    // ---------- Templates ----------

    private void RefreshTemplates() => TemplateList.ItemsSource = Templates.All.ToList();

    private void NewTemplate_Click(object sender, RoutedEventArgs e)
    {
        TemplateNameBox.Text = "";
        TemplateLinesBox.Text = "";
        TemplateNote.Text = "";
        TemplatePopup.PlacementTarget = (UIElement)sender;
        TemplatePopup.IsOpen = true;
        TemplateNameBox.Focus();
    }

    private void TemplateCancel_Click(object sender, RoutedEventArgs e) => TemplatePopup.IsOpen = false;

    private void TemplateSave_Click(object sender, RoutedEventArgs e)
    {
        string name = TemplateNameBox.Text.Trim();
        if (name.Length == 0 || TemplateLinesBox.Text.Trim().Length == 0)
        {
            TemplateNote.Text = "Give it a name and at least one task.";
            return;
        }
        Templates.All.Add(new TaskTemplate { Name = name, Lines = TemplateLinesBox.Text.Replace("\r\n", "\n") });
        Templates.Save();
        RefreshTemplates();
        TemplatePopup.IsOpen = false;
    }

    private void UseTemplate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskTemplate template) return;
        PushUndo();
        int added = 0;
        foreach (var line in template.Lines.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var task = new BinTask { From = $"Template: {template.Name}" };
            TaskParser.Apply(task, line);
            if (task.Text.Length == 0) continue;
            TaskBin.All.Add(task);
            added++;
        }
        TaskBin.Save();
        _toast.ShowMessage($"Added {added} tasks", $"From the {template.Name} template.");
    }

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskTemplate template) return;
        if (!Ask($"Delete the template \"{template.Name}\"?", "Delete template", defaultNo: true)) return;
        Templates.All.Remove(template);
        Templates.Save();
        RefreshTemplates();
    }

    // ---------- Daily goals and streaks ----------

    private int TasksDoneOn(DateTime day) => ResolvedTasks.All.Count(t => t.ResolvedAt.Date == day.Date);

    private bool GoalMet(DateTime day, Dictionary<DateTime, int>? counts = null)
    {
        _stats ??= FocusStats.Load();
        int tasks = counts != null ? counts.GetValueOrDefault(day.Date) : TasksDoneOn(day);
        return tasks >= _settings.GoalTasks || _stats.MinutesOn(day) >= _settings.GoalFocusHours * 60;
    }

    /// <summary>Days in a row the goal was met. Today counts once met; weekends never break the run.</summary>
    private int Streak()
    {
        var counts = ResolvedTasks.All.GroupBy(t => t.ResolvedAt.Date).ToDictionary(g => g.Key, g => g.Count());
        bool todayWeekend = DateTime.Today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        int streak = GoalMet(DateTime.Today, counts) && (_settings.CountWeekends || !todayWeekend) ? 1 : 0;
        for (var day = DateTime.Today.AddDays(-1); day > DateTime.Today.AddDays(-730); day = day.AddDays(-1))
        {
            bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (weekend && !_settings.CountWeekends) continue; // weekends left out of the streak
            if (GoalMet(day, counts)) streak++;
            else if (!weekend) break;
        }
        return streak;
    }

    private void UpdateGoals()
    {
        if (GoalWidget == null) return;
        bool on = _settings.On("goals");
        GoalWidget.Visibility = on && !_settings.SidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        _stats ??= FocusStats.Load();
        int tasks = TasksDoneOn(DateTime.Today);
        double hours = _stats.MinutesOn(DateTime.Today) / 60;
        GoalTasksText.Text = $"{tasks} of {_settings.GoalTasks} tasks";
        GoalTasksBar.Value = Math.Min(1, tasks / (double)_settings.GoalTasks);
        GoalFocusText.Text = $"{hours:0.#} of {_settings.GoalFocusHours:0.#} hours of focus";
        GoalFocusBar.Value = Math.Min(1, hours / _settings.GoalFocusHours);
        int streak = Streak();
        StreakText.Text = streak == 0 ? "Hit a goal today to start a streak" : streak == 1 ? "1 day streak" : $"{streak} day streak";

        string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (GoalMet(DateTime.Today) && _settings.LastGoalCelebrated != today)
        {
            _settings.LastGoalCelebrated = today;
            SaveSettingsQuietly();
            _toast.ShowMessage("Goal reached! 🎉", streak > 1 ? $"That's {streak} days in a row." : "Nice work today.");
        }
    }

    // ---------- Stats and workload ----------

    private void RefreshStats()
    {
        if (StatsPanel == null || StatsPanel.Visibility != Visibility.Visible) return;
        _stats ??= FocusStats.Load();
        var days = Enumerable.Range(0, 28).Select(i => DateTime.Today.AddDays(i - 27))
            .Where(d => _settings.CountWeekends || d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToList();
        StatsSummary.Text = _settings.On("goals")
            ? $"Current streak: {Streak()} days. Daily goal: {_settings.GoalTasks} tasks or {_settings.GoalFocusHours:0.#} hours of focus."
            : "Your last four weeks, and the week ahead.";

        if (_settings.On("stats"))
        {
            DrawBars(StatsFocusChart, days, days.Select(d => _stats.MinutesOn(d) / 60).ToList(), "FocusBrush", v => $"{v:0.#} h");
            DrawBars(StatsTasksChart, days, days.Select(d => (double)TasksDoneOn(d)).ToList(), "BreakBrush", v => $"{v:0}");

            var measured = ResolvedTasks.All.Where(t => t.Estimate > 0 && t.Spent > 0).ToList();
            StatsEstimateText.Text = measured.Count < 3
                ? "Give tasks an estimate and finish them in focus sessions; after a few, you'll see how estimates compare with reality here."
                : $"Over {measured.Count} tasks, you estimated {measured.Average(t => Math.Max(1, t.Estimate)):0.#} sessions on average and they took " +
                  $"{measured.Average(t => (double)t.Spent):0.#}. " +
                  (measured.Average(t => t.Spent - Math.Max(1, t.Estimate)) > 0.3 ? "Tasks tend to take longer than estimated: try adding a little extra." :
                   measured.Average(t => t.Spent - Math.Max(1, t.Estimate)) < -0.3 ? "Tasks tend to take less than estimated." : "Your estimates are spot on.");
        }

        if (_settings.On("workload")) DrawWorkload();
        if (_settings.On("statsGame")) DrawProgress();
    }

    /// <summary>The game side: level card, highlights, badges and patterns.</summary>
    private void DrawProgress()
    {
        _stats ??= FocusStats.Load();
        var p = Progress.Compute(ResolvedTasks.All, _stats.AllDays(), _settings.GoalTasks, _settings.GoalFocusHours, DateTime.Today, _settings.CountWeekends);

        var panel = ((SolidColorBrush)FindResource("Panel")).Color;
        var tint = ((SolidColorBrush)FindResource("FocusTint")).Color;
        LevelCard.Background = new LinearGradientBrush(tint, panel, 0);
        LevelNumber.Text = (Array.IndexOf(Progress.Levels, p.CurrentLevel) + 1).ToString();
        LevelName.Text = p.CurrentLevel.Name;
        LevelPoints.Text = $"{p.Points:N0} points   +{p.PointsToday} today   +{p.PointsThisWeek:N0} this week";
        LevelBar.Value = p.LevelProgress;
        LevelNext.Text = p.NextLevel == null ? "Top level reached. Legendary." : $"{p.NextLevel.From - p.Points:N0} points to {p.NextLevel.Name}";
        StreakBig.Text = $"{p.CurrentStreak} 🔥";
        StreakBest.Text = $"Longest: {p.LongestStreak} days";
        LevelCard.ToolTip = $"Points: {Progress.PointsPerTask} per task finished, 1 per 5 minutes of focus with blocking, " +
                            $"{Progress.PointsPerGoalDay} for each day you meet your goal.";

        // Highlights: small tiles.
        StatsHighlights.Children.Clear();
        void Tile(string value, string label)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold });
            var l = new TextBlock { Text = label, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            l.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            stack.Children.Add(l);
            var tile = new Border { Width = 170, Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10), Child = stack };
            tile.SetResourceReference(Border.BackgroundProperty, "Panel2");
            StatsHighlights.Children.Add(tile);
        }
        Tile(p.TotalTasks.ToString("N0"), "tasks finished, all time");
        Tile($"{p.TotalFocusHours:N0} h", "focus with blocking, all time");
        Tile($"{p.AverageTasksPerWorkday:0.#}", "tasks on an average workday");
        Tile(p.GoalDaysThisMonth.ToString(), $"goal days in {DateTime.Today:MMMM}");
        if (p.BestTaskDay.Count > 0) Tile(p.BestTaskDay.Count.ToString(), $"most tasks in a day ({p.BestTaskDay.Day:MMM d})");
        if (p.BestFocusDay.Hours > 0) Tile($"{p.BestFocusDay.Hours:0.#} h", $"most focus in a day ({p.BestFocusDay.Day:MMM d})");
        if (p.BestWeek.Count > 0) Tile(p.BestWeek.Count.ToString(), $"best week (of {p.BestWeek.WeekOf:MMM d})");

        // Badges: earned in color, the rest faded with progress.
        StatsBadges.Children.Clear();
        foreach (var b in p.Badges)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = b.Icon, FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Segoe UI Emoji") });
            stack.Children.Add(new TextBlock { Text = b.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            var prog = new TextBlock { Text = b.ProgressText, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            prog.SetResourceReference(TextBlock.ForegroundProperty, b.Earned ? "BreakBrush" : "Muted");
            stack.Children.Add(prog);
            var badge = new Border
            {
                Width = 128, Padding = new Thickness(8, 12, 8, 10), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(12),
                Child = stack, Opacity = b.Earned ? 1 : 0.45, ToolTip = b.Description, BorderThickness = new Thickness(b.Earned ? 1.5 : 0)
            };
            badge.SetResourceReference(Border.BackgroundProperty, b.Earned ? "FocusTint" : "Panel2");
            badge.SetResourceReference(Border.BorderBrushProperty, "FocusBrush");
            StatsBadges.Children.Add(badge);
        }
        int earned = p.Badges.Count(b => b.Earned);

        // Patterns: plain-language insights.
        StatsPatterns.Children.Clear();
        void Line(string text)
        {
            var t = new TextBlock { Text = "•  " + text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
            StatsPatterns.Children.Add(t);
        }
        Line($"You've earned {earned} of {p.Badges.Count} badges.");
        if (p.BestWeekday.Length > 0) Line($"{p.BestWeekday} are your most productive day.");
        if (p.PeakHour.Length > 0) Line($"You finish the most tasks between {p.PeakHour}.");
        if (p.TopProjects.Count > 0) Line("Last 30 days by project: " + string.Join(", ", p.TopProjects.Select(x => $"{x.Name} {x.Count}")) + ".");
        if (p.TotalTasks < 10) Line("Patterns appear once you've finished 10 or more tasks.");
    }

    /// <summary>A simple bar chart: one bar per day, the value on hover, weekday letters underneath.</summary>
    private void DrawBars(Grid chart, List<DateTime> days, List<double> values, string colorKey, Func<double, string> label)
    {
        chart.Children.Clear();
        chart.ColumnDefinitions.Clear();
        double max = Math.Max(values.DefaultIfEmpty(0).Max(), 0.001);
        for (int i = 0; i < days.Count; i++)
        {
            chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new DockPanel { Margin = new Thickness(2, 0, 2, 0), ToolTip = $"{days[i]:ddd MMM d}: {label(values[i])}" };
            var day = new TextBlock { Text = days[i].ToString("ddd")[..1], FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center };
            day.SetResourceReference(TextBlock.ForegroundProperty, days[i] == DateTime.Today ? "Text" : "Muted");
            DockPanel.SetDock(day, Dock.Bottom);
            cell.Children.Add(day);
            var bar = new Border
            {
                Height = Math.Max(2, 120 * values[i] / max),
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(3, 3, 0, 0),
                Opacity = days[i].DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.5 : 1
            };
            bar.SetResourceReference(Border.BackgroundProperty, values[i] > 0 ? colorKey : "Panel3");
            cell.Children.Add(bar);
            Grid.SetColumn(cell, i);
            chart.Children.Add(cell);
        }
    }

    /// <summary>For each of the next 7 days: planned work against the focus sessions available.</summary>
    private void DrawWorkload()
    {
        StatsWorkload.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            var day = DateTime.Today.AddDays(i);
            var (blocks, start) = PlanFor(day);
            var now = DateTime.Now.TimeOfDay;
            var t = start;
            int sessions = 0;
            foreach (var b in blocks)
            {
                if (b.IsFocus && (i > 0 || t >= now)) sessions++;
                t += TimeSpan.FromMinutes(b.Minutes);
            }
            var placedIds = new HashSet<string>(blocks.SelectMany(b => b.Tasks).Select(x => x.BinId).Where(id => id.Length > 0));
            double placed = blocks.SelectMany(b => b.Tasks).Where(x => !x.Done).Sum(Size);
            double waiting = TaskBin.All.Where(x => !placedIds.Contains(x.Id) &&
                (x.PlannedDate == day || x.Due == day || (i == 0 && x.IsOverdue))).Sum(Size);
            double work = placed + waiting;
            bool over = work > sessions;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            var name = new TextBlock { Text = i == 0 ? "Today" : i == 1 ? "Tomorrow" : day.ToString("dddd"), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(name);

            double scale = Math.Max(Math.Max(sessions, work), 1);
            var track = new Grid { Height = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var capacity = new Border { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
            capacity.SetResourceReference(Border.BackgroundProperty, "Panel3");
            var load = new Border { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            load.SetResourceReference(Border.BackgroundProperty, over ? "PriorityHigh" : "BreakBrush");
            track.Children.Add(capacity);
            track.Children.Add(load);
            track.SizeChanged += (_, e) =>
            {
                capacity.Width = e.NewSize.Width * sessions / scale;
                load.Width = e.NewSize.Width * Math.Min(work, scale) / scale;
            };
            Grid.SetColumn(track, 1);
            row.Children.Add(track);

            var text = new TextBlock
            {
                Text = $"{Math.Ceiling(work):0} of {sessions} sessions planned" + (over ? ", too much" : ""),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, over ? "PriorityHigh" : "Muted");
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            StatsWorkload.Children.Add(row);
        }
    }

    // ---------- Automations ----------

    /// <summary>
    /// Runs the automations you've switched on, once a day (and the Waiting reminder once a day):
    /// overdue tasks become high priority, Doing goes back to To do overnight, and long waits get a reminder.
    /// </summary>
    private void RunAutomations()
    {
        string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_settings.LastAutomationDay == today) return;
        _settings.LastAutomationDay = today;
        SaveSettingsQuietly();

        bool changed = false;
        if (_settings.On("autoOverdueHigh"))
            foreach (var t in TaskBin.All.Where(t => t.IsOverdue && !t.IsHigh)) { t.Priority = TaskPriority.High; changed = true; }
        if (_settings.On("autoDoingReset"))
            foreach (var t in TaskBin.All.Where(t => t.Status == "doing")) { t.Status = ""; changed = true; }
        if (changed) TaskBin.Save();

        if (_settings.On("autoWaiting"))
        {
            var stale = TaskBin.All.Where(t => t.Status == "waiting" && t.WaitingSince is DateTime since &&
                                              (DateTime.Today - since.Date).TotalDays >= _settings.WaitingReminderDays).ToList();
            if (stale.Count > 0)
                _tray.ShowBalloonTip(15000, "Still waiting",
                    $"{string.Join(", ", stale.Take(3).Select(t => t.Text))}{(stale.Count > 3 ? $" and {stale.Count - 3} more" : "")} " +
                    $"{(stale.Count == 1 ? "has" : "have")} been waiting {_settings.WaitingReminderDays}+ days. Time to follow up?",
                    System.Windows.Forms.ToolTipIcon.None);
        }
        RefreshTasksTab();
    }

    // ---------- Shrinking the sidebar to icons ----------

    private readonly Dictionary<RadioButton, object?> _navLabels = new();

    private void CollapseSidebar_Click(object sender, RoutedEventArgs e) => SetSidebarCollapsed(!_settings.SidebarCollapsed);

    /// <summary>Collapsed: just the icons (names show when you hover). Expanded: icons and names.</summary>
    private void SetSidebarCollapsed(bool collapsed)
    {
        _settings.SidebarCollapsed = collapsed;
        SaveSettingsQuietly();
        var navs = new[] { NavHome, NavDashboard, NavPlan, NavCalendar, NavTasks, NavStats, NavLists, NavPomodoro, NavFocus, NavSettings };
        foreach (var nav in navs)
        {
            if (!_navLabels.ContainsKey(nav)) _navLabels[nav] = nav.Content;
            nav.Content = collapsed ? null : _navLabels[nav];
            nav.ToolTip = collapsed ? _navLabels[nav] : null;
        }
        SidebarBorder.Width = collapsed ? 60 : 232;
        SidebarBorder.Padding = collapsed ? new Thickness(8, 18, 8, 14) : new Thickness(12, 18, 12, 14);
        SidebarColumn.Width = new GridLength(collapsed ? 80 : 252);
        SidebarTitle.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        AddTaskLabel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        NoticeLabel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        GoalWidget.Visibility = collapsed || !_settings.On("goals") ? Visibility.Collapsed : Visibility.Visible;
        CollapseBtn.Content = collapsed ? "\uE76C" : "\uE76B"; // arrow right / arrow left
        CollapseBtn.ToolTip = collapsed ? "Show names (Ctrl+\\)" : "Shrink the sidebar to icons (Ctrl+\\)";
        // Collapsed, the logo makes room for the button underneath it.
        DockPanel.SetDock(CollapseBtn, collapsed ? Dock.Bottom : Dock.Right);
    }
}
