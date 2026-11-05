using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FocusApp;

/// <summary>Plans for each date, automatic planning from the calendar, auto-start, and the Calendar tab.</summary>
public partial class MainWindow
{
    private DateTime _planDate = DateTime.Today;
    private bool _loadingPlan;
    private bool _planAutomatic;
    private bool _planAutoStart;
    private string _planFingerprint = "";

    /// <summary>When this day's work ends, if you adjusted it (otherwise the usual end from Settings).</summary>
    private TimeSpan? _planEnd;
    private bool _planDirty;
    private bool _syncingAutoStart;
    private bool _refreshingCalendars;
    private DateTime _calDate = DateTime.Today;
    private readonly DispatcherTimer _planSaveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _calendarTimer = new() { Interval = TimeSpan.FromMinutes(Defaults.CalendarRefreshMinutes) };

    /// <summary>A day plan is running and you're looking at it (so its times are locked).</summary>
    private bool ViewingRunningDay => _ui.PlanRunning && _planDate == DateTime.Today;

    // ---------- Loading and saving the plan for a date ----------

    /// <summary>The plan from before plans were saved per date becomes today's plan, once.</summary>
    private void MigrateOldPlan()
    {
        try
        {
            if (Storage.TryLoad<DayPlanFile>(DayPlanFile.FileFor(DateTime.Today), out _)) return;
            if (!Storage.TryLoad<DayLayout>("current-plan.json", out var old) || old.Blocks.Count == 0) return;
            var file = new DayPlanFile
            {
                Date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DayStart = old.DayStart,
                StartsNow = old.StartsNow,
                Blocks = old.Blocks
            };
            Storage.Save(DayPlanFile.FileFor(DateTime.Today), file);
            File.Move(Path.Combine(Storage.Folder, "current-plan.json"), Path.Combine(Storage.Folder, "current-plan.old.json"), true);
        }
        catch (Exception ex)
        {
            Log.Error("MigrateOldPlan", ex);
        }
    }

    private static bool TryLoadPlanFile(DateTime date, out DayPlanFile file) =>
        Storage.TryLoad(DayPlanFile.FileFor(date), out file) && file.Blocks.Count > 0;

    /// <summary>Shows the plan for a date: the saved one, or an automatic one built from the calendar.</summary>
    private void LoadPlanForDate(DateTime date)
    {
        SavePlanNow();
        _planDate = date.Date;
        _loadingPlan = true;
        try
        {
            bool haveFile = TryLoadPlanFile(_planDate, out var file);
            if (ViewingRunningDay)
            {
                ShareRunningPlan();
                _planAutomatic = haveFile && file.Automatic;
                _planAutoStart = haveFile && file.AutoStart;
                _planFingerprint = haveFile ? file.CalendarFingerprint : "";
                _planEnd = haveFile && TryParseTime(file.DayEnd, out var runEnd) ? runEnd : null;
            }
            else if (haveFile)
            {
                ClearPlan();
                if (TryParseTime(file.DayStart, out var savedStart) && !file.StartsNow)
                    PlanReflow.InferAnchors(file.Blocks, savedStart);
                foreach (var b in file.Blocks) _plan.Add(b);
                if (file.StartsNow || !TryParseTime(file.DayStart, out var start)) UseCurrentTime();
                else SetPinnedStart(start);
                _planAutomatic = file.Automatic;
                _planAutoStart = file.AutoStart;
                _planFingerprint = file.CalendarFingerprint;
                _planEnd = TryParseTime(file.DayEnd, out var savedEnd) ? savedEnd : null;
                _planWorkStart = TryParseTime(file.WorkStart, out var savedWorkStart) ? savedWorkStart : null;
            }
            else
            {
                _planEnd = null;
                _planWorkStart = null;
                _planAutoStart = false;
                MakeAutomaticPlan(_planDate);
            }
        }
        finally
        {
            _loadingPlan = false;
        }
        _planDirty = false;
        _planFileStamp = PlanFileStamp(_planDate);
        RefreshPlanTasks();
        SelectBlock(null);
        LayoutNameBox.Text = "";
        ApplyRunningEditRules();
        UpdateDayHeader();
        Timeline.ScrollTo(_planDate == DateTime.Today ? DateTime.Now.TimeOfDay - TimeSpan.FromMinutes(30) : ParseDayStart());
    }

    /// <summary>Fills the shown date around its meetings (doesn't save until something changes).</summary>
    private void MakeAutomaticPlan(DateTime date)
    {
        var busy = CalendarService.BusyEventsOn(date);
        var (blocks, start) = AutoPlanner.BuildDay(date, busy, _settings.FocusMinutes, _settings.BreakMinutes,
            _settings.DefaultPlanLists, _settings.DefaultPlanApps,
            _planWorkStart ?? _settings.WorkdayStartTime, _planEnd ?? _settings.WorkdayEndTime);
        bool wasLoading = _loadingPlan;
        _loadingPlan = true;
        try
        {
            ClearPlan();
            foreach (var b in blocks) _plan.Add(b);
            SetPinnedStart(start);
        }
        finally
        {
            _loadingPlan = wasLoading;
        }
        _planAutomatic = true;
        _planFingerprint = AutoPlanner.Fingerprint(busy);
    }

    /// <summary>Called whenever you change the plan.</summary>
    private void MarkPlanEdited()
    {
        if (_loadingPlan) return;
        _planAutomatic = false;
        _planDirty = true;
        _planSaveTimer.Stop();
        _planSaveTimer.Start();
        UpdateDayHeader();
    }

    private void SavePlanNow(bool force = false)
    {
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        _planSaveTimer.Stop();
        if (!_planDirty && !force) return;
        try
        {
            var file = new DayPlanFile
            {
                Date = _planDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DayStart = ParseDayStart().ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                StartsNow = _startIsAuto,
                Blocks = _plan.Select(b => b.Clone()).ToList(),
                Automatic = _planAutomatic,
                AutoStart = _planAutoStart,
                CalendarFingerprint = _planFingerprint,
                DayEnd = _planEnd?.ToString(@"hh\:mm", CultureInfo.InvariantCulture) ?? "",
                WorkStart = _planWorkStart?.ToString(@"hh\:mm", CultureInfo.InvariantCulture) ?? ""
            };
            Storage.Save(DayPlanFile.FileFor(_planDate), file);
            _planDirty = false;
            _planFileStamp = PlanFileStamp(_planDate);
        }
        catch (Exception ex)
        {
            Log.Error("SavePlanNow", ex);
        }
    }

    private void UpdateDayHeader()
    {
        if (PlanDateText == null) return;
        var today = DateTime.Today;
        PlanDateText.Text = _planDate == today ? "Today, " + _planDate.ToString("dddd MMMM d") : _planDate.ToString("dddd, MMMM d");
        TodayBtn.Visibility = _planDate == today ? Visibility.Collapsed : Visibility.Visible;

        _syncingAutoStart = true;
        AutoStartCheck.IsChecked = _planAutoStart;
        AutoStartCheck.IsEnabled = _planDate >= today;
        _syncingAutoStart = false;
        StartDayBtn.IsEnabled = _planDate == today;
        StartDayBtn.ToolTip = _planDate == today
            ? "Starts on the clock: if you start late, you join whichever session is happening now"
            : "Only today's plan can be started. Check \"Start this day automatically\" to have this day start by itself.";

        string status;
        if (_planDate < today) status = "This day has passed.";
        else if (_planDate > today && _planAutoStart)
            status = $"Starts automatically at {DateTime.Today.Add(ParseDayStart()):t} on {_planDate:dddd}. Focus just needs to be running (it starts with Windows).";
        else if (ViewingRunningDay) status = "This day is running. You can rename sessions and block more apps.";
        else if (_planAutomatic)
            status = CalendarService.Sources.Count == 0
                ? "Automatic plan. Connect your calendar in Settings to plan around meetings."
                : "Automatic plan from your calendar. It updates itself when meetings change, until you edit it.";
        else
        {
            status = "Edited by you.";
            if (CalendarService.Sources.Count > 0 && _planFingerprint.Length > 0 &&
                AutoPlanner.Fingerprint(CalendarService.BusyEventsOn(_planDate)) != _planFingerprint)
                status += " Your calendar has changed since this was planned. Click Auto-fill day to plan around it again.";
        }
        PlanStatusText.Text = status;
    }

    private void PrevDay_Click(object sender, RoutedEventArgs e) => LoadPlanForDate(_planDate.AddDays(-1));
    private void NextDay_Click(object sender, RoutedEventArgs e) => LoadPlanForDate(_planDate.AddDays(1));
    private void Today_Click(object sender, RoutedEventArgs e) => LoadPlanForDate(DateTime.Today);

    private void AutoFill_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditStructure()) return;
        if (!_planAutomatic && _plan.Count > 0 &&
            !Ask("Replace this plan with a new one built around your meetings, the break, and lunch?", "Auto-fill day"))
            return;
        PushUndo(includePlan: true);
        MakeAutomaticPlan(_planDate);
        _planDirty = true;
        SavePlanNow();
        SelectBlock(null);
        UpdateDayHeader();
    }

    private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingAutoStart) return;
        _planAutoStart = AutoStartCheck.IsChecked == true;
        SavePlanNow(force: true);
    }

    // ---------- When the calendar changes ----------

    private async System.Threading.Tasks.Task RefreshCalendarsAsync()
    {
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        if (_refreshingCalendars || CalendarService.Sources.Count == 0) return;
        _refreshingCalendars = true;
        try { await CalendarService.RefreshAsync(); }
        catch (Exception ex) { Log.Error("RefreshCalendars", ex); }
        finally
        {
            _refreshingCalendars = false;
            FillCalendarSettings();
        }
    }

    private void Calendar_Updated()
    {
        // A running day takes in new, moved or cancelled meetings (for the part that hasn't happened yet).
        // Today's running or hand-made plan: new or cancelled meetings are asked about (or fitted in, or shown on top, per Settings).
        HandleCalendarChangesForPlannedDay();

        // The plan on screen follows the calendar while it's still automatic (and not running).
        if (_planAutomatic && !ViewingRunningDay && _planDate >= DateTime.Today)
        {
            string fingerprint = AutoPlanner.Fingerprint(CalendarService.BusyEventsOn(_planDate));
            if (fingerprint != _planFingerprint)
            {
                bool saved = TryLoadPlanFile(_planDate, out _);
                MakeAutomaticPlan(_planDate);
                SelectBlock(null);
                if (saved) SavePlanNow(force: true);
            }
        }
        UpdateSavedAutomaticPlans();
        UpdateDayHeader();
        RefreshCalendarTab();
    }

    /// <summary>
    /// While today's plan runs and the calendar changes: meetings that were added or moved are fitted in
    /// at their times (with the usual break after), cancelled ones are taken out, and the focus sessions
    /// and breaks around them reshape within their limits. Lunch, the set breaks, and the session
    /// happening now aren't touched.
    /// </summary>
    /// <param name="only">Just these meetings (title, calendar start); null = every new one.</param>
    /// <param name="cancelledToo">Also take out meetings the calendar no longer has.</param>
    private void AdjustRunningDayForCalendar(Func<string, TimeSpan, bool>? only = null, bool cancelledToo = true)
    {
        if (_runner.ScheduleStartLocal is not DateTime dayStart || _runner.Current is not PlanBlock current) return;
        try
        {
            var blocks = _runner.Blocks.ToList();
            int ci = blocks.IndexOf(current);
            var currentEnd = dayStart.TimeOfDay + TimeSpan.FromMinutes(blocks.Take(ci + 1).Sum(b => b.Minutes));

            // Meetings still to come, per the calendar (a meeting already under way starts when the current session ends).
            var wanted = CalendarService.BusyEventsOn(DateTime.Today)
                .Where(e => !e.AllDay)
                .Select(e => (Start: Max(e.Start.TimeOfDay, currentEnd), End: e.End.Date > DateTime.Today ? TimeSpan.FromHours(24) : e.End.TimeOfDay, e.Title, Raw: e.Start.TimeOfDay))
                .Where(m => m.End > m.Start && m.End > currentEnd)
                .ToList();

            var planned = blocks.Skip(ci + 1).Where(b => b.IsMeeting && b.IsAnchored).ToList();
            var cancelled = !cancelledToo ? new List<PlanBlock>() : planned.Where(p => !wanted.Any(w => SameMeeting(p, w.Start, w.Raw))).ToList();
            var added = wanted.Where(w => !planned.Any(p => SameMeeting(p, w.Start, w.Raw)) && (only == null || only(w.Title, w.Raw))).ToList();
            if (cancelled.Count == 0 && added.Count == 0) return;

            // A meeting's start is non-negotiable: one that begins during the current focus session or break ends it
            // then (or right now, if it has already begun), and the meeting starts on time. Another meeting under way
            // isn't cut.
            if (!current.IsMeeting && added.Count > 0)
            {
                var first = added.Min(m => m.Raw);
                var cutAt = Max(first, DateTime.Now.TimeOfDay);
                var curStart = currentEnd - TimeSpan.FromMinutes(current.Minutes);
                if (cutAt < currentEnd)
                {
                    current.Minutes = Math.Max(1, (int)Math.Ceiling((cutAt - curStart).TotalMinutes));
                    currentEnd = curStart + TimeSpan.FromMinutes(current.Minutes);
                    added = added.Select(m => (Start: Max(m.Raw, currentEnd), m.End, m.Title, m.Raw)).ToList();
                }
            }

            var before = blocks.Select(b => (b, b.Minutes)).ToList();
            var endBefore = dayStart.TimeOfDay + TimeSpan.FromMinutes(blocks.Sum(b => b.Minutes));
            var list = blocks.ToList();
            var skipped = new List<string>();
            var skippedNames = new List<string>();

            foreach (var gone in cancelled)
            {
                int gi = list.IndexOf(gone);
                // Its "break after meeting" goes with it.
                if (gi + 1 < list.Count && list[gi + 1].IsAnchored && list[gi + 1].Name == "Break after meeting")
                    PlanReflow.RemoveAndFill(list, list[gi + 1], _settings.FocusMinutes, _settings.BreakMinutes);
                PlanReflow.RemoveAndFill(list, gone, _settings.FocusMinutes, _settings.BreakMinutes);
            }

            foreach (var m in added.OrderBy(m => m.Start))
            {
                if (!PlaceMeeting(list, dayStart.TimeOfDay, ci + 1, m.Start, m.End, m.Raw, m.Title, out var why)) { skipped.Add($"{m.Title} ({why})"); skippedNames.Add(m.Title); }
            }

            // Reshape everything after the current session, keeping it within the limits.
            int locked = list.IndexOf(current) + 1;
            var head = list.Take(locked).ToList();
            var tail = PlanReflow.Reflow(list.Skip(locked).ToList(), dayStart.TimeOfDay + TimeSpan.FromMinutes(head.Sum(b => b.Minutes)),
                null, _settings.FocusMinutes, _settings.BreakMinutes, ReflowEnd(list, endBefore));
            list = PlanReflow.Rebalance(head.Concat(tail).ToList(), before, null, _settings.FocusMinutes, _settings.BreakMinutes, locked);

            _runner.Reschedule(list, dayStart);
            ApplyBlockingForCurrent();
            SaveRunState();
            if (_planDate == DateTime.Today)
            {
                ShareRunningPlan();
                _planDirty = true;
                SavePlanNow();
            }
            else
            {
                Storage.Save(DayPlanFile.FileFor(DateTime.Today), new DayPlanFile
                {
                    Date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DayStart = dayStart.TimeOfDay.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                    Blocks = list.Select(b => b.Clone()).ToList()
                });
            }

            string what = string.Join(", ", added.Where(a => !skippedNames.Contains(a.Title)).Select(a => a.Title)
                .Concat(cancelled.Select(c => c.Name + " (cancelled)")));
            if (what.Length > 0) _toast.ShowMessage("Your day was adjusted for your calendar", what);
            if (skipped.Count > 0)
                _toast.ShowMessage("A meeting clashes with your plan", $"{string.Join(", ", skipped)}: it stays shown on top of your day.");
        }
        catch (Exception ex)
        {
            Log.Error("AdjustRunningDayForCalendar", ex);
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>The set break and lunch: the only fixed blocks a new meeting may move or skip.</summary>
    private static bool IsBreakOrLunch(PlanBlock b) => b.IsAnchored && (IsMorningBreak(b) || b.Name == Defaults.LunchName);

    /// <summary>A planned meeting matches a calendar meeting by its place in the plan or its calendar start.</summary>
    private static bool SameMeeting(PlanBlock p, TimeSpan start, TimeSpan rawStart) =>
        p.AnchorStart == start || p.MeetingStart?.TimeOfDay == rawStart;

    /// <summary>
    /// Puts a calendar meeting into a plan at its time (blocks before <paramref name="firstFree"/> are left alone):
    /// a meeting already going on when it starts makes it begin when that one ends; the set break and lunch are
    /// skipped that day if it covers them completely, or nudged just before or after it if it only overlaps them
    /// (the only time they move); then the usual break after it. It isn't added if it would cover another meeting
    /// that starts during it, and <paramref name="why"/> says so.
    /// </summary>
    private bool PlaceMeeting(List<PlanBlock> list, TimeSpan dayStart, int firstFree, TimeSpan start, TimeSpan end, TimeSpan rawStart, string title, out string why)
    {
        why = "";
        List<TimeSpan> starts() => Starts(list, dayStart);
        var st = starts();
        TimeSpan EndOf(int i) => st[i] + TimeSpan.FromMinutes(list[i].Minutes);
        TimeSpan locked = firstFree <= 0 ? dayStart : EndOf(Math.Min(firstFree, list.Count) - 1);
        start = Max(start, locked);

        // 1. A meeting already under way: this one follows it (that meeting's own break after is dropped).
        for (int i = 0; i < list.Count; i++)
            if (list[i].IsMeeting && list[i].IsAnchored && st[i] <= start && EndOf(i) > start) start = EndOf(i);
        if (start >= end) { why = "covered by another meeting"; return false; }
        int after = list.FindIndex(b => b.IsAnchored && b.Name == "Break after meeting" && b.AnchorStart == start);
        if (after >= firstFree && after >= 0) { list.RemoveAt(after); st = starts(); }

        // 2. The set break and lunch: skipped if covered, nudged if only touched.
        foreach (var b in list.Skip(firstFree).Where(IsBreakOrLunch).ToList())
        {
            int bi = list.IndexOf(b);
            var bs = st[bi]; var be = bs + TimeSpan.FromMinutes(b.Minutes);
            if (bs >= end || be <= start) continue;
            list.Remove(b);
            if (bs < start || be > end)
            {
                var len = TimeSpan.FromMinutes(b.Minutes);
                var moved = bs < start && start - len >= locked ? start - len : end;
                b.AnchorStart = moved;
                st = starts();
                int at2 = Enumerable.Range(firstFree, Math.Max(0, list.Count - firstFree)).FirstOrDefault(k => st[k] >= moved, list.Count);
                list.Insert(at2, b);
            }
            st = starts();
        }

        // 3. Another meeting (or fixed block) starting during it: it stays on top instead.
        for (int i = firstFree; i < list.Count; i++)
            if (list[i].IsAnchored && st[i] < end && EndOf(i) > start && !(IsBreakOrLunch(list[i]) && list[i].AnchorStart == end))
            { why = $"overlaps {list[i].Name}"; return false; }

        int at = Enumerable.Range(firstFree, Math.Max(0, list.Count - firstFree)).FirstOrDefault(k => st[k] >= start, list.Count);
        list.Insert(at, new PlanBlock { Kind = BlockKind.Meeting, Name = title, Minutes = Math.Max(1, (int)(end - start).TotalMinutes), AnchorStart = start, MeetingStart = DateTime.Today + rawStart });
        var nextFixed = list.Skip(at + 1).Where(b => b.IsAnchored).Select(b => b.AnchorStart!.Value).DefaultIfEmpty(TimeSpan.FromHours(24)).Min();
        int pause = (int)Math.Min(Defaults.BreakAfterMeeting, (nextFixed - end).TotalMinutes);
        if (pause >= 1) list.Insert(at + 1, new PlanBlock { Kind = BlockKind.Break, Name = "Break after meeting", Minutes = pause, AnchorStart = end });
        return true;
    }

    private static bool IsMorningBreak(PlanBlock b) =>
        b.IsAnchored && b.Kind == BlockKind.Break && b.Name == Defaults.MorningBreakName && b.AnchorStart == Defaults.MorningBreakStart;

    private static List<TimeSpan> Starts(List<PlanBlock> list, TimeSpan dayStart)
    {
        var starts = new List<TimeSpan>(list.Count);
        var t = dayStart;
        foreach (var b in list)
        {
            starts.Add(t);
            t += TimeSpan.FromMinutes(b.Minutes);
        }
        return starts;
    }

    /// <summary>Saved future plans that you haven't edited are rebuilt when their meetings change.</summary>
    private void UpdateSavedAutomaticPlans()
    {
        try
        {
            string folder = Path.Combine(Storage.SharedFolder, "plans");
            if (!Directory.Exists(folder)) return;
            foreach (var path in Directory.GetFiles(folder, "*.json"))
            {
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                if (date < DateTime.Today || date == _planDate) continue;
                if (date == DateTime.Today && _ui.PlanRunning) continue;
                if (!TryLoadPlanFile(date, out var file) || !file.Automatic) continue;

                var busy = CalendarService.BusyEventsOn(date);
                string fingerprint = AutoPlanner.Fingerprint(busy);
                if (fingerprint == file.CalendarFingerprint) continue;
                var (blocks, start) = AutoPlanner.BuildDay(date, busy, _settings.FocusMinutes, _settings.BreakMinutes,
                    _settings.DefaultPlanLists, _settings.DefaultPlanApps,
            _settings.WorkdayStartTime, _settings.WorkdayEndTime);
                file.Blocks = blocks;
                file.DayStart = start.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                file.StartsNow = false;
                file.CalendarFingerprint = fingerprint;
                Storage.Save(DayPlanFile.FileFor(date), file);
            }
        }
        catch (Exception ex)
        {
            Log.Error("UpdateSavedAutomaticPlans", ex);
        }
    }

    // ---------- Starting by itself ----------

    /// <summary>
    /// Starts today's plan by itself if you asked for that (for this day, or every workday),
    /// once its first session has begun. Never restarts a day you stopped.
    /// </summary>
    private void CheckAutoStart()
    {
        if (_runner.IsRunning) return;
        string today = DateTime.Today.ToString("yyyy-MM-dd");
        if (_settings.LastAutoStartDate == today) return;

        bool workday = DateTime.Today.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        bool wanted = _settings.AutoStartWorkdays && workday;
        if (!wanted)
            wanted = _planDate == DateTime.Today ? _planAutoStart : TryLoadPlanFile(DateTime.Today, out var f) && f.AutoStart;
        if (!wanted) return;

        if (_planDate != DateTime.Today) LoadPlanForDate(DateTime.Today);
        if (_plan.Count == 0) return;
        var start = _startIsAuto ? DateTime.Now : DateTime.Today + ParseDayStart();
        var end = start.AddMinutes(_plan.Sum(b => b.Minutes));
        if (DateTime.Now < start) return;
        if (DateTime.Now >= end)
        {
            _settings.LastAutoStartDate = today;
            SaveSettingsQuietly();
            return;
        }

        StartTodayPlan(automatic: true);
        if (_runner.IsRunning)
        {
            SaveSettingsQuietly();
            _toast.ShowMessage("Your day has started", "Focus started today's plan automatically.");
        }
    }

    // ---------- Calendar tab ----------

    private void SetUpCalendarTab()
    {
        CalDay.RangeStart = Defaults.CalendarStart;
        CalDay.RangeEnd = Defaults.CalendarEnd;
        CalDay.ShowNowLine = true;
        // The calendar's day view edits the same plan as the Day plan tab.
        CalDay.BlockClicked += block =>
        {
            SelectBlock(block);
            FocusTitle(CalTitleBox);
        };
        CalDay.MenuFor = BuildBlockMenu;
        CalDay.CanDrag = b => !b.IsAnchored && (RunningIndex < 0 || _plan.IndexOf(b) > RunningIndex);
        CalDay.BlockDropped += (block, index) => MoveBlockTo(block, index);
        CalDay.TaskDropped += AddTaskToSession;
        SyncWeekPlanChecks();
        CalWeek.ShowPlan = _settings.WeekShowsPlan;
        CalWeek.RangeStart = Defaults.CalendarStart;
        CalWeek.RangeEnd = Defaults.CalendarEnd;
        CalWeek.DayClicked += day =>
        {
            _calDate = day;
            CalDayView.IsChecked = true;
        };
        _calendarTimer.Tick += async (_, _) => await RefreshCalendarsAsync();
        _calendarTimer.Start();
        _planSaveTimer.Tick += (_, _) => SavePlanNow();
        FillCalendarSettings();
        Dispatcher.BeginInvoke(new Action(async () => await RefreshCalendarsAsync()), DispatcherPriority.Background);
    }

    /// <summary>The plan for any date, without changing what the Day plan shows.</summary>
    private (IReadOnlyList<PlanBlock> Blocks, TimeSpan Start) PlanFor(DateTime date)
    {
        if (date.Date == _planDate) return (_plan.ToList(), _startIsAuto ? DateTime.Now.TimeOfDay : ParseDayStart());
        if (TryLoadPlanFile(date, out var file))
            return (file.Blocks, file.StartsNow || !TryParseTime(file.DayStart, out var s) ? _settings.WorkdayStartTime : s);
        var (blocks, start) = AutoPlanner.BuildDay(date, CalendarService.BusyEventsOn(date), _settings.FocusMinutes,
            _settings.BreakMinutes, _settings.DefaultPlanLists, _settings.DefaultPlanApps,
            _settings.WorkdayStartTime, _settings.WorkdayEndTime);
        return (blocks, start);
    }

    private void RefreshCalendarTab()
    {
        if (CalendarPanel == null || CalendarPanel.Visibility != Visibility.Visible) return;
        bool week = CalWeekView.IsChecked == true;
        CalDay.Visibility = week ? Visibility.Collapsed : Visibility.Visible;
        CalEditor.Visibility = week ? Visibility.Collapsed : Visibility.Visible;
        CalWeek.Visibility = week ? Visibility.Visible : Visibility.Collapsed;

        // Showing a day here opens it for editing too (the Day plan tab follows along).
        if (!week && _calDate.Date != _planDate) LoadPlanForDate(_calDate);

        CalShowPlanCheck.Visibility = week ? Visibility.Visible : Visibility.Collapsed;
        if (week)
        {
            CalWeek.ShowPlan = _settings.WeekShowsPlan;
            int sinceMonday = ((int)_calDate.DayOfWeek + 6) % 7;
            var monday = _calDate.AddDays(-sinceMonday);
            CalDateText.Text = $"Week of {monday:MMMM d}";
            CalWeek.Show(_calDate, PlanFor);
        }
        else
        {
            CalDateText.Text = _calDate == DateTime.Today ? "Today, " + _calDate.ToString("dddd MMMM d") : _calDate.ToString("dddd, MMMM d");
            var (blocks, start) = PlanFor(_calDate);
            CalDay.Events = CalendarService.EventsOn(_calDate);
            CalDay.Selected = _selected;
            CalDay.Show(blocks, _calDate + start);
            CalDay.ScrollTo(_calDate == DateTime.Today ? DateTime.Now.TimeOfDay - TimeSpan.FromMinutes(45) : start);
        }

        string info;
        if (CalendarService.Sources.Count == 0)
            info = "No calendar connected yet. Add one in Settings, under Calendars.";
        else
        {
            var allDay = CalendarService.EventsOn(_calDate).Where(e => e.AllDay).Select(e => e.Title).ToList();
            info = string.Join("  ", CalendarService.Sources.Select(s => $"{s.Name}: {s.Status}."));
            if (!week && allDay.Count > 0) info = "All day: " + string.Join(", ", allDay) + ".   " + info;
        }
        CalInfoText.Text = info;
    }

    private bool _syncingWeekPlanChecks;

    /// <summary>Show or hide your plan in the week views (the setting is shared and remembered).</summary>
    private void WeekShowPlan_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingWeekPlanChecks || sender is not System.Windows.Controls.CheckBox box || CalWeek == null || DashWeek == null) return;
        _settings.WeekShowsPlan = box.IsChecked == true;
        SaveSettingsQuietly();
        SyncWeekPlanChecks();
        CalWeek.ShowPlan = DashWeek.ShowPlan = _settings.WeekShowsPlan;
        CalWeek.RequestRender();
        DashWeek.RequestRender();
    }

    private void SyncWeekPlanChecks()
    {
        _syncingWeekPlanChecks = true;
        CalShowPlanCheck.IsChecked = _settings.WeekShowsPlan;
        DashShowPlanCheck.IsChecked = _settings.WeekShowsPlan;
        _syncingWeekPlanChecks = false;
    }

    private void CalPrev_Click(object sender, RoutedEventArgs e) { _calDate = _calDate.AddDays(CalWeekView.IsChecked == true ? -7 : -1); RefreshCalendarTab(); }
    private void CalNext_Click(object sender, RoutedEventArgs e) { _calDate = _calDate.AddDays(CalWeekView.IsChecked == true ? 7 : 1); RefreshCalendarTab(); }
    private void CalToday_Click(object sender, RoutedEventArgs e) { _calDate = DateTime.Today; RefreshCalendarTab(); }
    private void CalView_Checked(object sender, RoutedEventArgs e) => RefreshCalendarTab();

    private void CalOpenInPlan_Click(object sender, RoutedEventArgs e)
    {
        var block = _selected;
        NavPlan.IsChecked = true;
        if (block != null) SelectBlock(block);
    }

    private void CalPlanDay_Click(object sender, RoutedEventArgs e)
    {
        LoadPlanForDate(_calDate);
        NavPlan.IsChecked = true;
    }

    // ---------- Calendar settings ----------

    private void FillCalendarSettings()
    {
        if (CalSourcesList == null) return;
        CalSourcesList.ItemsSource = CalendarService.Sources.ToList();
        DefaultAppsBtn.Content = AppsLabel(_settings.DefaultPlanApps);
        DefaultListsBtn.Content = ListsLabel(_settings.DefaultPlanLists);
        AutoStartWorkdaysCheck.IsChecked = _settings.AutoStartWorkdays;
    }

    private async void AddCalendar_Click(object sender, RoutedEventArgs e)
    {
        string? problem = CalendarService.CheckLink(CalUrlBox.Text, out var uri);
        if (problem != null || uri == null)
        {
            Info(problem ?? "That link can't be used.", "Add calendar");
            return;
        }
        string name = CalNameBox.Text.Trim();
        CalendarService.AddLink(name.Length > 0 ? name : "Calendar", uri);
        CalUrlBox.Text = "";
        FillCalendarSettings();
        await RefreshCalendarsAsync();
        UpdateDayHeader();
    }

    private async void ImportCalendar_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a calendar file", Filter = "Calendar files (*.ics)|*.ics", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string name = CalNameBox.Text.Trim();
            CalendarService.AddFile(name.Length > 0 ? name : Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName);
        }
        catch (Exception ex)
        {
            Info("Couldn't import that file: " + ex.Message, "Import calendar");
            return;
        }
        FillCalendarSettings();
        await RefreshCalendarsAsync();
    }

    private async void RemoveCalendar_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CalendarSource source) return;
        if (!Ask($"Stop reading \"{source.Name}\"?", "Remove calendar", defaultNo: true)) return;
        CalendarService.Remove(source);
        CalendarService.CleanUpFiles();
        FillCalendarSettings();
        if (CalendarService.Sources.Count == 0) await CalendarService.RefreshAsync(); // clears events
        else await RefreshCalendarsAsync();
    }

    private async void RefreshCalendars_Click(object sender, RoutedEventArgs e)
    {
        if (CalendarService.Sources.Count == 0)
        {
            Info("Add a calendar link first.", "Calendars");
            return;
        }
        await RefreshCalendarsAsync();
    }

    private void DefaultApps_Click(object sender, RoutedEventArgs e) =>
        OpenPicker((UIElement)sender, _settings.DefaultPlanApps, chosen =>
        {
            _settings.DefaultPlanApps = chosen;
            SaveSettingsQuietly();
            SaveSharedPlanDefaults();
            FillCalendarSettings();
            RebuildAutomaticPlanOnScreen();
        });

    private void DefaultLists_Click(object sender, RoutedEventArgs e) =>
        OpenListPicker((UIElement)sender, _settings.DefaultPlanLists, ids =>
        {
            var before = _settings.DefaultPlanLists.ToList();
            _settings.DefaultPlanLists = ids;
            SaveSettingsQuietly();
            SaveSharedPlanDefaults();
            FillCalendarSettings();
            RebuildAutomaticPlanOnScreen();
            OfferDefaultsOnPlannedDays(before, ids);
        });

    // ---------- Default block lists, shared by your computers ----------

    /// <summary>
    /// Takes the default block lists and apps from plan-defaults.json (synced), so every computer fills focus sessions
    /// the same way. The first time, this computer's own defaults go into the file.
    /// </summary>
    private void AdoptSharedPlanDefaults()
    {
        try
        {
            if (Storage.TryLoad<PlanDefaults>("plan-defaults.json", out var shared) && shared.UpdatedUtc != default)
            {
                bool same = shared.Lists.SequenceEqual(_settings.DefaultPlanLists) && shared.PauseWeekends == _settings.PauseWeekends &&
                            shared.PracticeEndOn == _settings.PracticeEndOn && shared.PracticeEnd == _settings.PracticeEnd &&
                            shared.Apps.Select(a => a.Key).SequenceEqual(_settings.DefaultPlanApps.Select(a => a.Key));
                if (same) return;
                _settings.PauseWeekends = shared.PauseWeekends;
                _settings.PracticeEndOn = shared.PracticeEndOn;
                _settings.PracticeEnd = shared.PracticeEnd;
                ShowPracticeEnd();
                if (PauseWeekendsCheck != null) PauseWeekendsCheck.IsChecked = shared.PauseWeekends;
                _settings.DefaultPlanLists = shared.Lists.Where(id => BlockLists.Find(id) != null).Distinct().ToList();
                _settings.DefaultPlanApps = shared.Apps.Select(a => a.Copy()).ToList();
                SaveSettingsQuietly();
                if (IsLoaded) FillCalendarSettings();
            }
            else if (_settings.DefaultPlanLists.Count > 0 || _settings.DefaultPlanApps.Count > 0 || _settings.PauseWeekends) SaveSharedPlanDefaults();
        }
        catch (Exception ex) { Log.Error("AdoptSharedPlanDefaults", ex); }
    }

    private void SaveSharedPlanDefaults()
    {
        try
        {
            Storage.Save("plan-defaults.json", new PlanDefaults
            {
                Lists = _settings.DefaultPlanLists.ToList(),
                Apps = _settings.DefaultPlanApps.Select(a => a.Copy()).ToList(),
                PauseWeekends = _settings.PauseWeekends,
                PracticeEndOn = _settings.PracticeEndOn,
                PracticeEnd = _settings.PracticeEnd,
                UpdatedUtc = DateTime.UtcNow
            });
        }
        catch (Exception ex) { Log.Error("SaveSharedPlanDefaults", ex); }
    }

    /// <summary>After changing the default block lists: offers to change them in the focus sessions of days already planned (today on).</summary>
    private void OfferDefaultsOnPlannedDays(List<string> before, List<string> after)
    {
        var added = after.Except(before).ToList();
        var removed = before.Except(after).ToList();
        if (added.Count == 0 && removed.Count == 0) return;
        try
        {
            string folder = Path.Combine(Storage.SharedFolder, "plans");
            var dates = new List<DateTime>();
            if (Directory.Exists(folder))
                foreach (var path in Directory.GetFiles(folder, "*.json"))
                    if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) &&
                        d >= DateTime.Today && !(d == DateTime.Today && _ui.PlanRunning) && d != _planDate)
                        dates.Add(d);
            bool screen = !ViewingRunningDay && !_planAutomatic && _plan.Any(b => b.IsFocus);
            if (dates.Count == 0 && !screen) return;
            string change = string.Join(" and ", new[]
            {
                added.Count > 0 ? "add " + string.Join(", ", added.Select(id => BlockLists.Find(id)?.Name ?? "a list")) : "",
                removed.Count > 0 ? "take off " + string.Join(", ", removed.Select(id => BlockLists.Find(id)?.Name ?? "a removed list")) : ""
            }.Where(x => x.Length > 0));
            if (!Ask($"Also {change} in the focus sessions you've already planned (today and later days)?" +
                     (_ui.PlanRunning ? "\n\nToday's running day isn't changed; edit its sessions on the Day plan." : ""), "Default block lists")) return;
            List<string> Fix(List<string> ids) => ids.Where(id => !removed.Contains(id)).Concat(added.Where(id => !ids.Contains(id))).ToList();
            foreach (var d in dates)
            {
                if (!TryLoadPlanFile(d, out var file)) continue;
                bool changed = false;
                foreach (var b in file.Blocks.Where(b => b.IsFocus))
                {
                    var fixedIds = Fix(b.BlockListIds);
                    if (!fixedIds.SequenceEqual(b.BlockListIds)) { b.BlockListIds = fixedIds; changed = true; }
                }
                if (changed) Storage.Save(DayPlanFile.FileFor(d), file);
            }
            if (screen)
            {
                foreach (var b in _plan.Where(b => b.IsFocus)) b.BlockListIds = Fix(b.BlockListIds);
                MarkPlanEdited();
                SavePlanNow(force: true);
            }
        }
        catch (Exception ex) { Log.Error("OfferDefaultsOnPlannedDays", ex); }
    }

    /// <summary>Applies new default blocking to the plan on screen if it's still automatic.</summary>
    private void RebuildAutomaticPlanOnScreen()
    {
        if (!_planAutomatic || ViewingRunningDay) return;
        bool saved = TryLoadPlanFile(_planDate, out _);
        MakeAutomaticPlan(_planDate);
        if (saved) SavePlanNow(force: true);
    }
}
