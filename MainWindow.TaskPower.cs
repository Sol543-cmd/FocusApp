using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// The second round of task features: repeats, reminders, steps and notes, Upcoming and snooze,
/// search, undo, the board, the end-of-day review, and switching features on and off.
/// </summary>
public partial class MainWindow
{
    // ---------- Finishing a task (and making the next one if it repeats) ----------

    /// <summary>
    /// Records a task as resolved. If it repeats, the next one goes into your task list with its
    /// next due date (and reminder), ready to go again.
    /// </summary>
    private void ResolveTask(TaskBase task, string from)
    {
        ResolvedTasks.Add(task.Text, from, task.Spent, task.Estimate, task.Project);
        UpdateGoals();
        if (!_settings.On("repeat") || task.RepeatRule is not RepeatRule rule) return;

        var oldDue = task.Due ?? DateTime.Today;
        var next = rule.Next(oldDue);
        while (next < DateTime.Today) next = rule.Next(next);

        var again = new BinTask { From = "Repeats: " + rule.Describe() };
        again.CopyDetailsFrom(task);
        again.Due = next;
        again.RemindAt = task.RemindAt is DateTime r ? next + r.TimeOfDay : null;
        again.Subtasks = task.Subtasks.Select(s => new Subtask { Text = s.Text }).ToList();
        again.PlannedFor = "";
        again.SnoozedUntil = null;
        again.Status = "";
        again.Spent = 0;
        TaskBin.All.Add(again);
        TaskBin.Save();
    }

    // ---------- Undo ----------

    private sealed class UndoState
    {
        public string Bin = "", Resolved = "";
        public TaskStore Store = TaskStores.Main; // which set (Work or Personal) this copy is of
        public DateTime PlanDate;
        public List<PlanBlock>? Plan;
        public string DayStart = "";
        public bool StartsNow;
        public TimeSpan? PlanEnd, WorkStart;
    }

    private readonly LinkedList<UndoState> _undo = new();
    private int _undoResolvedCount = -1;
    private bool _restoring;

    /// <summary>Remembers how things are now, so the next change can be undone.</summary>
    /// <summary>A copy of how tasks (and, if asked, the plan on screen) are right now.</summary>
    private UndoState? CaptureState(bool includePlan)
    {
        try
        {
            var last = _undo.Last?.Value;
            string bin = System.Text.Json.JsonSerializer.Serialize(TaskBin.All.ToList());
            string resolved = last != null && ReferenceEquals(last.Store, TaskStores.Current) && ResolvedTasks.All.Count == _undoResolvedCount ? last.Resolved
                : System.Text.Json.JsonSerializer.Serialize(ResolvedTasks.All);
            _undoResolvedCount = ResolvedTasks.All.Count;
            var state = new UndoState { Bin = bin, Resolved = resolved, PlanDate = _planDate, Store = TaskStores.Current };
            if (includePlan && !ViewingRunningDay)
            {
                state.Plan = _plan.Select(b => b.Clone()).ToList();
                state.DayStart = DayStartBox.Text;
                state.StartsNow = _startIsAuto;
                state.PlanEnd = _planEnd;
                state.WorkStart = _planWorkStart;
            }
            return state;
        }
        catch (Exception ex)
        {
            Log.Error("CaptureState", ex);
            return null;
        }
    }

    /// <summary>Remembers how things are now, so the next change can be undone.</summary>
    private void PushUndo(bool includePlan = false)
    {
        if (_restoring || !_settings.On("undo")) return;
        var state = CaptureState(includePlan);
        if (state == null) return;
        _undo.AddLast(state);
        while (_undo.Count > 30) _undo.RemoveFirst();
        _redo.Clear(); // a new change starts a new history
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (!_settings.On("undo")) return;
        if (_undo.Last is not LinkedListNode<UndoState> node)
        {
            _toast.ShowMessage("Nothing to undo", "Undo remembers your last 30 changes while Focus is open.");
            return;
        }
        _undo.RemoveLast();
        var state = node.Value;
        using var sameSet = TaskStores.Use(state.Store);
        var current = CaptureState(state.Plan != null);
        if (current != null) _redo.Push(current);
        RestoreState(state, "Undone");
    }

    /// <summary>Puts tasks (and the plan, if the copy has it and it's still on screen) back the way they were.</summary>
    private void RestoreState(UndoState state, string message)
    {
        using var sameSet = TaskStores.Use(state.Store); // always back into the set it came from (Work or Personal)
        _restoring = true;
        try
        {
            var bin = System.Text.Json.JsonSerializer.Deserialize<List<BinTask>>(state.Bin) ?? new();
            TaskBin.All.Clear();
            foreach (var t in bin) TaskBin.All.Add(t);
            TaskBin.Save();
            var resolved = System.Text.Json.JsonSerializer.Deserialize<List<ResolvedTask>>(state.Resolved) ?? new();
            ResolvedTasks.All.Clear();
            ResolvedTasks.All.AddRange(resolved);
            ResolvedTasks.Save();
            _undoResolvedCount = -1;

            if (state.Plan != null && state.PlanDate == _planDate && !ViewingRunningDay)
            {
                _loadingPlan = true;
                try
                {
                    ClearPlan();
                    foreach (var b in state.Plan) _plan.Add(b.Clone());
                    if (state.StartsNow) UseCurrentTime();
                    else if (TryParseTime(state.DayStart, out var start)) SetPinnedStart(start);
                    _planEnd = state.PlanEnd;
                    _planWorkStart = state.WorkStart;
                }
                finally
                {
                    _loadingPlan = false;
                }
                _planDirty = true;
                SavePlanNow();
                SelectBlock(null);
                RecalcTimes();
            }
            ClearSelection();
            RefreshTasksTab();
            RefreshDashTasks();
            _toast.ShowMessage(message, "");
        }
        catch (Exception ex)
        {
            Log.Error("RestoreState", ex);
        }
        finally
        {
            _restoring = false;
        }
    }

    private string _search = "";

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshTasksTab();
    }

    private bool MatchesSearch(TaskBase t) =>
        _search.Length == 0 ||
        t.Text.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ||
        t.Notes.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ||
        t.Tags.Any(tag => tag.Contains(_search.TrimStart('#'), StringComparison.CurrentCultureIgnoreCase)) ||
        t.Subtasks.Any(s => s.Text.Contains(_search, StringComparison.CurrentCultureIgnoreCase));

    // ---------- Upcoming ----------

    private sealed class UpcomingDay
    {
        public string Header { get; init; } = "";
        public List<BinTask> Items { get; init; } = new();
        public DateTime? Date { get; init; }
    }

    /// <summary>The day a task shows up on in Upcoming: snoozed-until, picked-for, or due date.</summary>
    private static DateTime? UpcomingDate(BinTask t) =>
        t.IsSnoozed ? t.SnoozedUntil : t.PlannedDate is DateTime p && p > DateTime.Today ? p : t.Due;

    private void RefreshUpcoming()
    {
        var today = DateTime.Today;
        var tasks = TaskBin.All.Where(MatchesSearch).Select(t => (Task: t, Date: UpcomingDate(t)))
            .Where(x => x.Date != null).ToList();
        var groups = new List<UpcomingDay>();
        void Add(string header, IEnumerable<BinTask> items, DateTime? date = null)
        {
            var list = items.ToList();
            // The next two weeks always show, so tasks can be dragged onto any of those days.
            if (list.Count > 0 || date != null) groups.Add(new UpcomingDay { Header = header, Items = list, Date = date });
        }
        Add("Overdue", tasks.Where(x => x.Date < today && !x.Task.IsSnoozed).Select(x => x.Task));
        for (int i = 0; i < 14; i++)
        {
            var day = today.AddDays(i);
            string header = i == 0 ? "Today" : i == 1 ? "Tomorrow" : day.ToString("dddd, MMMM d");
            Add(header, tasks.Where(x => x.Date == day).OrderBy(x => TaskBase.Rank(x.Task)).Select(x => x.Task), day);
        }
        Add("Later", tasks.Where(x => x.Date >= today.AddDays(14)).OrderBy(x => x.Date).Select(x => x.Task));
        if (groups.Count == 0) groups.Add(new UpcomingDay { Header = "Nothing scheduled. Give tasks a due date or snooze them to see them here." });
        UpcomingList.ItemsSource = groups;
    }

    // ---------- Board ----------

    private static readonly (string Status, string Title)[] BoardColumns =
        { ("", "To do"), ("doing", "Doing"), ("waiting", "Waiting"), ("done", "Done today") };

    private void RefreshBoard()
    {
        BoardGrid.Children.Clear();
        BoardGrid.ColumnDefinitions.Clear();
        for (int c = 0; c < BoardColumns.Length; c++)
        {
            BoardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (status, title) = BoardColumns[c];

            var column = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0, 0, c < 3 ? 10 : 0, 0), AllowDrop = true };
            column.SetResourceReference(Border.BackgroundProperty, "Panel2");
            column.Drop += (_, e) => BoardDrop(status, e);
            column.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(typeof(BinTask)) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };

            var stack = new StackPanel();
            var items = new List<FrameworkElement>();
            if (status == "done")
            {
                foreach (var r in ResolvedTasks.All.Where(r => r.ResolvedAt.Date == DateTime.Today &&
                             (_search.Length == 0 || r.Text.Contains(_search, StringComparison.CurrentCultureIgnoreCase))))
                    items.Add(BoardCard(r.Text, r.Took, null));
            }
            else
            {
                foreach (var t in TaskBin.All.Where(t => t.Status == status && !(t.IsSnoozed && _settings.On("upcoming")) && MatchesSearch(t))
                             .OrderBy(TaskBase.Rank))
                    items.Add(BoardCard(t.Text, t.Details, t));
            }
            var header = new TextBlock { Text = $"{title} ({items.Count})", FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 10) };
            header.SetResourceReference(TextBlock.FontSizeProperty, "TaskFontSize");
            stack.Children.Add(header);
            foreach (var item in items) stack.Children.Add(item);
            column.Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(column, c);
            BoardGrid.Children.Add(column);
        }
    }

    private FrameworkElement BoardCard(string text, string details, BinTask? task)
    {
        var stack = new StackPanel();
        var title = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.FontSizeProperty, "TaskFontSize");
        stack.Children.Add(title);
        if (details.Length > 0)
        {
            var d = new TextBlock { Text = details, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            d.SetResourceReference(TextBlock.ForegroundProperty, task?.IsOverdue == true ? "MeetingBrush" : "Muted");
            stack.Children.Add(d);
        }
        var card = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        if (task == null) return card;

        card.Cursor = Cursors.Hand;
        card.ToolTip = "Drag to another column. Click to edit.";
        Point down = default;
        bool pressed = false;
        card.MouseLeftButtonDown += (_, e) => { down = e.GetPosition(card); pressed = true; };
        card.MouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(card);
            if (Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) < 8) return;
            pressed = false;
            DragDrop.DoDragDrop(card, new DataObject(typeof(BinTask), task), DragDropEffects.Move);
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (!pressed) return;
            pressed = false;
            if (_settings.On("taskDetails")) OpenTaskDetails(card, task, () => { TaskBin.Save(); RefreshTasksTab(); }, () => { TaskBin.All.Remove(task); TaskBin.Save(); });
        };
        return card;
    }

    private void BoardDrop(string status, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(BinTask)) is not BinTask task || !TaskBin.All.Contains(task)) return;
        PushUndo(includePlan: true);
        if (status == "done") FinishListTask(task, task.From, task.Spent);
        else
        {
            if (status == "waiting" && task.Status != "waiting") task.WaitingSince = DateTime.Now;
            task.Status = status;
            TaskBin.Save();
        }
        RefreshTasksTab();
    }

    // ---------- The task details panel: the extra fields ----------

    private readonly ObservableCollection<Subtask> _editSubtasks = new();

    private void FillExtraDetails(TaskBase task)
    {
        FillProjectPicker(task);
        TaskRepeatBox.Text = task.Repeat;
        TaskRemindBox.Text = task.RemindAt is DateTime r ? (r.Date == DateTime.Today ? r.ToString("t") : $"{r:ddd MMM d} {r:t}") : "";
        TaskStatusTodo.IsChecked = task.Status == "";
        TaskStatusDoing.IsChecked = task.Status == "doing";
        TaskStatusWaiting.IsChecked = task.Status == "waiting";
        TaskWaitingBox.Text = task.WaitingOn;
        TaskNotesBox.Text = task.Notes;
        _editSubtasks.Clear();
        foreach (var s in task.Subtasks) _editSubtasks.Add(new Subtask { Text = s.Text, Done = s.Done });
        TaskSubtaskList.ItemsSource = _editSubtasks;
        TaskSubtaskBox.Text = "";
        TaskSnoozeBox.Text = task.IsSnoozed ? task.SnoozedUntil!.Value.ToString("d") : "";

        TaskRepeatRow.Visibility = _settings.On("repeat") ? Visibility.Visible : Visibility.Collapsed;
        TaskRemindRow.Visibility = _settings.On("naturalDates") ? Visibility.Visible : Visibility.Collapsed;
        TaskStatusRow.Visibility = _settings.On("board") && task is BinTask ? Visibility.Visible : Visibility.Collapsed;
        TaskSubtasksRow.Visibility = _settings.On("subtasks") ? Visibility.Visible : Visibility.Collapsed;
        TaskSnoozeRow.Visibility = _settings.On("upcoming") && task is BinTask ? Visibility.Visible : Visibility.Collapsed;
        TaskTodayCheck.Visibility = task is BinTask && _settings.On("todayPlanning") ? Visibility.Visible : Visibility.Collapsed;
        UpdateRepeatPreview();
        UpdateRemindPreview();
    }

    /// <summary>Reads the extra fields back. Returns a problem to show, or null if all is well.</summary>
    private string? SaveExtraDetails(TaskBase task)
    {
        DateTime? remind = null;
        if (TaskRemindRow.Visibility == Visibility.Visible && TaskRemindBox.Text.Trim().Length > 0)
        {
            remind = ParseReminder(TaskRemindBox.Text, task.Due);
            if (remind == null) return "Couldn't read the reminder. Try 3pm, tomorrow 9am, or friday at 2:30pm.";
        }
        if (TaskRepeatRow.Visibility == Visibility.Visible && TaskRepeatBox.Text.Trim().Length > 0 &&
            RepeatRule.Parse(TaskRepeatBox.Text, task.Due) == null)
            return "Couldn't read the repeat. Try every friday, weekdays, or every month on the 15th.";
        DateTime? snooze = null;
        if (TaskSnoozeRow.Visibility == Visibility.Visible && TaskSnoozeBox.Text.Trim().Length > 0)
        {
            snooze = TaskParser.ParseDate(TaskSnoozeBox.Text);
            if (snooze == null) return "Couldn't read the snooze date. Try monday, next week, or 10/20.";
        }

        if (TaskRemindRow.Visibility == Visibility.Visible) task.RemindAt = remind;
        if (TaskRepeatRow.Visibility == Visibility.Visible)
        {
            task.Repeat = TaskRepeatBox.Text.Trim();
            if (task.Repeat.Length > 0 && task.Due == null && task.RepeatRule is RepeatRule rule)
                task.Due = rule.Next(DateTime.Today.AddDays(-1)); // a repeating task needs a first date
        }
        if (TaskStatusRow.Visibility == Visibility.Visible)
        {
            string newStatus = TaskStatusDoing.IsChecked == true ? "doing" : TaskStatusWaiting.IsChecked == true ? "waiting" : "";
            if (newStatus == "waiting" && task.Status != "waiting") task.WaitingSince = DateTime.Now;
            task.Status = newStatus;
            task.WaitingOn = task.Status == "waiting" ? TaskWaitingBox.Text.Trim() : "";
        }
        if (TaskSubtasksRow.Visibility == Visibility.Visible)
        {
            task.Notes = TaskNotesBox.Text;
            task.Subtasks = _editSubtasks.Where(s => s.Text.Trim().Length > 0).Select(s => new Subtask { Text = s.Text.Trim(), Done = s.Done }).ToList();
        }
        if (TaskSnoozeRow.Visibility == Visibility.Visible) task.SnoozedUntil = snooze;
        return null;
    }

    /// <summary>"3pm", "tomorrow 9am", "friday at 2:30pm". A time alone means the due date (or today).</summary>
    private static DateTime? ParseReminder(string text, DateTime? due)
    {
        var words = RepeatRule.Words(text);
        DateTime? date = null;
        TimeSpan? time = null;
        for (int i = 0; i < words.Count; i++)
        {
            if (time == null && DateWords.TryTime(words, i, out int ut) is TimeSpan t) { time = t; i += ut - 1; continue; }
            int skip = words[i] is "on" ? 1 : 0;
            if (date == null && DateWords.TryDate(words, i + skip, out int ud, DateTime.Today) is DateTime d) { date = d; i += skip + ud - 1; continue; }
            return null;
        }
        if (time == null) return null;
        return (date ?? due ?? DateTime.Today).Date + time.Value;
    }

    private void TaskRepeatBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRepeatPreview();
    private void TaskRemindBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRemindPreview();

    private void UpdateRepeatPreview()
    {
        string text = TaskRepeatBox.Text.Trim();
        TaskRepeatPreview.Text = text.Length == 0 ? "Doesn't repeat" :
            RepeatRule.Parse(text, _editingTask?.Due) is RepeatRule r ? r.Describe() : "Not understood yet";
    }

    private void UpdateRemindPreview()
    {
        string text = TaskRemindBox.Text.Trim();
        TaskRemindPreview.Text = text.Length == 0 ? "No reminder" :
            ParseReminder(text, _editingTask?.Due) is DateTime r ? $"Reminds you {r:dddd, MMM d} at {r:t}" : "Not understood yet";
    }

    private void TaskSubtaskBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        string text = TaskSubtaskBox.Text.Trim();
        if (text.Length == 0) return;
        _editSubtasks.Add(new Subtask { Text = text });
        TaskSubtaskBox.Text = "";
    }

    private void RemoveSubtask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Subtask s) _editSubtasks.Remove(s);
    }

    private void SnoozeTomorrow_Click(object sender, RoutedEventArgs e) => TaskSnoozeBox.Text = DateTime.Today.AddDays(1).ToString("d");
    private void SnoozeNextWeek_Click(object sender, RoutedEventArgs e) =>
        TaskSnoozeBox.Text = DateTime.Today.AddDays(7 - ((int)DateTime.Today.DayOfWeek + 6) % 7).ToString("d");
    private void SnoozeNone_Click(object sender, RoutedEventArgs e) => TaskSnoozeBox.Text = "";

    // ---------- Reminders ----------

    /// <summary>Shows reminders that are due (checked every 30 seconds). Each fires once.</summary>
    private void CheckTaskReminders()
    {
        if (!_settings.On("naturalDates")) return;
        var now = DateTime.Now;
        var due = new List<TaskBase>();
        due.AddRange(TaskBin.All.Where(t => t.RemindAt is DateTime r && r <= now));
        foreach (var block in _plan) due.AddRange(block.Tasks.Where(t => !t.Done && t.RemindAt is DateTime r && r <= now));
        if (_runner.IsRunning) foreach (var block in _runner.Blocks) due.AddRange(block.Tasks.Where(t => !t.Done && t.RemindAt is DateTime r && r <= now));
        if (due.Count == 0) return;

        foreach (var task in due.Distinct())
        {
            bool recent = now - task.RemindAt!.Value < TimeSpan.FromHours(12);
            task.RemindAt = null; // fire once
            if (recent)
            {
                _tray.ShowBalloonTip(15000, "Reminder", task.Text, System.Windows.Forms.ToolTipIcon.None);
                Notify("reminder:" + task.Id + now.ToString("HHmm"), "Reminder: " + task.Text, "Due " + (task.Due is DateTime d ? d.ToString("ddd MMM d") : "today") + ".");
            }
        }
        TaskBin.Save();
        if (_planDate == DateTime.Today) { _planDirty = true; SavePlanNow(); }
        if (_runner.IsRunning) SaveRunState();
    }

    // ---------- End-of-day review ----------

    private void ReviewToday_Click(object sender, RoutedEventArgs e) => OpenReview();

    /// <summary>Unfinished tasks from today: in today's plan sessions, and in the bin for today.</summary>
    private void OpenReview()
    {
        if (!_settings.On("review")) return;
        bool running = _mode == RunMode.DayPlan && _runner.IsRunning;
        var (blocks, planStart) = running ? (_runner.Blocks, TimeSpan.Zero) : PlanFor(DateTime.Today);
        var fromPlan = blocks.SelectMany(b => b.Tasks.Where(t => !t.Done && t.Text.Trim().Length > 0).Select(t => (Block: b, Task: (TaskBase)t))).ToList();
        var fromBin = TaskBin.All.Where(t => (t.IsPlannedToday || t.IsDueToday || t.IsOverdue) && !t.IsSnoozed).ToList();
        // A list task placed in sessions shows once: as the list task.
        var listIds = new HashSet<string>(fromBin.Select(t => t.Id));
        fromPlan = fromPlan.Where(x => x.Task is not TaskItem i || i.BinId.Length == 0 || !listIds.Contains(i.BinId))
            .GroupBy(x => x.Task is TaskItem i && i.BinId.Length > 0 ? i.BinId : x.Task.Id).Select(g => g.First()).ToList();

        _stats ??= FocusStats.Load();
        string summary = $"Today you resolved {ResolvedTasks.CountSince(DateTime.Today)} tasks and did {Format.HoursMinutes(_stats.MinutesOn(DateTime.Today))} of focus with blocking. " +
                         "Choose where each unfinished task goes.";
        var window = new ReviewWindow(fromPlan.Select(x => x.Task).Concat(fromBin), summary);
        if (IsVisible) window.Owner = this;
        _settings.LastReviewDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        SaveSettingsQuietly();
        if (window.ShowDialog() != true || window.Result == null) return;

        PushUndo(includePlan: _planDate == DateTime.Today);
        foreach (var choice in window.Result)
        {
            // Tasks still on a session come off it first.
            var owner = fromPlan.FirstOrDefault(x => ReferenceEquals(x.Task, choice.Task)).Block;
            BinTask binTask;
            if (owner != null && choice.Task is TaskItem linked && FindLinked(linked) is BinTask original)
            {
                // From the task list: the list task is updated, and today's copies come off the sessions.
                binTask = original;
                RemoveLinkedCopies(original.Id);
            }
            else if (owner != null && choice.Task is TaskItem item)
            {
                owner.Tasks.Remove(item);
                binTask = new BinTask { From = $"From {owner.Name}, {DateTime.Now:ddd MMM d}" };
                binTask.CopyDetailsFrom(item);
                if (choice.Where != "drop") TaskBin.All.Add(binTask);
            }
            else if (choice.Task is BinTask existing)
            {
                binTask = existing;
                RemoveLinkedCopies(existing.Id); // today is over; tomorrow's plan gets it fresh
            }
            else continue;

            switch (choice.Where)
            {
                case "tomorrow": binTask.PlannedFor = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); break;
                case "day": binTask.PlannedFor = choice.Day!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); break;
                case "bin": binTask.PlannedFor = ""; break;
                case "drop": TaskBin.All.Remove(binTask); RemoveLinkedCopies(binTask.Id); break;
            }
        }
        TaskBin.Save();
        if (_runner.IsRunning) SaveRunState();
        if (_planDate == DateTime.Today) { _planDirty = true; SavePlanNow(); }
        else if (!running && fromPlan.Count > 0 && TryLoadPlanFile(DateTime.Today, out var todayFile))
        {
            // Today's plan isn't on screen: save the tasks taken off its sessions.
            todayFile.Blocks = blocks.Select(b => b.Clone()).ToList();
            Storage.Save(DayPlanFile.FileFor(DateTime.Today), todayFile);
        }
        RefreshTasksTab();
        RefreshDashTasks();
        _toast.ShowMessage("Day reviewed", "Tomorrow's tasks are picked and waiting in your task list.");
    }

    /// <summary>At the end of the working day, offers the review once (if there's anything to review).</summary>
    private void CheckReviewTime()
    {
        if (!_settings.On("review") || _runner.IsRunning) return;
        string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_settings.LastReviewDate == today || DateTime.Now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        var end = DateTime.Today + (_planDate == DateTime.Today && _planEnd != null ? _planEnd.Value : _settings.WorkdayEndTime);
        if (DateTime.Now < end || DateTime.Now > end.AddHours(3)) return;
        _settings.LastReviewDate = today;
        SaveSettingsQuietly();
        _reviewFromBalloon = true;
        _tray.ShowBalloonTip(15000, "Review your day", "Two minutes to decide where today's unfinished tasks go. Click here.",
            System.Windows.Forms.ToolTipIcon.None);
        Notify("review:" + today, "Review your day", "Two minutes to decide where today's unfinished tasks go.", ("Review now", OpenReview));
    }

    private bool _reviewFromBalloon;

    // ---------- Switching features on and off ----------

    private readonly Dictionary<string, CheckBox> _featureChecks = new();

    private void BuildFeatureChecks()
    {
        FeatureChecks.Children.Clear();
        _featureChecks.Clear();
        foreach (var group in Features.All.GroupBy(f => f.Area))
        {
            var header = new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) };
            FeatureChecks.Children.Add(header);
            foreach (var f in group.Where(f => f.Key is not ("greek" or "alphabet" or "concepts"))) // switched in Greek practice
            {
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = f.Name });
                var d = new TextBlock { Text = f.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                d.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                text.Children.Add(d);
                var check = new CheckBox { Content = text, IsChecked = _settings.On(f.Key), Margin = new Thickness(0, 0, 0, 8) };
                _featureChecks[f.Key] = check;
                FeatureChecks.Children.Add(check);
            }
        }
    }

    private void SaveFeatureChecks()
    {
        foreach (var (key, check) in _featureChecks) _settings.Features[key] = check.IsChecked == true;
    }

    /// <summary>Shows or hides each feature's buttons and turns its behavior on or off.</summary>
    private void ApplyFeatures()
    {
        TaskParser.Shortcuts = _settings.On("taskShortcuts");
        TaskParser.NaturalDates = _settings.On("naturalDates");
        Visibility V(string key) => _settings.On(key) ? Visibility.Visible : Visibility.Collapsed;

        BinFilters.Visibility = V("taskFilters");
        if (!_settings.On("taskFilters")) _binFilter = "all";
        SmartOrderCheck.Visibility = V("smartOrder");
        if (!_settings.On("smartOrder")) SmartOrderCheck.IsChecked = false;
        TodayPanel.Visibility = V("todayPlanning");
        _ui.TodayVisibility = V("todayPlanning");
        RefreshDashTasks(); // also decides whether + From task list shows
        SearchBox.Visibility = V("search");
        SearchToggle.Visibility = V("search");
        if (!_settings.On("search")) SearchRow.Visibility = Visibility.Collapsed;
        SearchHint.Visibility = _settings.On("search") && SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_settings.On("search")) SearchBox.Text = "";
        UndoTasksBtn.Visibility = V("undo");
        UndoPlanBtn.Visibility = V("undo");
        ReviewBtn.Visibility = V("review");
        BinUpcomingView.Visibility = V("upcoming");
        // The Home page: hidden from the sidebar when switched off (the dashboard takes over).
        NavHome.Visibility = V("home");
        if (!_settings.On("home") && NavHome.IsChecked == true) NavDashboard.IsChecked = true;
        ProjectsCard.Visibility = V("projects");
        TemplatesCard.Visibility = V("templates");
        GoalsSettings.Visibility = V("goals");
        WebsiteSettings.Visibility = Visibility.Collapsed; // website blocking was removed
        VocabOptions.Visibility = V("greek");
        AlphaOptions.Visibility = V("alphabet");
        DashGreekBtn.Visibility = V("greek");
        DashAlphaBtn.Visibility = V("alphabet");
        DashConceptsBtn.Visibility = V("concepts");
        ConceptOptions.Visibility = V("concepts");
        AutomationSettings.Visibility = _settings.On("autoWaiting") ? Visibility.Visible : Visibility.Collapsed;
        StatsCharts.Visibility = V("stats");
        StatsGame.Visibility = V("statsGame");
        StatsWorkloadSection.Visibility = V("workload");
        NavStats.Visibility = _settings.On("stats") || _settings.On("workload") || _settings.On("statsGame") ? Visibility.Visible : Visibility.Collapsed;
        if (NavStats.Visibility != Visibility.Visible && NavStats.IsChecked == true) NavPlan.IsChecked = true;
        if (!_settings.On("projects") && _binFilter.StartsWith("project:")) _binFilter = "all";
        if (!_settings.On("ageFilters") && _binFilter.StartsWith("age")) _binFilter = "all";
        SetUpFocusKey(_settings.On("focusKey"));
        UpdateGoals();
        BinBoardView.Visibility = V("board");
        if ((BinUpcomingView.IsChecked == true && !_settings.On("upcoming")) || (BinBoardView.IsChecked == true && !_settings.On("board")))
            BinTodoView.IsChecked = true;

        if (_settings.On("quickAdd")) { if (_quickAddKeys.Length == 0) SetUpQuickAdd(); }
        else
        {
            RemoveQuickAdd();
            _quickAddKeys = "";
            QuickAddInfo.Text = "The quick-add key is switched off (Settings, Features).";
        }
        RefreshTasksTab();
        UpdateKeysInfo();
    }

    // ---------- The guide ----------

    private void BuildGuide()
    {
        GuidePanel.Children.Clear();
        foreach (var topic in Guide.Topics)
        {
            var body = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(12, 2, 0, 10) };
            TextBlock Para(string label, string text)
            {
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
                tb.Inlines.Add(new System.Windows.Documents.Run(label) { FontWeight = FontWeights.SemiBold });
                tb.Inlines.Add(new System.Windows.Documents.Run(text));
                return tb;
            }
            body.Children.Add(Para("How it works: ", topic.HowItWorks));
            body.Children.Add(Para("Using it well: ", topic.Tips));
            var inspired = Para("Inspired by: ", topic.InspiredBy);
            inspired.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            body.Children.Add(inspired);

            var header = new Button { Content = "▸  " + topic.Title, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 4, 4, 4), FontSize = 14 };
            header.SetResourceReference(StyleProperty, "LinkButton");
            header.SetResourceReference(Control.ForegroundProperty, "Text");
            header.Click += (_, _) =>
            {
                bool open = body.Visibility != Visibility.Visible;
                body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                header.Content = (open ? "▾  " : "▸  ") + topic.Title;
            };
            GuidePanel.Children.Add(header);
            GuidePanel.Children.Add(body);
        }
    }
}
