using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// Update 5: working with tasks (selecting several, renaming in place, dragging, right-click menus),
/// the project and category editors, the newer settings, daily records, export and print,
/// keyboard shortcuts, redo, and the welcome tour.
/// </summary>
public partial class MainWindow
{
    // ======================================================================================
    // Lists that refresh often only change when their contents do (a click in progress is
    // never interrupted by the list being rebuilt underneath it).
    // ======================================================================================

    private static void SetItemsIfChanged<T>(ItemsControl list, List<T> items) where T : class
    {
        if (list.ItemsSource is List<T> old && old.Count == items.Count &&
            old.Zip(items, (a, b) => ReferenceEquals(a, b)).All(same => same))
            return;
        list.ItemsSource = items;
    }

    // ======================================================================================
    // Click to open, double-click to rename
    // ======================================================================================

    private System.Windows.Threading.DispatcherTimer? _openTimer;

    /// <summary>Opens something after a short pause, unless a double-click (rename) comes first.</summary>
    private void DelayOpen(Action open)
    {
        CancelPendingOpen();
        _openTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _openTimer.Tick += (_, _) =>
        {
            CancelPendingOpen();
            try { open(); }
            catch (Exception ex) { Log.Error("DelayOpen", ex); }
        };
        _openTimer.Start();
    }

    private void CancelPendingOpen()
    {
        _openTimer?.Stop();
        _openTimer = null;
    }

    private TextBox? _renaming;

    /// <summary>Turns a task's title into a text box right where it is (double-click).</summary>
    private void StartRename(TaskBase task, FrameworkElement? source)
    {
        FinishRename(keep: true);
        var panel = source as Panel;
        var box = panel?.Children.OfType<TextBox>().FirstOrDefault(b => b.Name == "RenameBox");
        var title = panel?.Children.OfType<TextBlock>().FirstOrDefault(b => b.Name == "Title");
        if (box == null || title == null) return;
        title.Visibility = Visibility.Collapsed;
        box.Visibility = Visibility.Visible;
        _renaming = box;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            box.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; FinishRename(keep: true); }
        else if (e.Key == Key.Escape) { e.Handled = true; FinishRename(keep: false); }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _renaming)) FinishRename(keep: true);
    }

    private void FinishRename(bool keep)
    {
        var box = _renaming;
        if (box == null) return;
        _renaming = null;
        var binding = box.GetBindingExpression(TextBox.TextProperty);
        if (keep && box.Text.Trim().Length > 0)
        {
            PushUndo(includePlan: true);
            binding?.UpdateSource();
            TaskBin.Save();
            if (_runner.IsRunning) SaveRunState();
        }
        else binding?.UpdateTarget();
        box.Visibility = Visibility.Collapsed;
        if (box.Parent is Panel panel)
            foreach (var t in panel.Children.OfType<TextBlock>().Where(t => t.Name == "Title")) t.Visibility = Visibility.Visible;
        RefreshTasksTab();
    }

    // ======================================================================================
    // Selecting several tasks (Ctrl+click, Shift+click) and acting on them together
    // ======================================================================================

    private readonly List<BinTask> _selection = new();
    private BinTask? _selectionAnchor;

    /// <summary>The tasks in the list as they're shown right now (sorted and filtered).</summary>
    private List<BinTask> VisibleBinTasks() =>
        _binView != null ? _binView.Cast<BinTask>().ToList() : TaskBin.All.ToList();

    /// <summary>Handles Ctrl+click and Shift+click. Returns true if the click was a selection click.</summary>
    private bool HandleSelectionClick(BinTask task)
    {
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control))
        {
            if (_selection.Remove(task)) task.IsSelected = false;
            else { _selection.Add(task); task.IsSelected = true; }
            _selectionAnchor = task;
            UpdateBulkBar();
            return true;
        }
        if (mods.HasFlag(ModifierKeys.Shift) && _selectionAnchor != null)
        {
            var visible = VisibleBinTasks();
            int a = visible.IndexOf(_selectionAnchor), b = visible.IndexOf(task);
            if (a >= 0 && b >= 0)
            {
                ClearSelection(keepAnchor: true);
                foreach (var t in visible.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1)) { _selection.Add(t); t.IsSelected = true; }
                UpdateBulkBar();
                return true;
            }
        }
        if (_selection.Count > 0) ClearSelection();
        _selectionAnchor = task;
        return false;
    }

    private void SelectOnly(BinTask task)
    {
        ClearSelection();
        _selection.Add(task);
        task.IsSelected = true;
        _selectionAnchor = task;
        UpdateBulkBar();
    }

    private void ClearSelection() => ClearSelection(keepAnchor: false);

    private void ClearSelection(bool keepAnchor)
    {
        foreach (var t in _selection) t.IsSelected = false;
        _selection.Clear();
        if (!keepAnchor) _selectionAnchor = null;
        UpdateBulkBar();
    }

    private void UpdateBulkBar()
    {
        if (BulkBar == null) return;
        BulkBar.Visibility = _selection.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BulkText.Text = $"{_selection.Count} selected";
    }

    private List<BinTask> SelectedStillInList() => _selection.Where(t => TaskBin.All.Contains(t)).ToList();

    private void BulkFinish_Click(object sender, RoutedEventArgs e)
    {
        var tasks = SelectedStillInList();
        if (tasks.Count == 0) return;
        PushUndo(includePlan: true);
        foreach (var t in tasks) FinishListTask(t, t.From, t.Spent);
        ClearSelection();
        _toast.ShowMessage($"{tasks.Count} tasks finished", "", "Undo", Undo);
    }

    private void BulkToday_Click(object sender, RoutedEventArgs e) => BulkPlan(DateTime.Today);
    private void BulkTomorrow_Click(object sender, RoutedEventArgs e) => BulkPlan(DateTime.Today.AddDays(1));

    private void BulkPlan(DateTime day)
    {
        var tasks = SelectedStillInList();
        if (tasks.Count == 0) return;
        PushUndo();
        foreach (var t in tasks) { t.PlannedFor = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); t.SnoozedUntil = null; }
        TaskBin.Save();
        RefreshTasksTab();
        _toast.ShowMessage($"{tasks.Count} tasks moved to {(day == DateTime.Today ? "today" : "tomorrow")}", "", "Undo", Undo);
    }

    private void BulkProject_Click(object sender, RoutedEventArgs e)
    {
        var tasks = SelectedStillInList();
        if (tasks.Count == 0) return;
        var menu = ProjectMenu(project => MoveToProject(tasks, project));
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private void BulkDelete_Click(object sender, RoutedEventArgs e)
    {
        var tasks = SelectedStillInList();
        if (tasks.Count == 0) return;
        PushUndo(includePlan: true);
        foreach (var t in tasks)
        {
            TaskBin.All.Remove(t);
            RemoveLinkedCopies(t.Id);
        }
        TaskBin.Save();
        ClearSelection();
        _toast.ShowMessage($"{tasks.Count} tasks deleted", "", "Undo", Undo);
    }

    private void BulkClear_Click(object sender, RoutedEventArgs e) => ClearSelection();

    /// <summary>A menu of projects (and "No project") that calls back with the choice.</summary>
    private ContextMenu ProjectMenu(Action<Project?> choose)
    {
        var menu = new ContextMenu();
        var none = new MenuItem { Header = "No project" };
        none.Click += (_, _) => choose(null);
        menu.Items.Add(none);
        foreach (var p in Projects.All)
        {
            var item = new MenuItem { Header = p.Name };
            var project = p;
            item.Click += (_, _) => choose(project);
            menu.Items.Add(item);
        }
        return menu;
    }

    private void MoveToProject(List<BinTask> tasks, Project? project)
    {
        PushUndo();
        foreach (var t in tasks) { t.Project = project?.Id ?? ""; t.Section = ""; }
        TaskBin.Save();
        RefreshTasksTab();
        _toast.ShowMessage(tasks.Count == 1 ? $"Moved to {project?.Name ?? "no project"}" : $"{tasks.Count} tasks moved to {project?.Name ?? "no project"}",
            "", "Undo", Undo);
    }

    // ======================================================================================
    // Dragging tasks: reorder the list, drop on a project, drop on a day in Upcoming
    // ======================================================================================

    private Point _binDown;
    private BinTask? _binPressed;

    private void BinRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _binPressed = (sender as FrameworkElement)?.DataContext as BinTask;
        _binDown = e.GetPosition(this);
    }

    private void BinRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (_binPressed == null || e.LeftButton != MouseButtonState.Pressed || _renaming != null) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _binDown.X) + Math.Abs(p.Y - _binDown.Y) < 8) return;
        var task = _binPressed;
        _binPressed = null;
        CancelPendingOpen();
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(BinTask), task), DragDropEffects.Move | DragDropEffects.Copy);
    }

    /// <summary>The tasks being dragged: all the selected ones if the dragged task is among them.</summary>
    private List<BinTask> DraggedTasks(DragEventArgs e)
    {
        if (e.Data.GetData(typeof(BinTask)) is not BinTask task || !TaskBin.All.Contains(task)) return new();
        return task.IsSelected && _selection.Count > 1 ? SelectedStillInList() : new List<BinTask> { task };
    }

    private void BinRow_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not BinTask target) return;
        var moving = DraggedTasks(e);
        if (moving.Count == 0 || moving.Contains(target)) return;
        if (SmartOrderCheck.IsChecked == true)
        {
            _toast.ShowMessage("Arranged by importance", "Turn off \"Most important first\" in the Show list to put tasks in your own order.");
            return;
        }
        PushUndo();
        foreach (var t in moving) TaskBin.All.Remove(t);
        int at = TaskBin.All.IndexOf(target);
        for (int i = 0; i < moving.Count; i++) TaskBin.All.Insert(at + i, moving[i]);
        TaskBin.Save();
        RefreshTasksTab();
    }

    private void ProjectRow_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not Project project) return;
        var moving = DraggedTasks(e);
        if (moving.Count > 0) MoveToProject(moving, project);
    }

    private void UpcomingHeader_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not UpcomingDay day || day.Date is not DateTime date) return;
        var moving = DraggedTasks(e);
        if (moving.Count == 0) return;
        PushUndo();
        foreach (var t in moving)
        {
            if (t.Due != null) t.Due = date;
            else t.PlannedFor = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            t.SnoozedUntil = null;
        }
        TaskBin.Save();
        RefreshTasksTab();
        _toast.ShowMessage($"Moved to {date:dddd, MMM d}", "", "Undo", Undo);
    }

    // ======================================================================================
    // The Day plan's task list: finish and open
    // ======================================================================================

    private void PlanTaskCheck_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlanTaskRow row) return;
        PushUndo(includePlan: true);
        FinishListTask(row.Task, row.Task.From, row.Task.Spent);
        _toast.ShowMessage("Task finished", row.Task.Text, "Undo", Undo);
    }

    private void PlanTaskText_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlanTaskRow row) return;
        if (e.ClickCount >= 2) { CancelPendingOpen(); StartRename(row.Task, sender as FrameworkElement); return; }
        if (!_settings.On("taskDetails")) return;
        var anchor = (UIElement)sender;
        DelayOpen(() => OpenBinTaskDetails(anchor, row.Task));
    }

    // ======================================================================================
    // Right-click menus on tasks (the same choices everywhere)
    // ======================================================================================

    private void TaskRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var context = (sender as FrameworkElement)?.DataContext;
        var task = context switch
        {
            BinTask b => b,
            PlanTaskRow r => r.Task,
            TaskItem i => FindLinked(i),
            _ => null
        };
        var menu = new ContextMenu();
        void Add(string text, Action act, bool enabled = true)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            item.Click += (_, _) => { try { act(); } catch (Exception ex) { Log.Error("TaskMenu", ex); } };
            menu.Items.Add(item);
        }

        if (task == null && context is TaskItem sessionTask)
        {
            // A task typed straight into a session.
            Add(sessionTask.Done ? "Mark not done" : "Mark done", () => { sessionTask.Done = !sessionTask.Done; if (_runner.IsRunning) SaveRunState(); });
            Add("Remove from this session", () =>
            {
                var block = _plan.Concat(_runner.Blocks).FirstOrDefault(b => b.Tasks.Contains(sessionTask));
                if (block != null) SetTaskAside(block, sessionTask);
            });
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
            return;
        }
        if (task == null) return;

        var group = task.IsSelected && _selection.Count > 1 ? SelectedStillInList() : new List<BinTask> { task };
        string several = group.Count > 1 ? $" ({group.Count} tasks)" : "";
        Add("Finish" + several, () =>
        {
            PushUndo(includePlan: true);
            foreach (var t in group) FinishListTask(t, t.From, t.Spent);
            ClearSelection();
            _toast.ShowMessage(group.Count == 1 ? "Task finished" : $"{group.Count} tasks finished", group.Count == 1 ? task.Text : "", "Undo", Undo);
        });
        if (group.Count == 1) Add("Edit...", () => OpenBinTaskDetails((UIElement)sender, task));
        if (group.Count == 1) Add("Rename", () => StartRename(task, FindTitlePanel(sender as DependencyObject)));
        menu.Items.Add(new Separator());
        Add("Do it today" + several, () => { foreach (var t in group) t.PlannedFor = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); SaveListChange(); });
        Add("Do it tomorrow" + several, () => { foreach (var t in group) t.PlannedFor = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); SaveListChange(); });
        if (_settings.On("upcoming"))
        {
            Add("Snooze until tomorrow", () => { foreach (var t in group) t.SnoozedUntil = DateTime.Today.AddDays(1); SaveListChange(); });
            Add("Snooze until next week", () => { foreach (var t in group) t.SnoozedUntil = DateTime.Today.AddDays(7 - ((int)DateTime.Today.DayOfWeek + 6) % 7); SaveListChange(); });
        }
        if (_settings.On("projects") && Projects.All.Count > 0)
        {
            var move = new MenuItem { Header = "Move to project" };
            var source = ProjectMenu(p => MoveToProject(group, p));
            foreach (var choice in source.Items.OfType<MenuItem>().ToList())
            {
                source.Items.Remove(choice); // a menu item can only live in one menu
                move.Items.Add(choice);
            }
            menu.Items.Add(move);
        }
        menu.Items.Add(new Separator());
        Add("Delete" + several, () =>
        {
            PushUndo(includePlan: true);
            foreach (var t in group) { TaskBin.All.Remove(t); RemoveLinkedCopies(t.Id); }
            TaskBin.Save();
            ClearSelection();
            _toast.ShowMessage(group.Count == 1 ? "Task deleted" : $"{group.Count} tasks deleted", group.Count == 1 ? task.Text : "", "Undo", Undo);
        });
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;

        void SaveListChange()
        {
            PushUndo();
            TaskBin.Save();
            RefreshTasksTab();
        }
    }

    /// <summary>The panel holding a task's title (for renaming from the right-click menu).</summary>
    private static FrameworkElement? FindTitlePanel(DependencyObject? from)
    {
        var queue = new Queue<DependencyObject>();
        if (from != null) queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is Panel p && p.Children.OfType<TextBox>().Any(b => b.Name == "RenameBox")) return p;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }

    // ======================================================================================
    // Editing a project: name, color, shortcuts, sections
    // ======================================================================================

    private Project? _editingProject;
    private string _editingColor = "";

    private void ProjectRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Project project) return;
        _editingProject = project;
        _editingColor = project.Color;
        ProjectNameBox.Text = project.Name;
        ProjectAliasBox.Text = string.Join(", ", project.Aliases);
        ProjectSectionsBox.Text = string.Join(Environment.NewLine, project.Sections.Select(s => s.Name));
        ProjectNote.Text = "";
        DrawProjectColors();
        ProjectPopup.PlacementTarget = (UIElement)sender;
        ProjectPopup.IsOpen = true;
    }

    private void DrawProjectColors()
    {
        ProjectColors.Children.Clear();
        foreach (var color in Projects.Palette)
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
            var dot = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = brush, Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand, BorderThickness = new Thickness(color.Equals(_editingColor, StringComparison.OrdinalIgnoreCase) ? 3 : 0)
            };
            dot.SetResourceReference(Border.BorderBrushProperty, "Text");
            string chosen = color;
            dot.MouseLeftButtonUp += (_, _) => { _editingColor = chosen; DrawProjectColors(); };
            ProjectColors.Children.Add(dot);
        }
    }

    private void ProjectSave_Click(object sender, RoutedEventArgs e)
    {
        var project = _editingProject;
        if (project == null) return;
        string name = ProjectNameBox.Text.Trim();
        if (name.Length == 0) { ProjectNote.Text = "The project needs a name."; return; }
        if (Projects.All.Any(p => p != project && p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            ProjectNote.Text = $"There's already a project called {name}.";
            return;
        }
        var aliases = ProjectAliasBox.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim().TrimStart('#')).Where(a => a.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var alias in aliases)
        {
            var other = Projects.All.FirstOrDefault(p => p != project &&
                (p.Name.Equals(alias, StringComparison.CurrentCultureIgnoreCase) || p.Aliases.Any(a => a.Equals(alias, StringComparison.CurrentCultureIgnoreCase))));
            if (other != null) { ProjectNote.Text = $"#{alias} is already used by {other.Name}."; return; }
        }

        // Sections: keep matching names, treat changed lines as renames, and take tasks out of removed ones.
        var lines = ProjectSectionsBox.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var oldSections = project.Sections.ToList();
        var result = new List<ProjectSection>();
        var unmatchedOld = oldSections.Where(s => !lines.Any(l => l.Equals(s.Name, StringComparison.CurrentCultureIgnoreCase))).ToList();
        foreach (var line in lines)
        {
            var same = oldSections.FirstOrDefault(s => s.Name.Equals(line, StringComparison.CurrentCultureIgnoreCase));
            if (same != null) { same.Name = line; result.Add(same); continue; }
            if (unmatchedOld.Count > 0) { var renamed = unmatchedOld[0]; unmatchedOld.RemoveAt(0); renamed.Name = line; result.Add(renamed); }
            else result.Add(new ProjectSection { Name = line });
        }
        var removedIds = new HashSet<string>(unmatchedOld.Select(s => s.Id));

        PushUndo();
        project.Name = name;
        project.Color = _editingColor;
        project.Aliases = aliases;
        project.Sections = result;
        foreach (var t in TaskBin.All.Where(t => t.Project == project.Id && removedIds.Contains(t.Section))) t.Section = "";
        Projects.Save();
        TaskBin.Save();
        ProjectPopup.IsOpen = false;
        RefreshProjects();
        RefreshTasksTab();
    }

    private void ProjectDelete_Click(object sender, RoutedEventArgs e)
    {
        var project = _editingProject;
        if (project == null) return;
        if (!Ask($"Delete the project \"{project.Name}\"? Its tasks stay in your list, without a project.", "Delete project", defaultNo: true)) return;
        PushUndo();
        foreach (var t in TaskBin.All.Where(t => t.Project == project.Id)) { t.Project = ""; t.Section = ""; }
        TaskBin.Save();
        Projects.All.Remove(project);
        Projects.Save();
        ProjectPopup.IsOpen = false;
    }

    // ======================================================================================
    // Settings added in updates 4 and 5: websites, meetings, weekends, themes, daily records
    // ======================================================================================

    private void FillNewerSettings()
    {
        WebModeBlock.IsChecked = _settings.WebMode == "block";
        WebModeAllow.IsChecked = _settings.WebMode == "allow";
        WebWhenFocus.IsChecked = _settings.WebWhen == "focus";
        WebWhenDay.IsChecked = _settings.WebWhen == "day";
        WebWhenAlways.IsChecked = _settings.WebWhen == "always";
        BuildCategoryChecks();
        MeetAsk.IsChecked = _settings.NewMeetings == "ask";
        MeetFit.IsChecked = _settings.NewMeetings == "fit";
        MeetOverlay.IsChecked = _settings.NewMeetings == "overlay";
        CountWeekendsCheck.IsChecked = _settings.CountWeekends;
        BuildThemeCards();
        UpdateRecordsInfo();
    }

    private void SaveNewerSettings()
    {
        _settings.WebMode = WebModeAllow.IsChecked == true ? "allow" : "block";
        _settings.WebWhen = WebWhenAlways.IsChecked == true ? "always" : WebWhenDay.IsChecked == true ? "day" : "focus";
        _settings.AllowedSites = _editAllowedSites.Select(SiteRules.Clean).Where(s => s.Contains('.')).Distinct().ToList();
        _settings.WebCategories = _categoryChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        _settings.NewMeetings = MeetFit.IsChecked == true ? "fit" : MeetOverlay.IsChecked == true ? "overlay" : "ask";
        _settings.CountWeekends = CountWeekendsCheck.IsChecked == true;
    }

    private readonly Dictionary<string, CheckBox> _categoryChecks = new();
    private string _editingCategory = "";

    private void BuildCategoryChecks()
    {
        WebCategoryChecks.Children.Clear();
        _categoryChecks.Clear();
        foreach (var category in SiteRules.Categories.Keys)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var edit = new Button { Content = "Edit sites", Margin = new Thickness(8, 0, 0, 0) };
            edit.SetResourceReference(StyleProperty, "LinkButton");
            string cat = category;
            edit.Click += (_, _) => OpenCategoryEditor(cat, edit);
            DockPanel.SetDock(edit, Dock.Right);
            row.Children.Add(edit);
            int count = SiteRules.SitesIn(category, _settings).Count();
            var check = new CheckBox { Content = $"{category} ({count} sites)", IsChecked = _settings.WebCategories.Contains(category), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(check);
            _categoryChecks[category] = check;
            WebCategoryChecks.Children.Add(row);
        }
    }

    private void OpenCategoryEditor(string category, UIElement anchor)
    {
        _editingCategory = category;
        CategoryTitle.Text = category;
        CategorySitesBox.Text = string.Join(Environment.NewLine, SiteRules.SitesIn(category, _settings));
        CategoryPopup.PlacementTarget = anchor;
        CategoryPopup.IsOpen = true;
    }

    private void CategorySave_Click(object sender, RoutedEventArgs e)
    {
        if (_editingCategory.Length == 0) return;
        _settings.WebCategorySites[_editingCategory] =
            CategorySitesBox.Text.Split('\n').Select(SiteRules.Clean).Where(s => s.Length > 0).Distinct().ToList();
        SaveSettingsQuietly();
        CategoryPopup.IsOpen = false;
        BuildCategoryChecks();
    }

    private void CategoryReset_Click(object sender, RoutedEventArgs e)
    {
        if (_editingCategory.Length == 0) return;
        _settings.WebCategorySites.Remove(_editingCategory);
        SaveSettingsQuietly();
        CategorySitesBox.Text = string.Join(Environment.NewLine, SiteRules.SitesIn(_editingCategory, _settings));
        BuildCategoryChecks();
    }

    // ---------- Themes ----------

    private static readonly (string Key, string Name, string[] Colors)[] ColorThemes =
    {
        ("Classic", "Classic", new[] { "#DC4C3E", "#058527", "#246FE0", "#FAF9F7" }),
        ("Mono", "Monochrome", new[] { "#1E1E1E", "#8A8A8A", "#D4D4D4", "#FFFFFF" }),
        ("Calm", "Calm", new[] { "#4E8C86", "#8DB596", "#7A9CC6", "#F5F3EE" })
    };

    private void BuildThemeCards()
    {
        ThemeCards.Children.Clear();
        foreach (var (key, name, colors) in ColorThemes)
        {
            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var c in colors)
            {
                var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 4, 0),
                    Background = (Brush)new BrushConverter().ConvertFromString(c)!, BorderThickness = new Thickness(1) };
                dot.SetResourceReference(Border.BorderBrushProperty, "Line");
                swatches.Children.Add(dot);
            }
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
            stack.Children.Add(swatches);
            bool chosen = _settings.ColorTheme == key;
            var card = new Border { Child = stack, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(chosen ? 2 : 1), Cursor = Cursors.Hand, ToolTip = chosen ? "Your theme" : $"Use the {name} theme" };
            card.SetResourceReference(Border.BackgroundProperty, "Panel2");
            card.SetResourceReference(Border.BorderBrushProperty, chosen ? "FocusBrush" : "Line");
            string k = key;
            card.MouseLeftButtonUp += (_, _) => SetColorTheme(k);
            ThemeCards.Children.Add(card);
        }
    }

    private void SetColorTheme(string key)
    {
        _settings.ColorTheme = key;
        ThemeManager.ColorTheme = key;
        ThemeManager.Apply(_settings.Theme);
        SaveSettingsQuietly();
        BuildThemeCards();
        RefreshAllTimelines();
        RefreshHome();
    }

    // ======================================================================================
    // Daily records: a Markdown file per day, in the Obsidian vault if you chose one
    // (falling back to the Focus folder whenever the vault can't be reached)
    // ======================================================================================

    private string FocusRecordsFolder(DateTime day) =>
        Path.Combine(Storage.SharedFolder, "Daily records", day.Year.ToString(CultureInfo.InvariantCulture));

    private DateTime _vaultCheckedAt = DateTime.MinValue;
    private bool _vaultOk, _vaultWasDown;

    /// <summary>True if the chosen vault folder is there and can be written to (checked at most once a minute).</summary>
    private bool VaultAvailable()
    {
        if (_settings.VaultFolder.Length == 0) return false;
        if (DateTime.Now - _vaultCheckedAt < TimeSpan.FromMinutes(1)) return _vaultOk;
        _vaultCheckedAt = DateTime.Now;
        try
        {
            if (!Directory.Exists(_settings.VaultFolder)) return _vaultOk = false;
            string probe = Path.Combine(_settings.VaultFolder, ".focus-write-check");
            File.WriteAllText(probe, DateTime.Now.ToString("O"));
            File.Delete(probe);
            return _vaultOk = true;
        }
        catch
        {
            return _vaultOk = false;
        }
    }

    private void UpdateRecordsInfo()
    {
        if (RecordsInfo == null) return;
        bool vault = _settings.VaultFolder.Length > 0;
        RecordsInfo.Text = !_settings.On("dailyRecords")
            ? "Daily records are switched off (Settings, Features)."
            : vault
                ? (VaultAvailable() ? $"Writing a file for each day into your Obsidian folder: {_settings.VaultFolder}"
                                    : $"Your Obsidian folder can't be reached right now, so records go to your Focus folder until it's back: {_settings.VaultFolder}")
                : $"Writing a file for each day into your Focus folder: {Path.Combine(Storage.SharedFolder, "Daily records")}";
        ClearVaultBtn.Visibility = vault ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChooseVault_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose a folder in your Obsidian vault for Focus's daily records (for example YourVault\\Focus)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (_settings.VaultFolder.Length > 0 && Directory.Exists(_settings.VaultFolder)) dialog.SelectedPath = _settings.VaultFolder;
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        _settings.VaultFolder = dialog.SelectedPath;
        _vaultCheckedAt = DateTime.MinValue;
        SaveSettingsQuietly();
        WriteDailyRecord(DateTime.Today);
        BackfillDailyRecords();
        UpdateRecordsInfo();
    }

    private void ClearVault_Click(object sender, RoutedEventArgs e)
    {
        _settings.VaultFolder = "";
        _settings.RecordFallbackDays.Clear();
        SaveSettingsQuietly();
        WriteDailyRecord(DateTime.Today);
        UpdateRecordsInfo();
    }

    private void OpenRecords_Click(object sender, RoutedEventArgs e)
    {
        string folder = _settings.VaultFolder.Length > 0 && VaultAvailable() ? _settings.VaultFolder : Path.Combine(Storage.SharedFolder, "Daily records");
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("OpenRecords", ex);
        }
    }

    /// <summary>History used by the records: tasks finished per day and focus minutes per day.</summary>
    private (Dictionary<DateTime, int> Tasks, Dictionary<DateTime, double> Focus) RecordHistory()
    {
        _stats ??= FocusStats.Load();
        return (ResolvedTasks.All.GroupBy(t => t.ResolvedAt.Date).ToDictionary(g => g.Key, g => g.Count()), _stats.AllDays());
    }

    private DailyRecords.DayData DayDataFor(DateTime day, (Dictionary<DateTime, int> Tasks, Dictionary<DateTime, double> Focus) history)
    {
        bool Weekend(DateTime d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        bool Met(DateTime d) => history.Tasks.GetValueOrDefault(d) >= _settings.GoalTasks ||
                                history.Focus.GetValueOrDefault(d) >= _settings.GoalFocusHours * 60;
        int streak = 0;
        for (var d = day; d > day.AddDays(-1000); d = d.AddDays(-1))
        {
            if (Weekend(d) && !_settings.CountWeekends) continue;
            if (Met(d)) streak++;
            else if (d == day) continue;
            else if (!Weekend(d)) break;
        }
        int points = history.Tasks.GetValueOrDefault(day) * Progress.PointsPerTask +
                     (int)(history.Focus.GetValueOrDefault(day) * Progress.PointsPerFocusMinute) + (Met(day) ? Progress.PointsPerGoalDay : 0);

        // The plan for that day, with start times.
        var plan = new List<(TimeSpan, PlanBlock)>();
        IEnumerable<PlanBlock>? blocks = null;
        TimeSpan start = TimeSpan.Zero;
        if (day == DateTime.Today && _mode == RunMode.DayPlan && _runner.IsRunning && _runner.ScheduleStartLocal is DateTime runStart)
        {
            blocks = _runner.Blocks;
            start = runStart.TimeOfDay;
        }
        else if (TryLoadPlanFile(day, out var file))
        {
            blocks = file.Blocks;
            start = TryParseTime(file.DayStart, out var s) ? s : _settings.WorkdayStartTime;
        }
        if (blocks != null)
            foreach (var b in blocks) { plan.Add((start, b)); start += TimeSpan.FromMinutes(b.Minutes); }

        var meetings = new List<SharedEvent>();
        try
        {
            foreach (var ev in CalendarService.EventsOn(day))
                meetings.Add(new SharedEvent { Title = ev.Title, Start = ev.Start, End = ev.End, AllDay = ev.AllDay, Busy = ev.Busy });
        }
        catch { /* no calendar connected */ }

        return new DailyRecords.DayData(
            day,
            history.Focus.GetValueOrDefault(day),
            ResolvedTasks.All.Where(t => t.ResolvedAt.Date == day).OrderBy(t => t.ResolvedAt).ToList(),
            TaskBin.All.Where(t => t.CreatedAt?.Date == day).Select(t => t.Text).ToList(),
            new List<BlockLogEntry>(),
            plan,
            meetings,
            Met(day), streak, points, _settings.GoalTasks, _settings.GoalFocusHours);
    }

    /// <summary>Writes (or updates) one day's record. Your "My notes" section is always kept.</summary>
    private void WriteDailyRecord(DateTime day) => WriteDailyRecord(day, RecordHistory(), onlyIfMissing: false);

    private void WriteDailyRecord(DateTime day, (Dictionary<DateTime, int>, Dictionary<DateTime, double>) history, bool onlyIfMissing)
    {
        _lastRecordWrite = DateTime.Now;
        if (!_settings.On("dailyRecords")) return;
        try
        {
            bool wantVault = _settings.VaultFolder.Length > 0;
            bool vault = wantVault && VaultAvailable();
            string folder = vault ? _settings.VaultFolder : FocusRecordsFolder(day);
            string path = Path.Combine(folder, DailyRecords.FileName(day));
            if (onlyIfMissing && File.Exists(path)) return;

            if (wantVault && !vault)
            {
                string key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!_settings.RecordFallbackDays.Contains(key)) { _settings.RecordFallbackDays.Add(key); SaveSettingsQuietly(); }
                if (!_vaultWasDown)
                {
                    _vaultWasDown = true;
                    _toast.ShowMessage("Obsidian folder unavailable", "Daily records go to your Focus folder until it's back.");
                }
            }

            Directory.CreateDirectory(folder);
            string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
            string content = DailyRecords.Build(DayDataFor(day, history), existing);
            if (content != existing) File.WriteAllText(path, content, new UTF8Encoding(false));
            if (vault) CatchUpVault();
        }
        catch (Exception ex)
        {
            Log.Error("WriteDailyRecord", ex);
        }
    }

    /// <summary>Once the vault is back, copies over the days written to the Focus folder meanwhile.</summary>
    private void CatchUpVault()
    {
        if (_settings.RecordFallbackDays.Count == 0) { _vaultWasDown = false; return; }
        int copied = 0;
        foreach (var key in _settings.RecordFallbackDays.ToList())
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            string from = Path.Combine(FocusRecordsFolder(day), DailyRecords.FileName(day));
            string to = Path.Combine(_settings.VaultFolder, DailyRecords.FileName(day));
            try
            {
                if (File.Exists(from) && (!File.Exists(to) || File.GetLastWriteTimeUtc(from) > File.GetLastWriteTimeUtc(to)))
                {
                    // Keep any notes already written in the vault copy.
                    string text = File.ReadAllText(from);
                    if (File.Exists(to))
                    {
                        string vaultNotes = DailyRecords.ExtractNotes(File.ReadAllText(to));
                        if (vaultNotes.Trim().Length > 0) text = DailyRecords.Build(DayDataFor(day, RecordHistory()), File.ReadAllText(to));
                    }
                    File.WriteAllText(to, text, new UTF8Encoding(false));
                    copied++;
                }
                _settings.RecordFallbackDays.Remove(key);
            }
            catch (Exception ex)
            {
                Log.Error("CatchUpVault", ex);
            }
        }
        SaveSettingsQuietly();
        if (_vaultWasDown || copied > 0)
            _toast.ShowMessage("Daily records are back in Obsidian", copied > 0 ? $"Copied {copied} day{(copied == 1 ? "" : "s")} over from your Focus folder." : "");
        _vaultWasDown = false;
    }

    /// <summary>Writes records for past days that don't have one yet, a few at a time so Focus stays responsive.</summary>
    private void BackfillDailyRecords()
    {
        if (!_settings.On("dailyRecords")) return;
        var history = RecordHistory();
        var days = history.Item1.Keys.Concat(history.Item2.Where(kv => kv.Value > 0).Select(kv => kv.Key))
            .Where(d => d < DateTime.Today && d > DateTime.Today.AddDays(-730)).Distinct().OrderByDescending(d => d).ToList();
        void Next(int from)
        {
            foreach (var day in days.Skip(from).Take(15)) WriteDailyRecord(day, history, onlyIfMissing: true);
            if (from + 15 < days.Count)
                Dispatcher.BeginInvoke(new Action(() => Next(from + 15)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        Next(0);
    }

    // ======================================================================================
    // Export and print: the same choices from the Tasks tab and the dashboard
    // ======================================================================================

    private sealed class ExportItem
    {
        public string Text = "", Detail = "", Due = "", Priority = "", Project = "", Tags = "", Notes = "";
        public bool Done;
        public DateTime? DueDate;
        public List<(string Text, bool Done)> Steps = new();
    }

    private sealed class ExportGroup
    {
        public string Header = "";
        public List<ExportItem> Items = new();
    }

    private static ExportItem ItemOf(TaskBase t) => new()
    {
        Text = t.Text, Detail = t.Details, Done = t is TaskItem ti && ti.Done,
        Due = t.Due?.ToString("d") ?? "", DueDate = t.Due, Priority = t.Priority.ToString(), Project = t.ProjectName,
        Tags = string.Join(" ", t.Tags.Select(x => "#" + x)), Notes = t.Notes,
        Steps = t.Subtasks.Select(s => (s.Text, s.Done)).ToList()
    };

    private static ExportItem ItemOf(ResolvedTask r) => new() { Text = r.Text, Done = true, Detail = r.Took, Project = Projects.Find(r.Project)?.Name ?? "" };

    private List<BinTask> ListTasks() => TaskBin.All.Where(t => !(t.IsSnoozed && _settings.On("upcoming"))).ToList();

    private static List<ExportGroup> One(string header, IEnumerable<TaskBase> tasks) =>
        new() { new ExportGroup { Header = header, Items = tasks.OrderBy(TaskBase.Rank).ThenBy(t => t.Due ?? DateTime.MaxValue).Select(ItemOf).ToList() } };

    /// <summary>Tasks grouped by the day they're for (due, picked, or snoozed until), between two dates.</summary>
    private List<ExportGroup> ByDay(DateTime from, DateTime to)
    {
        var groups = new List<ExportGroup>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var day = d;
            var items = TaskBin.All.Where(t => (t.Due == day || t.PlannedDate == day || (t.IsSnoozed && t.SnoozedUntil == day)) ||
                                               (day == DateTime.Today && t.IsOverdue))
                .OrderBy(TaskBase.Rank).Select(ItemOf).ToList();
            if (items.Count > 0) groups.Add(new ExportGroup { Header = day == DateTime.Today ? "Today" : day.ToString("dddd, MMMM d"), Items = items });
        }
        return groups;
    }

    private List<ExportGroup> TodaysPlanSheet()
    {
        var (blocks, start) = TodaysSchedule();
        var groups = new List<ExportGroup>();
        var t = start;
        foreach (var b in blocks)
        {
            var end = t + TimeSpan.FromMinutes(b.Minutes);
            groups.Add(new ExportGroup
            {
                Header = $"{DateTime.Today.Add(t):t} to {DateTime.Today.Add(end):t}   {b.Name}",
                Items = b.IsFocus ? b.Tasks.Where(x => x.Text.Trim().Length > 0).Select(ItemOf).ToList() : new()
            });
            t = end;
        }
        try
        {
            var meetings = CalendarService.EventsOn(DateTime.Today).Where(e => !e.AllDay).ToList();
            if (meetings.Count > 0)
                groups.Add(new ExportGroup { Header = "Meetings", Items = meetings.Select(m => new ExportItem { Text = $"{m.Start:t}  {m.Title}" }).ToList() });
        }
        catch { /* no calendar */ }
        return groups;
    }

    /// <summary>The lists you can print or export. Projects and tags come as sub-lists.</summary>
    private List<(string Title, Func<List<ExportGroup>>? Build, List<(string, Func<List<ExportGroup>>)>? Sub)> ExportChoices()
    {
        var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        var list = new List<(string, Func<List<ExportGroup>>?, List<(string, Func<List<ExportGroup>>)>?)>();
        if (_runner.Current is PlanBlock cur && cur.IsFocus)
            list.Add(("This session", () => One(cur.Name, cur.Tasks.Where(x => x.Text.Trim().Length > 0)), null));
        list.Add(("Today's plan (day sheet)", TodaysPlanSheet, null));
        list.Add(("Today", () => One("Today", ListTasks().Where(t => t.IsPlannedToday || t.IsDueToday || t.IsOverdue)), null));
        list.Add(("Tomorrow", () => ByDay(DateTime.Today.AddDays(1), DateTime.Today.AddDays(1)), null));
        list.Add(("This week", () => ByDay(DateTime.Today, monday.AddDays(6)), null));
        list.Add(("Upcoming (two weeks)", () => ByDay(DateTime.Today, DateTime.Today.AddDays(13)), null));
        list.Add(("Overdue", () => One("Overdue", ListTasks().Where(t => t.IsOverdue)), null));
        list.Add(("Undated", () => One("Undated", ListTasks().Where(t => t.IsUndated)), null));
        list.Add(("Waiting on", () => new List<ExportGroup> { new() { Header = "Waiting on",
            Items = ListTasks().Where(t => t.Status == "waiting").Select(t => { var i = ItemOf(t); i.Text = t.WaitingOn.Length > 0 ? $"{t.Text} (waiting on {t.WaitingOn})" : t.Text; return i; }).ToList() } }, null));
        list.Add(("All tasks", () => ListTasks().GroupBy(t => t.ProjectName).OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key)
            .Select(g => new ExportGroup { Header = g.Key.Length == 0 ? "No project" : g.Key, Items = g.OrderBy(TaskBase.Rank).Select(ItemOf).ToList() }).ToList(), null));
        if (Projects.All.Count > 0)
            list.Add(("Projects", null, Projects.All.Select(p => (p.Name, (Func<List<ExportGroup>>)(() =>
                ListTasks().Where(t => t.Project == p.Id).GroupBy(t => t.SectionName).OrderBy(g => g.Key.Length > 0).ThenBy(g => g.Key)
                    .Select(g => new ExportGroup { Header = g.Key.Length == 0 ? p.Name : $"{p.Name} / {g.Key}", Items = g.OrderBy(TaskBase.Rank).Select(ItemOf).ToList() })
                    .ToList()))).ToList()));
        var tags = TaskBin.All.SelectMany(t => t.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(t => t).ToList();
        if (tags.Count > 0)
            list.Add(("Tags", null, tags.Select(tag => ("@" + tag, (Func<List<ExportGroup>>)(() =>
                One("@" + tag, ListTasks().Where(t => t.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase)))))).ToList()));
        list.Add(("Done today", () => new List<ExportGroup> { new() { Header = "Done today",
            Items = ResolvedTasks.All.Where(r => r.ResolvedAt.Date == DateTime.Today).OrderBy(r => r.ResolvedAt).Select(ItemOf).ToList() } }, null));
        list.Add(("Done this week", () => ResolvedTasks.All.Where(r => r.ResolvedAt >= monday).GroupBy(r => r.ResolvedAt.Date).OrderBy(g => g.Key)
            .Select(g => new ExportGroup { Header = g.Key.ToString("dddd, MMMM d"), Items = g.OrderBy(r => r.ResolvedAt).Select(ItemOf).ToList() }).ToList(), null));
        return list;
    }

    /// <summary>What's on screen in the Tasks tab right now.</summary>
    private (string Title, Func<List<ExportGroup>> Build) CurrentTasksView()
    {
        if (BinDoneView.IsChecked == true)
            return ("Done", () => ResolvedTasks.All.GroupBy(r => r.ResolvedAt.Date).OrderByDescending(g => g.Key).Take(60)
                .Select(g => new ExportGroup { Header = g.Key.ToString("dddd, MMMM d"), Items = g.Select(ItemOf).ToList() }).ToList());
        if (BinUpcomingView.IsChecked == true)
            return ("Upcoming", () => (UpcomingList.ItemsSource as IEnumerable<UpcomingDay> ?? Enumerable.Empty<UpcomingDay>())
                .Select(g => new ExportGroup { Header = g.Header, Items = g.Items.Select(ItemOf).ToList() }).Where(g => g.Items.Count > 0).ToList());
        string label = BinUndatedView.IsChecked == true ? "Undated" : BinTodayView.IsChecked == true ? "Today" : BinTomorrowView.IsChecked == true ? "Tomorrow" : _binFilter == "all" ? "All tasks" : "Filtered tasks";
        return (label, () => new List<ExportGroup> { new() { Header = label, Items = VisibleBinTasks().Select(ItemOf).ToList() } });
    }

    private void Export_Click(object sender, RoutedEventArgs e) => ShowExportMenu((UIElement)sender, includeCurrentView: NavTasks.IsChecked == true);
    private void DashPrint_Click(object sender, RoutedEventArgs e) => ShowExportMenu((UIElement)sender, includeCurrentView: false);

    /// <summary>
    /// The export menu: click a list and it prints straight away (the print window also offers Save as
    /// PDF). Copying as text and saving a spreadsheet are at the bottom, with the same lists.
    /// </summary>
    private void ShowExportMenu(UIElement anchor, bool includeCurrentView)
    {
        var choices = new List<(string Title, Func<List<ExportGroup>>? Build, List<(string, Func<List<ExportGroup>>)>? Sub)>();
        if (includeCurrentView)
        {
            var (title, build) = CurrentTasksView();
            choices.Add(($"This view ({title})", build, null));
        }
        choices.AddRange(ExportChoices());

        // Every list prints straight away when clicked. Projects and tags are listed right in the menu
        // (under small headings) rather than in submenus, so every item is one click.
        var menu = new ContextMenu();
        MenuItem Leaf(string title, Func<List<ExportGroup>> build, string? header = null)
        {
            var item = new MenuItem { Header = header ?? title };
            item.Click += (_, e) =>
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { PrintGroups(title, build()); }
                    catch (Exception ex) { Log.Error("Export", ex); Info("Couldn't print: " + ex.Message, "Print"); }
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
            return item;
        }
        MenuItem Heading(string text) => new() { Header = text, IsEnabled = false, FontSize = 11, FontWeight = FontWeights.SemiBold };

        menu.Items.Add(Heading("Print a list (or save it as a PDF)"));
        for (int n = 0; n < choices.Count; n++)
        {
            var (title, build, sub) = choices[n];
            if (build != null) menu.Items.Add(Leaf(title, build));
            else if (sub != null && sub.Count > 0)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Heading(title));
                foreach (var (subTitle, subBuild) in sub) menu.Items.Add(Leaf(subTitle, subBuild, "    " + subTitle));
            }
            if (n == 0 && includeCurrentView) menu.Items.Add(new Separator());
        }

        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Prints a list of tasks as a clean page: the Focus logo, the list's name and today's date at the
    /// top, then each task with an open circle big enough to tick with a pencil, its details underneath
    /// in small gray type, steps as smaller circles, and a few ruled lines for notes at the end.
    /// </summary>
    private void PrintGroups(string title, List<ExportGroup> groups)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        try
        {
            var doc = BuildPrintDocument(title, groups, dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
            dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, "Focus: " + title);
        }
        catch (Exception ex)
        {
            Log.Error("PrintGroups", ex);
            Info("Couldn't print: " + ex.Message, "Print");
        }
    }

    private FlowDocument BuildPrintDocument(string title, List<ExportGroup> groups, double width, double height)
    {
        // Ink-friendly colors: near-black text, one accent (black in Monochrome), soft gray details.
        var ink = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        var gray = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
        var rule = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        // The printout follows your task look: Default (soft gray rows), Compact (tighter), Paper (ruled lines only).
        string look = TaskLook.Current;
        bool lines = look != "default";
        double gap = look == "compact" ? 4 : 8, circle = look == "compact" ? 14 : 17;
        Brush? fill = look == "default" ? new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF1)) : null;
        var cellLine = lines ? new Thickness(0, 0, 0, 0.75) : new Thickness(0, 0, 0, 3); // in Default, white gaps separate the gray rows
        Brush cellLineBrush = lines ? rule : Brushes.White;
        var accent = _settings.ColorTheme == "mono" ? ink
                   : _settings.ColorTheme == "calm" ? new SolidColorBrush(Color.FromRgb(0x4F, 0x8A, 0x86))
                   : new SolidColorBrush(Color.FromRgb(0xDC, 0x4C, 0x3E));
        double margin = 54; // three-quarters of an inch
        title = title.Replace(" (day sheet)", "");
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(margin), ColumnWidth = double.PositiveInfinity, ColumnGap = 0,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12, Foreground = ink,
            PageWidth = width, PageHeight = height
        };
        double content = Math.Max(300, width - 2 * margin);

        // ---- Header: logo and name on the left, the date on the right, then a thin accent line ----
        var header = new Grid { Width = content, Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(160, content - 46 - 170)) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        try
        {
            var logo = new System.Windows.Controls.Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")),
                Width = 34, Height = 34, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left
            };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            header.Children.Add(logo);
        }
        catch (Exception ex) { Log.Error("Print logo", ex); }
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "FOCUS", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = accent });
        names.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(names, 1);
        header.Children.Add(names);
        var when = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 2) };
        when.Children.Add(new TextBlock { Text = DateTime.Today.ToString("dddd"), FontSize = 10.5, Foreground = gray, HorizontalAlignment = HorizontalAlignment.Right });
        when.Children.Add(new TextBlock { Text = DateTime.Today.ToString("MMMM d, yyyy"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ink, HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(when, 2);
        header.Children.Add(when);
        doc.Blocks.Add(new BlockUIContainer(header) { Margin = new Thickness(0) });
        doc.Blocks.Add(new BlockUIContainer(new Border { Height = 2, Width = content, Background = accent }) { Margin = new Thickness(0, 6, 0, 6) });

        int total = groups.Sum(g => g.Items.Count), done = groups.Sum(g => g.Items.Count(x => x.Done));
        string summary = total == 0 ? "Nothing here right now." : total == 1 ? "1 task" : $"{total} tasks";
        if (done > 0) summary += $", {done} done";
        doc.Blocks.Add(new Paragraph(new Run(summary)) { FontSize = 10.5, Foreground = gray, Margin = new Thickness(0, 0, 0, 10) });

        // ---- The tasks ----
        // The day sheet keeps every session (empty ones get a line to write on); other lists skip empty groups.
        bool daySheet = title.StartsWith("Today's plan", StringComparison.OrdinalIgnoreCase);
        foreach (var g in groups.Where(g => g.Items.Count > 0 || groups.Count == 1 || daySheet))
        {
            bool showHeader = groups.Count > 1 || g.Header != title;
            if (showHeader)
            {
                var h = new Paragraph { Margin = new Thickness(0, 14, 0, 2), KeepWithNext = true };
                h.Inlines.Add(new Run(g.Header) { FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = ink });
                h.Inlines.Add(new Run($"   {g.Items.Count}") { FontSize = 10.5, Foreground = gray });
                doc.Blocks.Add(h);
                doc.Blocks.Add(new BlockUIContainer(new Border { Height = 1, Width = content, Background = rule }) { Margin = new Thickness(0, 0, 0, 2) });
            }

            var table = new Table { CellSpacing = 0, Margin = new Thickness(0) };
            // Exact widths from the page (a "fill the rest" column can collapse when printing).
            table.Columns.Add(new TableColumn { Width = new GridLength(30) });
            table.Columns.Add(new TableColumn { Width = new GridLength(Math.Max(160, content - 30 - 96)) });
            table.Columns.Add(new TableColumn { Width = new GridLength(96) });
            var rows = new TableRowGroup();
            table.RowGroups.Add(rows);
            foreach (var item in g.Items)
            {
                var row = new TableRow();
                if (fill != null) row.Background = fill;
                row.Cells.Add(new TableCell(new BlockUIContainer(Circle(circle, item.Done, ink, gray)) { Margin = new Thickness(fill != null ? 6 : 0, gap + 1, 0, 0) })
                    { BorderBrush = cellLineBrush, BorderThickness = cellLine });

                var text = new Paragraph { Margin = new Thickness(0, gap, 8, 0) };
                text.Inlines.Add(new Run(item.Text) { FontSize = 12.5, Foreground = item.Done ? gray : ink, TextDecorations = item.Done ? TextDecorations.Strikethrough : null });
                var cell = new TableCell(text) { BorderBrush = cellLineBrush, BorderThickness = cellLine, Padding = new Thickness(0, 0, 0, gap) };
                string detail = string.Join("   ", new[] { item.Project, item.Tags }.Where(x => x.Length > 0));
                if (detail.Length == 0 && item.Detail.Length > 0 && item.Due.Length == 0) detail = item.Detail;
                if (detail.Length > 0) cell.Blocks.Add(new Paragraph(new Run(detail)) { FontSize = 9.5, Foreground = gray, Margin = new Thickness(0, 1, 8, 0) });
                foreach (var (stepText, stepDone) in item.Steps)
                {
                    double stepWidth = Math.Max(140, content - 30 - 96 - 8);
                    var step = new Grid { Margin = new Thickness(0, 5, 0, 0), Width = stepWidth, HorizontalAlignment = HorizontalAlignment.Left };
                    step.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                    step.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(stepWidth - 22) });
                    step.Children.Add(Circle(12, stepDone, ink, gray));
                    var st = new TextBlock { Text = stepText, FontSize = 11, Foreground = stepDone ? gray : ink, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(st, 1);
                    step.Children.Add(st);
                    cell.Blocks.Add(new BlockUIContainer(step));
                }
                row.Cells.Add(cell);

                // Right column: due date and priority, small and aligned.
                var side = new Paragraph { TextAlignment = TextAlignment.Right, Margin = new Thickness(0, gap + 1, fill != null ? 6 : 0, 0) };
                if (item.Priority == "High") side.Inlines.Add(new Run("High  ") { FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = accent });
                if (item.DueDate is DateTime due)
                {
                    // "Oct 15", with the year only when it isn't this year; overdue in the accent color.
                    string label = due.Year == DateTime.Today.Year ? due.ToString("MMM d") : due.ToString("MMM d, yyyy");
                    side.Inlines.Add(new Run(label) { FontSize = 10, Foreground = !item.Done && due < DateTime.Today ? accent : gray });
                }
                else if (item.Due.Length > 0) side.Inlines.Add(new Run(item.Due) { FontSize = 10, Foreground = gray });
                row.Cells.Add(new TableCell(side) { BorderBrush = cellLineBrush, BorderThickness = cellLine });
                rows.Rows.Add(row);
            }
            if (g.Items.Count == 0 && daySheet)
                doc.Blocks.Add(new BlockUIContainer(new Border { Height = 0.75, Width = content, Background = rule }) { Margin = new Thickness(30, 24, 0, 0) });
            else if (g.Items.Count == 0) doc.Blocks.Add(new Paragraph(new Run("Nothing here right now.")) { Foreground = gray });
            else doc.Blocks.Add(table);
        }

        // ---- A few lines for notes ----
        var notes = new Paragraph(new Run("Notes")) { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = gray, Margin = new Thickness(0, 22, 0, 0), KeepWithNext = true };
        doc.Blocks.Add(notes);
        for (int n = 0; n < 4; n++)
            doc.Blocks.Add(new BlockUIContainer(new Border { Height = 0.75, Width = content, Background = rule }) { Margin = new Thickness(0, 22, 0, 0) });
        doc.Blocks.Add(new Paragraph(new Run($"Printed from Focus, {DateTime.Now:MMMM d, yyyy h:mm tt}")) { FontSize = 8.5, Foreground = gray, Margin = new Thickness(0, 18, 0, 0) });
        return doc;
    }

    /// <summary>A pencil-friendly circle: open to tick by hand, or filled with a check if already done.</summary>
    private static FrameworkElement Circle(double size, bool done, Brush ink, Brush gray)
    {
        var g = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        g.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = done ? gray : ink, StrokeThickness = size > 14 ? 1.3 : 1.1, Fill = done ? gray : Brushes.White });
        if (done)
            g.Children.Add(new TextBlock { Text = "✓", Foreground = Brushes.White, FontSize = size * 0.65, FontWeight = FontWeights.Bold,
                                           HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return g;
    }

    private void CopyGroups(string title, List<ExportGroup> groups)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title).AppendLine();
        foreach (var g in groups)
        {
            if (groups.Count > 1 || g.Header != title) sb.AppendLine(g.Header);
            foreach (var i in g.Items)
            {
                sb.AppendLine((i.Done ? "[x] " : "[ ] ") + i.Text + (i.Detail.Length > 0 ? $"  ({i.Detail})" : ""));
                foreach (var (text, done) in i.Steps) sb.AppendLine((done ? "    [x] " : "    [ ] ") + text);
            }
            sb.AppendLine();
        }
        Clipboard.SetText(sb.ToString().TrimEnd());
        _toast.ShowMessage("Copied", $"{title}: {groups.Sum(g => g.Items.Count)} tasks, ready to paste.");
    }

    private void SaveGroupsCsv(string title, List<ExportGroup> groups)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Focus tasks - {title} - {DateTime.Today:yyyy-MM-dd}.csv",
            Filter = "Spreadsheet (CSV)|*.csv",
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("Group,Task,Done,Due,Priority,Project,Tags,Steps,Notes");
        foreach (var g in groups)
            foreach (var i in g.Items)
                sb.AppendLine(string.Join(",", Q(g.Header), Q(i.Text), Q(i.Done ? "yes" : ""), Q(i.Due), Q(i.Priority), Q(i.Project), Q(i.Tags),
                    Q(string.Join("; ", i.Steps.Select(s => (s.Done ? "[x] " : "[ ] ") + s.Text))), Q(i.Notes)));
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true)); // the marker lets Excel read accents correctly
        _toast.ShowMessage("Saved", Path.GetFileName(dialog.FileName));
    }

    // ======================================================================================
    // Keyboard shortcuts
    // ======================================================================================

    /// <summary>
    /// Handles the app's keyboard shortcuts. While you're typing in a box, only Ctrl shortcuts that
    /// don't mean something to the box itself are handled.
    /// </summary>
    private bool HandleShortcut(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods == ModifierKeys.Control, ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift), alt = mods == ModifierKeys.Alt;
        bool typing = Keyboard.FocusedElement is TextBox;
        bool inPopup = BlockPopup.IsOpen || TaskPopup.IsOpen;

        // Work from anywhere, even while typing.
        // Ctrl+N: in the Tasks tab, open the add box there; anywhere else, the quick-add box.
        if (ctrl && key == Key.N) { if (NavTasks.IsChecked == true) ShowAddBox(); else SideAddTask_Click(this, new RoutedEventArgs()); return true; }
        if (ctrl && key == Key.P) { PrintShortcut(); return true; }
        if (ctrl && key == Key.OemComma) { NavSettings.IsChecked = true; return true; }
        if (key == Key.F1 || (ctrl && (key == Key.Oem2 || key == Key.OemQuestion))) { ShowShortcutHelp(); return true; }
        if (key == Key.F5 && mods == ModifierKeys.None) { RefreshCalendars_Click(this, new RoutedEventArgs()); ReloadSharedData(force: true); return true; }
        if (ctrl && key >= Key.D1 && key <= Key.D8)
        {
            var navs = new[] { NavDashboard, NavPlan, NavCalendar, NavTasks, NavStats, NavLists, NavPomodoro, NavFocus }
                .Where(n => n.Visibility == Visibility.Visible).ToList();
            int i = key - Key.D1;
            if (i < navs.Count) navs[i].IsChecked = true;
            return true;
        }
        if (ctrl && key == Key.F)
        {
            if (NavPlan.IsChecked == true) { PlanTaskSearch.Focus(); PlanTaskSearch.SelectAll(); }
            else { NavTasks.IsChecked = true; ShowSearch(); }
            return true;
        }
        if (ctrl && key == Key.E && _runner.Current is PlanBlock running && running.IsFocus)
        {
            OpenExtend(NavDashboard.IsChecked == true ? DashTimer : (UIElement)RunTime);
            return true;
        }

        if (typing || inPopup) return false;

        if ((ctrl && key == Key.Y) || (ctrlShift && key == Key.Z)) { Redo(); return true; }

        // Day plan and Calendar: days.
        bool plan = NavPlan.IsChecked == true, calendar = NavCalendar.IsChecked == true, tasks = NavTasks.IsChecked == true;
        if (ctrl && (key == Key.Left || key == Key.Right || key == Key.T) && (plan || calendar))
        {
            if (plan) { if (key == Key.T) Today_Click(this, new RoutedEventArgs()); else if (key == Key.Left) PrevDay_Click(this, new RoutedEventArgs()); else NextDay_Click(this, new RoutedEventArgs()); }
            else { if (key == Key.T) CalToday_Click(this, new RoutedEventArgs()); else if (key == Key.Left) CalPrev_Click(this, new RoutedEventArgs()); else CalNext_Click(this, new RoutedEventArgs()); }
            return true;
        }

        // Day plan: blocks.
        if (plan && _plan.Count > 0)
        {
            int at = _selected == null ? -1 : _plan.IndexOf(_selected);
            if (mods == ModifierKeys.None && (key == Key.Up || key == Key.Down))
            {
                int next = key == Key.Down ? Math.Min(_plan.Count - 1, at + 1) : Math.Max(0, at < 0 ? 0 : at - 1);
                SelectBlock(_plan[next]);
                Timeline.ScrollToBlock(_plan[next]);
                return true;
            }
            if (mods == ModifierKeys.None && key == Key.Enter && _selected != null) { OpenBlockEditor(_selected); return true; }
            if (ctrl && key == Key.D && _selected != null) { DuplicateSelected(); return true; }
            if (alt && (key == Key.Up || key == Key.Down) && _selected != null && at >= 0)
            {
                int to = key == Key.Up ? at - 1 : at + 1;
                if (to >= 0 && to < _plan.Count) MoveBlockTo(_selected, to);
                return true;
            }
        }

        // Tasks tab: moving through the list.
        if (tasks && BinTodoScroll.Visibility == Visibility.Visible)
        {
            var visible = VisibleBinTasks();
            var current = _selection.Count == 1 ? _selection[0] : null;
            if (mods == ModifierKeys.None && (key == Key.Up || key == Key.Down) && visible.Count > 0)
            {
                int i = current == null ? -1 : visible.IndexOf(current);
                int next = key == Key.Down ? Math.Min(visible.Count - 1, i + 1) : Math.Max(0, i < 0 ? 0 : i - 1);
                SelectOnly(visible[next]);
                return true;
            }
            if (mods == ModifierKeys.None && key == Key.Enter && current != null)
            {
                OpenBinTaskDetails(BinList, current);
                return true;
            }
            if (ctrlShift && key == Key.Enter && _selection.Count > 0) { BulkFinish_Click(this, new RoutedEventArgs()); return true; }
            if (mods == ModifierKeys.None && key == Key.Delete && _selection.Count > 0) { BulkDelete_Click(this, new RoutedEventArgs()); return true; }
            if (mods == ModifierKeys.None && key == Key.Escape && _selection.Count > 0) { ClearSelection(); return true; }
        }

        // Dashboard: Space pauses or resumes (when no button has the keyboard).
        if (mods == ModifierKeys.None && key == Key.Space && NavDashboard.IsChecked == true && _runner.IsRunning &&
            Keyboard.FocusedElement is not ButtonBase && PauseBtn.Visibility == Visibility.Visible)
        {
            Pause_Click(this, new RoutedEventArgs());
            return true;
        }
        return false;
    }

    private void PrintShortcut()
    {
        if (NavTasks.IsChecked == true) ShowExportMenu(ExportBtn, includeCurrentView: true);
        else ShowExportMenu(NavDashboard.IsChecked == true ? DashPrintBtn : (UIElement)NavSettings, includeCurrentView: false);
    }

    private void ShowShortcutHelp()
    {
        Info(string.Join("\n", new[]
        {
            "Ctrl+N            New task",
            "Ctrl+F            Search",
            "Ctrl+K            Command bar",
            "Ctrl+1 to 8       Dashboard, Day plan, Calendar, Tasks, Stats, Block lists, Pomodoro, Quick focus",
            "Ctrl+,            Settings",
            "Ctrl+Z / Ctrl+Y   Undo / redo",
            "Ctrl+P            Print or export",
            "Ctrl+← / →        Previous / next day",
            "Ctrl+T            Today",
            "↑ / ↓             Move through tasks or blocks",
            "Enter             Open the selected task or block",
            "Ctrl+Shift+Enter  Finish the selected task",
            "Delete            Delete the selected task or block",
            "Ctrl+D            Duplicate the selected block",
            "Alt+↑ / Alt+↓     Move the selected block earlier or later",
            "Space             Pause or resume (dashboard)",
            "Ctrl+E            Extend the current session",
            "F5                Refresh calendar and sync",
            "Ctrl+Enter / Esc  Save and close / close any popup",
            "Ctrl+Shift+Space  Add a task from anywhere",
            "Ctrl+Shift+F      Start or stop quick focus from anywhere"
        }), "Keyboard shortcuts");
    }

    // ======================================================================================
    // Redo
    // ======================================================================================

    private readonly Stack<UndoState> _redo = new();

    private void Redo()
    {
        if (!_settings.On("undo")) return;
        if (_redo.Count == 0) { _toast.ShowMessage("Nothing to redo", ""); return; }
        var state = _redo.Pop();
        using var sameSet = TaskStores.Use(state.Store);
        var current = CaptureState(state.Plan != null);
        if (current != null) _undo.AddLast(current);
        RestoreState(state, "Redone");
    }

    // ======================================================================================
    // Welcome tour (first run on a computer)
    // ======================================================================================

    private void ShowWelcomeIfNeeded()
    {
        if (_settings.TourShown) return;
        // Someone already using Focus (tasks or history exist) doesn't need the tour.
        if (TaskBin.All.Count > 0 || ResolvedTasks.All.Count > 0 || !_settings.On("tour"))
        {
            _settings.TourShown = true;
            SaveSettingsQuietly();
            return;
        }
        _settings.TourShown = true;
        SaveSettingsQuietly();
        var pages = new[]
        {
            ("Welcome to Focus", "Plan your day in focus sessions and breaks, block distractions while you work, and keep every task in one place."),
            ("Plan your day", "The Day plan fills your working hours around your meetings. Click any block to change it; drag tasks from the list on the right into sessions."),
            ("Tasks", "Add tasks anywhere: the + Add task button, Ctrl+N, or Ctrl+Shift+Space from any app. Type things like \"Call Sam friday at 3pm #Client @calls\"."),
            ("Focus", "Press Start my day on the dashboard. Blocked apps and sites stay closed during focus sessions. Press F1 any time for keyboard shortcuts.")
        };
        int page = 0;
        var title = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var dots = new TextBlock { Margin = new Thickness(0, 18, 0, 0) };
        dots.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var next = new Button { Content = "Next", Padding = new Thickness(22, 8, 22, 8), HorizontalAlignment = HorizontalAlignment.Right };
        next.SetResourceReference(StyleProperty, "PrimaryButton");
        var skip = new Button { Content = "Skip", Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(skip);
        buttons.Children.Add(next);
        var stack = new StackPanel { Margin = new Thickness(30) };
        stack.Children.Add(title);
        stack.Children.Add(body);
        stack.Children.Add(dots);
        stack.Children.Add(buttons);
        var window = new Window
        {
            Title = "Welcome to Focus", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Content = stack, Owner = IsVisible ? this : null
        };
        window.SetResourceReference(BackgroundProperty, "Panel");
        window.SetResourceReference(ForegroundProperty, "Text");
        void Show()
        {
            title.Text = pages[page].Item1;
            body.Text = pages[page].Item2;
            dots.Text = string.Join("  ", Enumerable.Range(0, pages.Length).Select(i => i == page ? "●" : "○"));
            next.Content = page == pages.Length - 1 ? "Get started" : "Next";
        }
        next.Click += (_, _) => { if (++page >= pages.Length) window.Close(); else Show(); };
        skip.Click += (_, _) => window.Close();
        Show();
        window.ShowDialog();
    }
}
