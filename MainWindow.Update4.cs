using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// Update 4: extending sessions, new meetings, filling part of the day, leftover time, tasks with a
/// time, website blocking, the floating timer's tasks, daily records, calendar sharing, themes and
/// weekends.
/// </summary>
public partial class MainWindow
{
    private DateTime _lastCalendarShare = DateTime.MinValue, _lastRecordWrite = DateTime.MinValue;

    private void SetUpUpdate4()
    {
        try { SetUpSettingsPage(); } catch (Exception ex) { Log.Error("SetUpSettingsPage", ex); }
        try { SetUpPersonal(); } catch (Exception ex) { Log.Error("SetUpPersonal", ex); }

        // How tasks look (Default, Compact or Paper); refreshed when the theme changes, since it uses the theme's colors.
        TaskLook.Apply(_settings.TaskLook);
        ThemeManager.Changed += () => Dispatcher.BeginInvoke(new Action(() => TaskLook.Apply(_settings.TaskLook)));

        // Tasks on calendar blocks.
        ApplyBlockTasksSetting();

        // The floating timer's tasks.
        _overlay.TaskToggled += OverlayTaskToggled;
        _overlay.PinChanged += pinned => { _settings.OverlayPinned = pinned; SaveSettingsQuietly(); };
        _overlay.ViewChanged += view => { _settings.OverlayView = view; _settings.OverlayToday = view == "today"; SaveSettingsQuietly(); UpdateOverlayTasks(); };
        _overlay.HideRequested += () =>
        {
            _overlayChoice = false;
            _settings.OverlayHidden = true; // stays hidden (even after a restart) until the dashboard's 🌡 brings it back
            SaveSettingsQuietly();
            UpdateOverlayVisibility();
        };
        _overlay.PersonalProjects = () => PersonalTasks.ProjectList.Select(pr => (pr.Id, pr.Name)).ToList();
        _overlay.PersonalScopeChanged += scope => { _settings.OverlayPersonalScope = scope; SaveSettingsQuietly(); };
        _overlay.SetPersonalScope(_settings.OverlayPersonalScope ?? "today");
        if (_settings.OverlayHidden) _overlayChoice = false;
        _overlay.SetView(_settings.OverlayView.Length > 0 ? _settings.OverlayView : _settings.OverlayToday ? "today" : "session");
        _overlay.SetRows(_settings.OverlayRows);
        _overlay.SetPinned(_settings.OverlayPinned);
        // Show the floating timer as Focus opens (unless switched off), even before anything runs.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_runner.IsRunning) ShowOverlayIdle();
            UpdateOverlayTasks();
            UpdateOverlayVisibility();
        }), System.Windows.Threading.DispatcherPriority.Loaded);

        // Filling part of the day.
        Timeline.RangeSelected += FillRange;

        // Leftovers from the old way of blocking websites (the hosts file) are cleaned up once.
        if (HostsBlocker.IsActive())
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Ask("An older version of Focus left website blocking switched on in Windows. Remove it now? (Windows will ask for permission once.)", "Websites"))
                    HostsBlocker.Request(false, Array.Empty<string>());
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // Daily records for past days that don't have one yet.
        Dispatcher.BeginInvoke(new Action(BackfillDailyRecords), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Runs every 30 seconds (with the reminders).</summary>
    private void Update4Tick()
    {
        if (DateTime.Now - _lastCalendarShare > TimeSpan.FromMinutes(15)) WriteCalendarShare();
        if (DateTime.Now - _lastRecordWrite > TimeSpan.FromMinutes(5)) WriteDailyRecord(DateTime.Today);
        UpdateOverlayTasks();
    }

    private void RefreshAllTimelines()
    {
        foreach (var t in new[] { Timeline, CalDay, DashDay, HomeDay }) t?.RequestRender();
    }

    private void ApplyBlockTasksSetting()
    {
        const bool on = true; // always shown (small, clean type); the old switch for this is gone
        foreach (var t in new[] { Timeline, CalDay, DashDay, HomeDay }) if (t != null) t.ShowTasks = on;
        RefreshAllTimelines();
    }

    // ---------- Extending a session ----------

    private void Extend_Click(object sender, RoutedEventArgs e) => OpenExtend((UIElement)sender);

    private void OpenExtend(UIElement anchor)
    {
        if (!_runner.IsRunning || _runner.Current == null)
        {
            _toast.ShowMessage("Nothing to extend", "Start a session first.");
            return;
        }
        ExtendSlider.Value = Math.Clamp(_settings.ExtendMinutes, 1, 60);
        ExtendPopup.PlacementTarget = anchor;
        ExtendPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => ExtendSlider.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ExtendSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ExtendValue != null) ExtendValue.Text = $"+{(int)ExtendSlider.Value} min";
    }

    private void ExtendApply_Click(object sender, RoutedEventArgs e) => ApplyExtend();

    private void ExtendPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; ApplyExtend(); }
        else if (e.Key == Key.Escape) { e.Handled = true; ExtendPopup.IsOpen = false; }
    }

    /// <summary>Lengthens the current session. In a day plan the next blocks make room; meetings, lunch and your day's end stay put.</summary>
    private void ApplyExtend()
    {
        int minutes = (int)Math.Round(ExtendSlider.Value);
        ExtendPopup.IsOpen = false;
        if (minutes < 1 || _runner.Current is not PlanBlock cur) return;
        _settings.ExtendMinutes = minutes;
        SaveSettingsQuietly();

        if (_mode == RunMode.DayPlan && _runner.IsOnSchedule)
        {
            if (!ViewingRunningDay) LoadPlanForDate(DateTime.Today);
            if (!_plan.Contains(cur)) return;
            cur.Minutes += minutes; // the plan reshapes around it
        }
        else _runner.ExtendCurrent(TimeSpan.FromMinutes(minutes));
        SaveRunState();
        _toast.ShowMessage($"{cur.Name} extended by {minutes} min", $"Now ends at {DateTime.Now + _runner.Remaining:t}.");
    }

    // ---------- New meetings after you've planned ----------

    private readonly HashSet<string> _meetingAnswers = new();

    /// <summary>
    /// What happens when the calendar gains or loses meetings on a day you've already planned:
    /// show them on top (the plan stays), fit them in, or ask each time.
    /// </summary>
    private void HandleCalendarChangesForPlannedDay()
    {
        bool running = _mode == RunMode.DayPlan && _runner.IsRunning;
        bool editedToday = !running && !_planAutomatic && _planDate == DateTime.Today;
        if (!running && !editedToday) return;

        var blocks = running ? _runner.Blocks.ToList() : _plan.ToList();
        var dayStart = running && _runner.ScheduleStartLocal is DateTime ds ? ds.TimeOfDay : ParseDayStart();
        int ci = running && _runner.Current is PlanBlock current ? blocks.IndexOf(current) : -1;
        var from = dayStart + TimeSpan.FromMinutes(blocks.Take(ci + 1).Sum(b => b.Minutes));
        if (!running) from = Max(from, DateTime.Now.TimeOfDay);

        var wanted = CalendarService.BusyEventsOn(DateTime.Today)
            .Where(e => !e.AllDay && e.End.TimeOfDay > from)
            .Select(e => (Start: Max(e.Start.TimeOfDay, from), e.Title)).ToList();
        var planned = blocks.Skip(ci + 1).Where(b => b.IsMeeting && b.IsAnchored).ToList();
        var added = wanted.Where(w => !planned.Any(p => p.AnchorStart == w.Start)).ToList();
        var cancelled = planned.Where(p => !wanted.Any(w => w.Start == p.AnchorStart)).ToList();
        if (added.Count == 0 && cancelled.Count == 0) return;

        string key = string.Join("|", added.Select(a => a.Start + a.Title).Concat(cancelled.Select(c => "-" + c.AnchorStart + c.Name)));
        string mode = _settings.NewMeetings;
        if (mode == "overlay" || _meetingAnswers.Contains(key)) return;
        if (mode == "ask")
        {
            _meetingAnswers.Add(key);
            string what = string.Join(", ", added.Select(a => $"{a.Title} at {DateTime.Today.Add(a.Start):t}")
                .Concat(cancelled.Select(c => $"{c.Name} (cancelled)")));
            Notify("meeting:" + key, added.Count > 0 ? "A new meeting in your day" : "A meeting was cancelled",
                $"Your calendar changed: {what}. Fit it into your plan, or keep your plan as it is (the meeting shows on top).",
                ("Fit into my plan", () =>
                {
                    if (_mode == RunMode.DayPlan && _runner.IsRunning) AdjustRunningDayForCalendar();
                    else FitMeetingsIntoPlan(added, cancelled);
                }),
                ("Keep my plan", () => { }));
            return;
        }
        if (running) AdjustRunningDayForCalendar();
        else FitMeetingsIntoPlan(added, cancelled);
    }

    /// <summary>For today's plan before it starts: adds meetings at their times (with a break after) and removes cancelled ones.</summary>
    private void FitMeetingsIntoPlan(List<(TimeSpan Start, string Title)> added, List<PlanBlock> cancelled)
    {
        var events = CalendarService.BusyEventsOn(DateTime.Today);
        ApplyEdit(list =>
        {
            foreach (var gone in cancelled)
            {
                int gi = list.IndexOf(gone);
                if (gi < 0) continue;
                if (gi + 1 < list.Count && list[gi + 1].IsAnchored && list[gi + 1].Name == "Break after meeting")
                    PlanReflow.RemoveAndFill(list, list[gi + 1], _settings.FocusMinutes, _settings.BreakMinutes);
                PlanReflow.RemoveAndFill(list, gone, _settings.FocusMinutes, _settings.BreakMinutes);
            }
            foreach (var (start, title) in added.OrderBy(a => a.Start))
            {
                var ev = events.FirstOrDefault(e => e.Title == title && Max(e.Start.TimeOfDay, start) == start);
                var end = ev != null ? ev.End.TimeOfDay : start + TimeSpan.FromMinutes(30);
                var starts = Starts(list, ParseDayStart());
                bool clash = list.Select((b, i) => (b, s: starts[i]))
                    .Any(x => x.b.IsAnchored && !IsMorningBreak(x.b) && x.s < end && x.s + TimeSpan.FromMinutes(x.b.Minutes) > start);
                if (clash) continue;
                list.RemoveAll(b => IsMorningBreak(b) && b.AnchorStart < end && b.AnchorStart + TimeSpan.FromMinutes(b.Minutes) > start);
                starts = Starts(list, ParseDayStart());
                int at = Enumerable.Range(0, list.Count).FirstOrDefault(i => starts[i] >= start, list.Count);
                list.Insert(at, new PlanBlock { Kind = BlockKind.Meeting, Name = title, Minutes = Math.Max(1, (int)(end - start).TotalMinutes), AnchorStart = start, MeetingStart = DateTime.Today + start });
                var nextFixed = list.Skip(at + 1).Where(b => b.IsAnchored).Select(b => b.AnchorStart!.Value).DefaultIfEmpty(TimeSpan.FromHours(24)).Min();
                int pause = (int)Math.Min(Defaults.BreakAfterMeeting, (nextFixed - end).TotalMinutes);
                if (pause >= 1) list.Insert(at + 1, new PlanBlock { Kind = BlockKind.Break, Name = "Break after meeting", Minutes = pause, AnchorStart = end });
            }
        }, keep: null);
        _toast.ShowMessage("Your plan was adjusted for your calendar", string.Join(", ", added.Select(a => a.Title)));
    }

    // ---------- Filling part of the day ----------

    private void FillPart_Click(object sender, RoutedEventArgs e)
    {
        Timeline.SelectingRange = !Timeline.SelectingRange;
        FillPartHint.Visibility = Timeline.SelectingRange ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Fills a chosen stretch of the day with focus sessions and breaks. Meetings and set breaks keep
    /// their times; sessions and breaks the range touches are replaced (whole blocks, so nothing is
    /// left half-cut).
    /// </summary>
    private void FillRange(TimeSpan from, TimeSpan to)
    {
        Timeline.SelectingRange = false;
        FillPartHint.Visibility = Visibility.Collapsed;
        try
        {
            var dayStart = ParseDayStart();
            var list = _plan.ToList();
            var starts = Starts(list, dayStart);
            int running = RunningIndex;

            // Grow the range to whole blocks, never into the past of a running day.
            var lockedUntil = running >= 0 ? starts[running] + TimeSpan.FromMinutes(list[running].Minutes) : TimeSpan.Zero;
            from = Max(from, lockedUntil);
            for (int i = 0; i < list.Count; i++)
            {
                var s = starts[i];
                var e = s + TimeSpan.FromMinutes(list[i].Minutes);
                if (list[i].IsAnchored || i <= running) continue;
                if (s < from && e > from) from = s;
                if (s < to && e > to) to = e;
            }
            if (to - from < TimeSpan.FromMinutes(10)) return;

            // Keep everything outside the range; inside it keep only fixed blocks.
            var before = list.Where((b, i) => starts[i] + TimeSpan.FromMinutes(b.Minutes) <= from).ToList();
            var after = list.Where((b, i) => starts[i] >= to).ToList();
            var fixedInside = list.Where((b, i) => b.IsAnchored && starts[i] < to && starts[i] + TimeSpan.FromMinutes(b.Minutes) > from).ToList();

            var items = fixedInside.Select(b => new FixedItem(b.AnchorStart!.Value, b.AnchorStart!.Value + TimeSpan.FromMinutes(b.Minutes), b.Name, b.Kind));
            var filled = AutoPlanner.Build(from, to, items, _settings.FocusMinutes, _settings.BreakMinutes, _planDate);
            // Fixed blocks come back as themselves (with their tasks and settings).
            for (int i = 0; i < filled.Count; i++)
                if (filled[i].IsAnchored && fixedInside.FirstOrDefault(f => f.AnchorStart == filled[i].AnchorStart && f.Kind == filled[i].Kind) is PlanBlock original)
                    filled[i] = original;
            foreach (var b in filled.Where(b => b.IsFocus && !b.IsAnchored)) ApplyDefaultBlocking(b);

            PushUndo(includePlan: true);
            ApplyEdit(l => { l.Clear(); l.AddRange(before); l.AddRange(filled); l.AddRange(after); }, keep: null);
            NumberFocusSessions();
            _toast.ShowMessage("Filled", $"{DateTime.Today.Add(from):t} to {DateTime.Today.Add(to):t} with focus sessions and breaks.", "Undo", Undo);
        }
        catch (Exception ex)
        {
            Log.Error("FillRange", ex);
        }
    }

    /// <summary>Your default block lists and apps (Settings, Automatic plans) on a new focus session.</summary>
    private void ApplyDefaultBlocking(PlanBlock b)
    {
        b.BlockListIds = _settings.DefaultPlanLists.ToList();
        b.BlockedApps = _settings.DefaultPlanApps.Select(a => a.Copy()).ToList();
    }

    /// <summary>Renames "Focus session N" blocks in order after big changes (custom names are left alone).</summary>
    private void NumberFocusSessions()
    {
        int n = 0;
        foreach (var b in _plan.Where(b => b.IsFocus))
        {
            n++;
            if (System.Text.RegularExpressions.Regex.IsMatch(b.Name, @"^Focus session \d+$")) b.Name = $"Focus session {n}";
        }
    }

    // ---------- Leftover time ----------

    /// <summary>"Make this a focus session": turns a break or leftover gap into a focus session (one click, undoable).</summary>
    private void MakeFocus_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not PlanBlock b || b.IsFocus || b.IsAnchored) return;
        PushUndo(includePlan: true);
        b.Kind = BlockKind.Focus;
        b.Name = "Focus session";
        ApplyDefaultBlocking(b);
        CloseBlockEditor();
        NumberFocusSessions();
        _planDirty = true;
        SavePlanNow();
        RefreshAllTimelines();
        _toast.ShowMessage("Now a focus session", $"{b.Minutes} min.", "Undo", Undo);
    }

    // ---------- Tasks with a time go into the session at that time ----------

    /// <summary>
    /// A task with a time (a reminder) on the day being planned goes into the focus session happening at
    /// that time, or the next one after (if the time falls in a break or meeting). It stays in your list
    /// until it's finished, and follows along if the plan changes.
    /// </summary>
    private void PlaceTimedTasks()
    {
        if (!_settings.On("timedTasks") || _placingTimed) return;
        _placingTimed = true;
        try
        {
            bool running = ViewingRunningDay;
            var blocks = running ? _runner.Blocks.ToList() : _plan.ToList();
            if (blocks.Count == 0) return;
            var start = running && _runner.ScheduleStartLocal is DateTime ds ? ds.TimeOfDay : ParseDayStart();
            var starts = Starts(blocks, start);
            int locked = running ? RunningIndex : -1;
            bool changed = false;

            foreach (var task in TaskBin.All.Where(t => t.RemindAt is DateTime r && r.Date == _planDate.Date).ToList())
            {
                var at = task.RemindAt!.Value.TimeOfDay;
                int target = -1;
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (!blocks[i].IsFocus || i < locked) continue;
                    var end = starts[i] + TimeSpan.FromMinutes(blocks[i].Minutes);
                    if (at < end) { target = i; break; }
                }
                if (target < 0) continue;
                for (int i = Math.Max(0, locked + 1); i < blocks.Count; i++)
                {
                    if (i == target) continue;
                    var stale = blocks[i].Tasks.Where(x => x.BinId == task.Id && x.RemindAt != null).ToList();
                    foreach (var s in stale) { blocks[i].Tasks.Remove(s); changed = true; }
                }
                if (!blocks[target].Tasks.Any(x => x.BinId == task.Id))
                {
                    blocks[target].Tasks.Add(LinkedCopy(task));
                    changed = true;
                }
            }
            if (!changed) return;
            if (running) SaveRunState();
            if (_planDate == DateTime.Today || !running) { _planDirty = true; SavePlanNow(); }
            RefreshDashTasks();
            RefreshPlanTasks();
            RefreshAllTimelines();
        }
        catch (Exception ex)
        {
            Log.Error("PlaceTimedTasks", ex);
        }
        finally
        {
            _placingTimed = false;
        }
    }

    private bool _placingTimed;

    // ---------- Website blocking ----------





    // ---------- The floating timer's tasks ----------

    private void UpdateOverlayTasks()
    {
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        try { BuildOverlayTasks(); }
        catch (Exception ex) { Log.Error("UpdateOverlayTasks", ex); }
    }

    private void BuildOverlayTasks()
    {
        _overlay.ShowTasksOnHover = true; // the caret always works
        // Nothing running: today's tasks, whichever view is chosen.
        // Personal: today's personal tasks (or all of them, if none are for today).
        if (_overlay.View == "personal")
        {
            // The caret beside Personal chooses Today, This week, All, or one project.
            string scope = _overlay.PersonalScope;
            string? project = scope.StartsWith("project:") ? scope[8..] : null;
            var mine = PersonalTasks.View(project != null ? "all" : scope, project).ToList();
            string head = "";
            if (mine.Count == 0 && scope == "today") { mine = PersonalTasks.View("all").ToList(); head = "Nothing for today: all personal tasks"; }
            _overlay.SetTasks(mine.Select(t => new OverlayTask { Source = t, Kind = "personal", Id = t.Id, Key = "p" + t.Id, Text = t.Text, IsHigh = t.IsHigh }).ToList(), head, "personal",
                              "No personal tasks yet.");
            return;
        }
        if (_overlay.TodayView || !_runner.IsRunning)
        {
            _overlay.SetTasks(TodayOverlayTasks(), _overlay.TodayView ? "" : "Nothing running: today's tasks", _overlay.View,
                              "Nothing picked, due or overdue today.");
            return;
        }

        static List<OverlayTask> Wrap(IEnumerable<TaskItem> items) => items.Where(t => t.Text.Trim().Length > 0)
            .Where(t => !t.Done) // ticked: gone from the drop-down (still ticked on the dashboard)
            .Select(t => new OverlayTask { Source = t, Kind = "session", Id = t.Id, Key = t.Id, Text = t.Text, IsHigh = t.IsHigh, Done = t.Done }).ToList();
        if (_runner.Current is not PlanBlock cur) { _overlay.SetTasks(TodayOverlayTasks(), "Nothing running: today's tasks", "session", "Nothing picked, due or overdue today."); return; }
        if (cur.IsFocus) { _overlay.SetTasks(Wrap(cur.Tasks), "", "session", "No tasks for this session yet."); return; }
        var next = _runner.Blocks.Skip(_runner.Index + 1).FirstOrDefault(b => b.IsFocus);
        _overlay.SetTasks(next != null ? Wrap(next.Tasks) : new List<OverlayTask>(), next != null ? $"Up next: {next.Name}" : "", "session",
                          next != null ? "No tasks for the next session yet." : "A break: no more focus sessions after it.");
    }


    /// <summary>Today's tasks for the floating timer: picked for today, due today, overdue, or placed in today's sessions.</summary>
    private List<OverlayTask> TodayOverlayTasks()
    {
            // Today's tasks: picked for today, due today, overdue, or placed in today's sessions.
            var placed = new HashSet<string>();
            try
            {
                var (blocks, _) = TodaysSchedule();
                foreach (var id in blocks.SelectMany(b => b.Tasks).Select(t => t.BinId).Where(id => !string.IsNullOrEmpty(id))) placed.Add(id);
            }
            catch (Exception ex) { Log.Error("Overlay today's plan", ex); } // no plan today: still show picked, due and overdue
            var today = TaskBin.All
                .Where(t => !t.IsSnoozed && (t.IsPlannedToday || t.IsDueToday || t.IsOverdue || placed.Contains(t.Id)))
                .OrderBy(TaskBase.Rank).ThenBy(t => t.Due ?? DateTime.MaxValue)
                .Select(t => new OverlayTask { Source = t, Kind = "main", Id = t.Id, Key = t.Id, Text = t.Text, IsHigh = t.IsHigh }).ToList();
        return today;
    }

    /// <summary>A task ticked on the floating timer: a session task is checked off; one of today's is finished.</summary>
    private void OverlayTaskToggled(OverlayTask t)
    {
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        try
        {
            // Find the task as it is now, by its ID (the drop-down's copy may be out of date).
            switch (t.Kind)
            {
                case "session":
                    var item = _runner.Blocks.SelectMany(b => b.Tasks).FirstOrDefault(x => x.Id == t.Id);
                    if (item != null) item.Done = t.Done;
                    else Log.Write("Floating timer: couldn't find the session task " + t.Text);
                    break;
                case "personal":
                    if (t.Done && PersonalTasks.All.FirstOrDefault(x => x.Id == t.Id) is BinTask mine) PersonalTasks.Finish(mine);
                    break;
                case "main":
                    if (t.Done && TaskBin.All.FirstOrDefault(x => x.Id == t.Id) is BinTask task)
                    {
                        PushUndo(includePlan: true);
                        FinishListTask(task, task.From, task.Spent);
                    }
                    break;
            }
            if (_runner.IsRunning) SaveRunState();
            RefreshDashTasks();
            RefreshAllTimelines();
            UpdateOverlayTasks();
        }
        catch (Exception ex) { Log.Error("OverlayTaskToggled", ex); }
    }

    // ---------- Sharing your calendar with the phone app ----------

    /// <summary>Saves the next three weeks of meetings to the Focus folder, for the phone app's calendar.</summary>
    private void WriteCalendarShare()
    {
        _lastCalendarShare = DateTime.Now;
        try
        {
            var events = new List<SharedEvent>();
            for (int i = -1; i < 21; i++)
                foreach (var e in CalendarService.EventsOn(DateTime.Today.AddDays(i)))
                    if (!events.Any(x => x.Title == e.Title && x.Start == e.Start))
                        events.Add(new SharedEvent { Title = e.Title, Start = e.Start, End = e.End, AllDay = e.AllDay, Busy = e.Busy });
            Storage.Save("calendar-share.json", events.OrderBy(e => e.Start).ToList());
        }
        catch (Exception ex)
        {
            Log.Error("WriteCalendarShare", ex);
        }
    }
}
