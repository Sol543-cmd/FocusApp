using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>The day plan: the calendar timeline, the selected-block editor, and copy/paste.</summary>
public partial class MainWindow
{
    private PlanBlock? _selected;
    private PlanBlock? _clipboard;

    // The title box that was just focused by clicking a block (so Delete still deletes the block).
    private TextBox? _freshTitle;

    /// <summary>One click on a block: its title is ready to type over.</summary>
    private void FocusTitle(TextBox box)
    {
        if (!_settings.On("oneClickRename")) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            box.SelectAll();
            _freshTitle = box;
        }), DispatcherPriority.Input);
    }

    /// <summary>When this day's work starts, if you adjusted it (otherwise the usual start from Settings).</summary>
    private TimeSpan? _planWorkStart;

    // Each block's length as last seen, so a change can be measured (and passed on to the next block).
    private readonly Dictionary<PlanBlock, int> _knownMinutes = new();
    private bool _reflowing;
    private readonly DispatcherTimer _nowTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    private void SetUpTimeline()
    {
        Timeline.RangeStart = Defaults.CalendarStart;
        Timeline.RangeEnd = Defaults.CalendarEnd;
        Timeline.ShowNowLine = true;
        Timeline.BlockClicked += OpenBlockEditor;
        Timeline.BlockRightClicked += block => { CloseBlockEditor(); SelectBlock(block); };
        EditorTitleBox.TextChanged += (_, _) => { if (EditorTitleBox.IsKeyboardFocused) _freshTitle = null; };
        CalTitleBox.TextChanged += (_, _) => { if (CalTitleBox.IsKeyboardFocused) _freshTitle = null; };
        Timeline.MenuFor = BuildBlockMenu;
        Timeline.CanDrag = b => !b.IsAnchored && (RunningIndex < 0 || _plan.IndexOf(b) > RunningIndex);
        Timeline.BlockDropped += (block, index) => MoveBlockTo(block, index);
        Timeline.TaskDropped += AddTaskToSession;
        PreviewKeyDown += Window_PreviewKeyDown;
        _nowTimer.Tick += (_, _) =>
        {
            RefreshHome();
            Timeline.UpdateNow();
            CalDay.UpdateNow();
            if (!_runner.IsRunning) RefreshDashboard();
        };
        _nowTimer.Start();
    }

    private void Plan_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (PlanBlock b in e.NewItems) b.PropertyChanged += Block_PropertyChanged;
        if (e.OldItems != null)
            foreach (PlanBlock b in e.OldItems) b.PropertyChanged -= Block_PropertyChanged;
        if (_selected != null && !_plan.Contains(_selected)) SelectBlock(null);
        RecalcTimes();
        MarkPlanEdited();
    }

    private void Block_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlanBlock.Minutes) or nameof(PlanBlock.Name) or nameof(PlanBlock.BlockedApps)
            or nameof(PlanBlock.BlockListIds) or nameof(PlanBlock.Tasks))
            MarkPlanEdited();
        switch (e.PropertyName)
        {
            case nameof(PlanBlock.Minutes):
                if (sender is PlanBlock changed && !_loadingPlan && !_reflowing &&
                    _knownMinutes.TryGetValue(changed, out int before) && before != changed.Minutes)
                {
                    int running = RunningIndex, at = _plan.IndexOf(changed);
                    bool shrinkingCurrent = at == running && changed.Minutes < before;
                    if ((running >= 0 && at < running) || shrinkingCurrent)
                    {
                        // Not allowed: put the old length back.
                        _reflowing = true;
                        changed.Minutes = before;
                        _reflowing = false;
                        if (shrinkingCurrent)
                            _toast.ShowMessage("The current session can't be shortened",
                                "It can be made longer. To end it early, use Skip (if your lock mode allows it).");
                        RecalcTimes();
                        break;
                    }
                    // Longer or shorter: the next block gives or takes the difference; fixed times stay put.
                    int delta = changed.Minutes - before;
                    ApplyEdit(list => PlanReflow.AbsorbChange(list, changed, delta), keep: changed);
                }
                else RecalcTimes();
                break;
            case nameof(PlanBlock.Name):
                Timeline.RequestRender();
                if (ViewingRunningDay) SaveRunState();
                break;
            case nameof(PlanBlock.Tasks):
                if (ViewingRunningDay) SaveRunState();
                RefreshPlanTasks();
                break;
            case nameof(PlanBlock.BlockedApps):
            case nameof(PlanBlock.BlockListIds):
                Timeline.RequestRender();
                if (ViewingRunningDay)
                {
                    // Apps added to the session that's running now are blocked right away.
                    ApplyBlockingForCurrent();
                    SaveRunState();
                }
                break;
        }
    }

    // ---------- Selecting and editing a block ----------

    private void SelectBlock(PlanBlock? block)
    {
        if (_selected != null) _selected.IsSelected = false;
        _selected = block;
        if (block != null) block.IsSelected = true;

        Timeline.Selected = block;
        Timeline.RequestRender();
        CalDay.Selected = block;
        CalDay.RequestRender();

        // The calendar tab's small editor shows the same block.
        CalEditorFields.DataContext = block;
        CalEditorFields.Visibility = block == null ? Visibility.Collapsed : Visibility.Visible;
        CalNoSelection.Visibility = block == null ? Visibility.Visible : Visibility.Collapsed;
        if (block != null)
        {
            CalEditorKind.SetResourceReference(TextBlock.ForegroundProperty,
                block.Kind switch { BlockKind.Focus => "FocusBrush", BlockKind.Meeting => "MeetingBrush", _ => "BreakBrush" });
            CalAnchorNote.Visibility = block.IsAnchored ? Visibility.Visible : Visibility.Collapsed;
            if (block.AnchorStart is TimeSpan fixedAt)
                CalAnchorNote.Text = $"Fixed time: stays at {DateTime.Today.Add(fixedAt):t}.";
        }

        EditorPanel.DataContext = block;
        RefreshEditorSites(block);
        if (block == null && BlockPopup.IsOpen) BlockPopup.IsOpen = false;
        if (block == null) return;

        EditorBlocking.Visibility = block.IsFocus ? Visibility.Visible : Visibility.Collapsed;
        EditorTasks.Visibility = block.IsFocus ? Visibility.Visible : Visibility.Collapsed;
        EditorAnchorNote.Visibility = block.IsAnchored ? Visibility.Visible : Visibility.Collapsed;
        if (block.AnchorStart is TimeSpan at)
            EditorAnchorNote.Text = $"Fixed time: this stays at {DateTime.Today.Add(at):t}, and the plan reshapes around it.";
        UpdateEditorLocks();
        NewTaskBox.Text = "";
        string accent = block.Kind switch { BlockKind.Focus => "FocusBrush", BlockKind.Meeting => "MeetingBrush", _ => "BreakBrush" };
        EditorKind.SetResourceReference(TextBlock.ForegroundProperty, accent);
    }

    /// <summary>Replacing the whole day (auto-fill, clear, loading a layout) isn't possible while it runs.</summary>
    private bool CanEditStructure(bool explain = true)
    {
        if (!ViewingRunningDay) return true;
        if (explain)
            Info("Your day is running, so it can't be replaced as a whole. You can still change anything that hasn't started yet.", "Day plan");
        return false;
    }

    /// <summary>While the day runs: the position of the session happening now (blocks up to it can't move). Otherwise -1.</summary>
    private int RunningIndex => ViewingRunningDay && _runner.Current is PlanBlock cur ? _plan.IndexOf(cur) : -1;

    /// <summary>Can this block be moved, deleted, or have things inserted around it? (Not once it has started.)</summary>
    private bool CanChangeBlock(PlanBlock block, bool explain = true)
    {
        int running = RunningIndex;
        if (running < 0 || _plan.IndexOf(block) > running) return true;
        if (explain)
            Info("That session has already started. You can change anything after it. To end the current one early, use Skip.", "Day plan");
        return false;
    }

    /// <summary>Where new blocks go: after the selected one, but never before what's happening now.</summary>
    private int SafeInsertIndex(int wanted) => Math.Max(wanted, RunningIndex + 1);

    /// <summary>Turns plan controls on or off depending on whether a session is running.</summary>
    private void ApplyRunningEditRules()
    {
        bool planRunning = ViewingRunningDay;
        AutoFillBtn.IsEnabled = !planRunning;
        ClearBtn.IsEnabled = !planRunning;
        DayStartBox.IsEnabled = !planRunning;
        DayStartNowBtn.IsEnabled = !planRunning;
        UpdateEditorLocks();
        EditorRunningNote.Visibility = planRunning ? Visibility.Visible : Visibility.Collapsed;
        ShowListEditor();
    }

    /// <summary>Shows the running day's blocks in the planner, so edits apply to what's running.</summary>
    private void ShareRunningPlan()
    {
        if (_planDate != DateTime.Today) return;
        var blocks = _runner.Blocks.ToList();
        bool wasLoading = _loadingPlan;
        _loadingPlan = true;
        try
        {
            ClearPlan();
            foreach (var b in blocks) _plan.Add(b);
            if (_runner.ScheduleStartLocal is DateTime start) SetPinnedStart(start.TimeOfDay);
        }
        finally
        {
            _loadingPlan = wasLoading;
        }
        SelectBlock(null);
        RecalcTimes();
    }

    private void HighlightRunningBlock() => Timeline.RequestRender();

    // ---------- Edits that keep fixed times in place ----------

    /// <summary>
    /// Makes a change to the plan, then reshapes it so meetings, lunch and the set break stay at
    /// their times and the day still lines up.
    /// </summary>
    private void ApplyEdit(Action<List<PlanBlock>> change, PlanBlock? keep)
    {
        PushUndo(includePlan: true);
        _reflowing = true;
        try
        {
            var before = _plan.Select(b => (b, b.Minutes)).ToList();
            var endBefore = ReflowStart() + TimeSpan.FromMinutes(_plan.Sum(b => b.Minutes));
            var list = _plan.ToList();
            change(list);

            // While the day runs, what has happened (and the session happening now) stays put;
            // only what comes after is reshaped.
            int locked = 0;
            var start = ReflowStart();
            if (ViewingRunningDay && _runner.Current is PlanBlock cur && list.IndexOf(cur) is int ci && ci >= 0)
            {
                locked = ci + 1;
                start += TimeSpan.FromMinutes(list.Take(locked).Sum(b => b.Minutes));
            }
            var head = list.Take(locked).ToList();
            var tail = list.Skip(locked).ToList();
            var keepInTail = keep != null && tail.Contains(keep) ? keep : null;
            tail = PlanReflow.Reflow(tail, start, keepInTail, _settings.FocusMinutes, _settings.BreakMinutes, ReflowEnd(list, endBefore));
            list = head.Concat(tail).ToList();
            list = PlanReflow.Rebalance(list, before, keep, _settings.FocusMinutes, _settings.BreakMinutes, locked);
            SyncPlanTo(list);
        }
        finally
        {
            _reflowing = false;
        }
        RecalcTimes();
        MarkPlanEdited();

        if (ViewingRunningDay && _runner.ScheduleStartLocal is DateTime dayStart)
        {
            // Tell the timer about the new times, and block whatever the current session now needs.
            _runner.Reschedule(_plan.ToList(), dayStart);
            ApplyBlockingForCurrent();
            SaveRunState();
        }
    }

    private TimeSpan ReflowStart() =>
        ViewingRunningDay && _runner.ScheduleStartLocal is DateTime s ? s.TimeOfDay
        : _startIsAuto ? TimeSpan.FromMinutes(Math.Floor(DateTime.Now.TimeOfDay.TotalMinutes)) : ParseDayStart();

    /// <summary>
    /// When the day should end: the end you set with Adjust working day, otherwise wherever it ended
    /// before this edit (so edits never make the day shorter or longer), or later if a fixed block runs past it.
    /// </summary>
    private TimeSpan ReflowEnd(List<PlanBlock> list, TimeSpan endBeforeEdit)
    {
        var end = _planEnd ?? endBeforeEdit;
        foreach (var b in list.Where(b => b.IsAnchored))
        {
            var anchorEnd = b.AnchorStart!.Value + TimeSpan.FromMinutes(b.Minutes);
            if (anchorEnd > end) end = anchorEnd;
        }
        return end;
    }

    /// <summary>Makes the plan on screen match <paramref name="target"/> with as few changes as possible.</summary>
    private void SyncPlanTo(List<PlanBlock> target)
    {
        for (int i = _plan.Count - 1; i >= 0; i--)
            if (!target.Contains(_plan[i])) _plan.RemoveAt(i);
        for (int i = 0; i < target.Count; i++)
        {
            if (i < _plan.Count && ReferenceEquals(_plan[i], target[i])) continue;
            int existing = _plan.IndexOf(target[i]);
            if (existing >= 0) _plan.Move(existing, i);
            else _plan.Insert(i, target[i]);
        }
    }

    /// <summary>Called after the day's start time changes.</summary>
    private void ReflowPlan()
    {
        if (_loadingPlan || ViewingRunningDay || !PlanReflow.HasAnchors(_plan)) return;
        ApplyEdit(_ => { }, keep: null);
    }

    private int InsertIndex() =>
        SafeInsertIndex(_selected != null && _plan.Contains(_selected) ? _plan.IndexOf(_selected) + 1 : _plan.Count);

    /// <summary>
    /// While the day runs: blocks that already happened can't change; the one happening now can only
    /// get longer; later ones can change freely.
    /// </summary>
    private void UpdateEditorLocks()
    {
        if (_selected == null || EditorMinutes == null) return;
        int running = RunningIndex;
        int index = _plan.IndexOf(_selected);
        bool past = running >= 0 && index < running;
        bool now = running >= 0 && index == running;
        EditorMinutes.IsEnabled = !past;
        EditorActions.IsEnabled = !past && !now;
        CalEditorMinutes.IsEnabled = !past;
        CalEditorActions.IsEnabled = !past && !now;
    }

    private void InsertAndSelect(PlanBlock block, int index)
    {
        ApplyEdit(list => list.Insert(Math.Clamp(index, 0, list.Count), block), keep: block);
        if (_plan.Contains(block)) SelectBlock(block);
    }

    /// <summary>A copy that can go anywhere: copies of fixed-time blocks aren't fixed.</summary>
    private static PlanBlock MovableCopy(PlanBlock source)
    {
        var copy = source.Clone();
        copy.AnchorStart = null;
        copy.MeetingStart = null;
        return copy;
    }

    private void AddPair_Click(object sender, RoutedEventArgs e)
    {
        int index = InsertIndex();
        var focus = NewFocus(_settings.FocusMinutes);
        var pause = NewBreak("Break", _settings.BreakMinutes);
        ApplyEdit(list =>
        {
            int i = Math.Clamp(index, 0, list.Count);
            list.Insert(i, focus);
            list.Insert(i + 1, pause);
        }, keep: focus);
        if (_plan.Contains(focus)) SelectBlock(focus);
    }

    private void DuplicateSelected()
    {
        if (_selected == null) return;
        InsertAndSelect(MovableCopy(_selected), SafeInsertIndex(_plan.IndexOf(_selected) + 1));
    }

    private void PasteBelow()
    {
        if (_clipboard == null)
        {
            Info("Copy a session or break first (right-click it, then Copy).", "Paste");
            return;
        }
        InsertAndSelect(MovableCopy(_clipboard), InsertIndex());
    }

    private void MoveSelected(int direction)
    {
        if (_selected == null || !CanChangeBlock(_selected)) return;
        if (_selected.IsAnchored)
        {
            Info("Meetings, lunch and the set break stay at their times. Move the sessions around them instead.", "Move");
            return;
        }
        int i = _plan.IndexOf(_selected);
        int j = i + direction;
        if (i < 0 || j < 0 || j >= _plan.Count) return;
        MoveBlockTo(_selected, direction > 0 ? j + 1 : j);
    }

    /// <summary>Moves a block so it sits before position <paramref name="index"/> (used by dragging too).</summary>
    private void MoveBlockTo(PlanBlock block, int index)
    {
        if (block.IsAnchored || !CanChangeBlock(block)) return;
        int old = _plan.IndexOf(block);
        if (old < 0) return;
        index = SafeInsertIndex(index); // can't drop before what's happening now
        int target = index > old ? index - 1 : index;
        if (target == old) return;
        ApplyEdit(list =>
        {
            list.Remove(block);
            list.Insert(Math.Clamp(target, 0, list.Count), block);
        }, keep: block);
        SelectBlock(block);
    }

    private void RemoveSelected()
    {
        if (_selected == null || !CanChangeBlock(_selected)) return;
        var removing = _selected;
        int i = _plan.IndexOf(removing);
        ApplyEdit(list =>
        {
            if (PlanReflow.HasAnchors(list)) PlanReflow.RemoveAndFill(list, removing, _settings.FocusMinutes, _settings.BreakMinutes);
            else list.Remove(removing);
        }, keep: null);
        if (_plan.Count > 0) SelectBlock(_plan[Math.Clamp(i, 0, _plan.Count - 1)]);
    }

    /// <summary>The Delete key removes the selected block (unless you're typing in a box).</summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (HandleShortcut(e)) { e.Handled = true; return; }
        // Ctrl+\ shrinks or expands the sidebar.
        if ((e.Key == System.Windows.Input.Key.Oem5 || e.Key == System.Windows.Input.Key.OemBackslash) &&
            System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            SetSidebarCollapsed(!_settings.SidebarCollapsed);
            e.Handled = true;
            return;
        }
        // Ctrl+K opens the command bar.
        if (e.Key == System.Windows.Input.Key.K && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            OpenCommandBar();
            e.Handled = true;
            return;
        }
        // Ctrl+Z undoes the last task or plan change (when you're not typing in a box).
        if (e.Key == System.Windows.Input.Key.Z && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control &&
            System.Windows.Input.Keyboard.FocusedElement is not TextBox)
        {
            Undo();
            e.Handled = true;
            return;
        }
        bool editingView = NavPlan.IsChecked == true || (NavCalendar.IsChecked == true && CalDayView.IsChecked == true);
        if (e.Key != System.Windows.Input.Key.Delete || !editingView || _selected == null) return;
        // Typing in a box: Delete edits the text, except right after clicking a block (title not yet changed).
        if (System.Windows.Input.Keyboard.FocusedElement is TextBox box &&
            !(ReferenceEquals(box, _freshTitle) && box.SelectionLength == box.Text.Length)) return;
        _freshTitle = null;
        RemoveSelected();
        e.Handled = true;
    }

    private void AddFocus_Click(object sender, RoutedEventArgs e)
    {
        InsertAndSelect(NewFocus(_settings.FocusMinutes), InsertIndex());
    }

    private void AddBreak_Click(object sender, RoutedEventArgs e)
    {
        InsertAndSelect(NewBreak("Break", _settings.BreakMinutes), InsertIndex());
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelected();

    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelected();

    private void CopySelected()
    {
        if (_selected == null) return;
        _clipboard = _selected.Clone();
        _toast.ShowMessage($"Copied \"{_selected.Name}\"", "Select a block and choose Paste below to add a copy after it.");
    }

    private void Paste_Click(object sender, RoutedEventArgs e) => PasteBelow();

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private ContextMenu BuildBlockMenu(PlanBlock block)
    {
        bool canEdit = CanChangeBlock(block, explain: false);
        var menu = new ContextMenu();
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Copy", CopySelected);
        Add("Paste below", PasteBelow, canEdit && _clipboard != null);
        Add("Duplicate", DuplicateSelected, canEdit);
        Add("Move up", () => MoveSelected(-1), canEdit);
        Add("Move down", () => MoveSelected(1), canEdit);
        Add("Delete", RemoveSelected, canEdit);
        return menu;
    }

    // ---------- Adjusting the working day ----------

    private void AdjustDay_Click(object sender, RoutedEventArgs e)
    {
        var start = ViewingRunningDay && _runner.ScheduleStartLocal is DateTime s ? s.TimeOfDay : ParseDayStart();
        var end = start + TimeSpan.FromMinutes(_plan.Sum(b => b.Minutes));
        AdjustStartBox.Text = DateTime.Today.Add(start).ToString("t");
        AdjustEndBox.Text = DateTime.Today.Add(end).ToString("t");
        AdjustStartBox.IsEnabled = !ViewingRunningDay;
        AdjustNote.Text = ViewingRunningDay
            ? "Your day is running, so only the end can change."
            : $"The usual hours are {DateTime.Today.Add(_settings.WorkdayStartTime):t} to {DateTime.Today.Add(_settings.WorkdayEndTime):t} (change them in Settings).";
        AdjustDayPopup.PlacementTarget = (UIElement)sender;
        AdjustDayPopup.IsOpen = true;
    }

    private void AdjustCancel_Click(object sender, RoutedEventArgs e) => AdjustDayPopup.IsOpen = false;

    private void AdjustApply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseTime(AdjustStartBox.Text, out var start) || !TryParseTime(AdjustEndBox.Text, out var end))
        {
            AdjustNote.Text = "Enter times like 7:30 AM and 4:00 PM.";
            return;
        }
        if (ViewingRunningDay && _runner.ScheduleStartLocal is DateTime runStart) start = runStart.TimeOfDay;
        if (end - start < TimeSpan.FromMinutes(30))
        {
            AdjustNote.Text = "The day needs to be at least 30 minutes long, with the end after the start.";
            return;
        }
        AdjustDayPopup.IsOpen = false;
        AdjustWorkingDay(start, end);
    }

    /// <summary>
    /// Moves the start and end of the day. Fixed blocks entirely outside the new hours (say, Devotions
    /// when the day now starts at 9) are taken out; sessions and breaks fill the new hours.
    /// </summary>
    private void AdjustWorkingDay(TimeSpan start, TimeSpan end)
    {
        _planWorkStart = start;
        _planEnd = end;
        if (!ViewingRunningDay) SetPinnedStart(start);
        int locked = RunningIndex;
        ApplyEdit(list =>
        {
            var outside = list.Where((b, i) => i > locked && b.IsAnchored &&
                (b.AnchorStart!.Value + TimeSpan.FromMinutes(b.Minutes) <= start || b.AnchorStart!.Value >= end)).ToList();
            foreach (var b in outside) list.Remove(b);
        }, keep: null);
        SelectBlock(null);
        _toast.ShowMessage("Working day adjusted", $"{DateTime.Today.Add(start):t} to {DateTime.Today.Add(end):t}");
    }

    // ---------- Blocking from the editor ----------

    private void EditorBlockApps_Click(object sender, RoutedEventArgs e)
    {
        var block = _selected;
        if (block == null) return;
        OpenPicker((UIElement)sender, block.BlockedApps, chosen => block.BlockedApps = chosen, lockCurrent: ViewingRunningDay);
    }

    private void EditorBlockList_Click(object sender, RoutedEventArgs e)
    {
        var block = _selected;
        if (block == null) return;
        OpenListPicker((UIElement)sender, block.BlockListIds, ids => block.BlockListIds = ids, lockCurrent: ViewingRunningDay);
    }

    private void RemoveAppFromBlock_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || (_ui.Running && ViewingRunningDay)) return;
        if ((sender as FrameworkElement)?.DataContext is not BlockedApp app) return;
        _selected.BlockedApps = _selected.BlockedApps.Where(a => a.Key != app.Key).ToList();
    }

    private void RemoveListFromBlock_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || (_ui.Running && ViewingRunningDay)) return;
        if ((sender as FrameworkElement)?.DataContext is not BlockList list) return;
        _selected.BlockListIds = _selected.BlockListIds.Where(id => id != list.Id).ToList();
    }
}
