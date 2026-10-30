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
        if (_mode == RunMode.DayPlan && _runner.IsRunning) AdjustRunningDayForCalendar();

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
    private void AdjustRunningDayForCalendar()
    {
        if (_runner.ScheduleStartLocal is not DateTime dayStart || _runner.Current is not PlanBlock current) return;
        try
        {
            var blocks = _runner.Blocks.ToList();
            int ci = blocks.IndexOf(current);
            var currentEnd = dayStart.TimeOfDay + TimeSpan.FromMinutes(blocks.Take(ci + 1).Sum(b => b.Minutes));

            // Meetings still to come, per the calendar (a meeting already under way starts when the current session ends).
            var wanted = CalendarService.BusyEventsOn(DateTime.Today)
                .Select(e => (Start: Max(e.Start.TimeOfDay, currentEnd), End: e.End.Date > DateTime.Today ? TimeSpan.FromHours(24) : e.End.TimeOfDay, e.Title))
                .Where(m => m.End > m.Start && m.End > currentEnd)
                .ToList();

            var planned = blocks.Skip(ci + 1).Where(b => b.IsMeeting && b.IsAnchored).ToList();
            var cancelled = planned.Where(p => !wanted.Any(w => w.Start == p.AnchorStart)).ToList();
            var added = wanted.Where(w => !planned.Any(p => p.AnchorStart == w.Start)).ToList();
            if (cancelled.Count == 0 && added.Count == 0) return;

            var before = blocks.Select(b => (b, b.Minutes)).ToList();
            var endBefore = dayStart.TimeOfDay + TimeSpan.FromMinutes(blocks.Sum(b => b.Minutes));
            var list = blocks.ToList();
            var skipped = new List<string>();

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
                // A meeting during the morning break replaces it (the plan rearranges around the meeting).
                var starts = Starts(list, dayStart.TimeOfDay);
                var overlapping = list.Select((b, i) => (b, s: starts[i]))
                    .Where(x => x.b.IsAnchored && x.s < m.End && x.s + TimeSpan.FromMinutes(x.b.Minutes) > m.Start)
                    .Select(x => x.b).ToList();
                foreach (var morningBreak in overlapping.Where(IsMorningBreak).ToList())
                {
                    if (list.IndexOf(morningBreak) <= list.IndexOf(current)) continue; // already happening or over
                    list.Remove(morningBreak);
                    overlapping.Remove(morningBreak);
                    starts = Starts(list, dayStart.TimeOfDay);
                }

                // Lunch, Devotions and other meetings keep their times; a meeting clashing with them is left out.
                bool clash = overlapping.Count > 0;
                if (clash)
                {
                    skipped.Add(m.Title);
                    continue;
                }

                int at = Enumerable.Range(ci + 1, Math.Max(0, list.Count - ci - 1)).FirstOrDefault(i => starts[i] >= m.Start, list.Count);
                var meeting = new PlanBlock
                {
                    Kind = BlockKind.Meeting,
                    Name = m.Title,
                    Minutes = Math.Max(1, (int)(m.End - m.Start).TotalMinutes),
                    AnchorStart = m.Start,
                    MeetingStart = DateTime.Today + m.Start
                };
                list.Insert(at, meeting);

                // The usual break after it, unless something fixed starts right away.
                var nextFixed = list.Skip(at + 1).Where(b => b.IsAnchored).Select(b => b.AnchorStart!.Value).DefaultIfEmpty(TimeSpan.FromHours(24)).Min();
                int pause = (int)Math.Min(Defaults.BreakAfterMeeting, (nextFixed - m.End).TotalMinutes);
                if (pause >= 1)
                    list.Insert(at + 1, new PlanBlock { Kind = BlockKind.Break, Name = "Break after meeting", Minutes = pause, AnchorStart = m.End });
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

            string what = string.Join(", ", added.Where(a => !skipped.Contains(a.Title)).Select(a => a.Title)
                .Concat(cancelled.Select(c => c.Name + " (cancelled)")));
            if (what.Length > 0) _toast.ShowMessage("Your day was adjusted for your calendar", what);
            if (skipped.Count > 0)
                _toast.ShowMessage("A meeting clashes with your plan", $"{string.Join(", ", skipped)} overlaps lunch, Devotions or another meeting, so it wasn't added.");
        }
        catch (Exception ex)
        {
            Log.Error("AdjustRunningDayForCalendar", ex);
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

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
            FillCalendarSettings();
            RebuildAutomaticPlanOnScreen();
        });

    private void DefaultLists_Click(object sender, RoutedEventArgs e) =>
        OpenListPicker((UIElement)sender, _settings.DefaultPlanLists, ids =>
        {
            _settings.DefaultPlanLists = ids;
            SaveSettingsQuietly();
            FillCalendarSettings();
            RebuildAutomaticPlanOnScreen();
        });

    /// <summary>Applies new default blocking to the plan on screen if it's still automatic.</summary>
    private void RebuildAutomaticPlanOnScreen()
    {
        if (!_planAutomatic || ViewingRunningDay) return;
        bool saved = TryLoadPlanFile(_planDate, out _);
        MakeAutomaticPlan(_planDate);
        if (saved) SavePlanNow(force: true);
    }
}
