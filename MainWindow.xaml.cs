using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FocusApp;

public partial class MainWindow : Window
{
    private enum RunMode { None, DayPlan, Pomodoro, QuickFocus }

    private int WarningSeconds => _settings.WarningSeconds;

#if DEBUG
    private static readonly bool IsTestBuild = true;
#else
    private static readonly bool IsTestBuild = false;
#endif

    private readonly ObservableCollection<PlanBlock> _plan = new();
    private readonly ObservableCollection<DayLayout> _layouts = new();
    private readonly SessionRunner _runner = new();
    private readonly AppBlocker _blocker = new();
    private readonly OverlayWindow _overlay;
    private readonly BlockedToast _toast;
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _quitting;

    // Locked mode is captured when a run starts, so changing the setting mid-session changes nothing.
    private bool _lockedRun;
    private bool _extremeRun;
    private bool _syncingLockChecks;

    // Extreme mode's short breaks from blocking.
    private DateTime? _passEndsUtc;

    // Extreme mode's helper process, which reopens Focus if it's force-closed.
    private Process? _watchdog;
    private DateTime _lastWatchdogCheckUtc = DateTime.MinValue;

    // True while picking up a session saved from last time (no flash or chime for that).
    private bool _resuming;

    // Used to skip the full-screen flash right after you start or skip something yourself.
    private DateTime _lastUserActionUtc = DateTime.MinValue;
    private CloseWarningWindow? _warning;
    private AppSettings _settings;
    private RunMode _mode = RunMode.None;

    // The plan's start time follows the clock until you type a specific time.
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _startIsAuto = true;
    private string _autoStartText = "";

    // Things the screen binds to (for example, hiding "remove" buttons while a session runs).
    private readonly UiState _ui = new();

    public MainWindow()
    {
        InitializeComponent();
        Application.Current.MainWindow = this;

        // Created after the main window so Windows treats this one as the main window.
        _overlay = new OverlayWindow();
        _toast = new BlockedToast();
        _tray = CreateTrayIcon();

        _settings = Storage.Load<AppSettings>("settings.json");
        _settings.Sanitize();
        _settings.StartWithWindows = true; // Focus always starts with Windows
        Sync.Init(); // before loading anything that syncs
        ThemeManager.ColorTheme = _settings.ColorTheme;
        ThemeManager.Apply(_settings.Theme);
        ApplyCustomization();
        DataContext = _ui;

        BlockLists.Load();
        BlockLists.Changed += BlockLists_Changed;
        AdoptSharedPlanDefaults(); // default block lists for focus sessions, shared by your computers
        ListsList.ItemsSource = null;
        RefreshListsTab();

        foreach (var layout in Storage.Load<List<DayLayout>>("layouts.json").OrderBy(l => l.Name))
            _layouts.Add(layout);
        LayoutList.ItemsSource = _layouts;
        _layouts.CollectionChanged += (_, _) => UpdateLayoutHint();
        UpdateLayoutHint();

        _plan.CollectionChanged += Plan_CollectionChanged;
        SetUpTimeline();

        // Calendars (read from the copy on disk first, then refreshed in the background).
        CalendarService.Load();
        CalendarService.Updated += Calendar_Updated;
        SetUpCalendarTab();
        SetUpDashboard();
        SetUpTasks();
        SetUpSync();
        SetUpQuickAdd();
        SetUpUpdate3();
        // The floating timer's list, ✕ and views, Settings saving by themselves, Personal (and its quick add), the task look.
        // (This step had been left out, so none of those were switched on.) A problem in it never stops Focus from opening.
        try { SetUpUpdate4(); } catch (Exception ex) { Log.Error("SetUpUpdate4", ex); }
        SetUpVocab();
        SetUpHome();
        SetUpPopupKeys();
        // Pop-up calendars on the task editor's date fields (DatePopup.cs).
        try { DatePopup.Attach(TaskDueBox); DatePopup.Attach(TaskSnoozeBox); } catch (Exception ex) { Log.Error("DatePopup", ex); }
        try { SetUpTraining(); } catch (Exception ex) { Log.Error("SetUpTraining", ex); } // the book course (MainWindow.Training.cs)
        try { SetUpFreedom(); } catch (Exception ex) { Log.Error("SetUpFreedom", ex); } // Freedom window and starting Freedom with focus sessions (MainWindow.Freedom.cs)
        try { SetUpUpdates(); } catch (Exception ex) { Log.Error("SetUpUpdates", ex); } // tray "Check for updates" and the quiet check (MainWindow.Updates.cs)
        Nav_Checked(NavDashboard, new RoutedEventArgs()); // open on the dashboard
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(new Action(ShowWelcomeIfNeeded), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        BuildGuide();
        ApplyFeatures();
        MigrateOldPlan();
        LoadPlanForDate(DateTime.Today);

        _clockTimer.Tick += (_, _) => FollowClock();
        _clockTimer.Start();

        FillSettingFields();
        FillPomodoroFields();
        FillLockedChecks();
        UpdateQuickBlockButtons();
        Timeline.ScrollTo(DateTime.Now.TimeOfDay - TimeSpan.FromMinutes(30));

        _runner.Tick += UpdateRunPanel;
        _runner.BlockStarted += Runner_BlockStarted;
        _runner.Finished += Runner_Finished;

        _blocker.AppBlocked += Blocker_AppBlocked;
        _blocker.WarningStarted += Blocker_WarningStarted;

        _overlay.OpenRequested += RestoreFromOverlay;
        _overlay.LocationChanged += (_, _) =>
        {
            if (!_overlay.IsVisible) return;
            _settings.OverlayLeft = _overlay.Left;
            _settings.OverlayTop = _overlay.Top;
        };

        StateChanged += (_, _) => UpdateOverlayVisibility();
        IsVisibleChanged += (_, _) => UpdateOverlayVisibility();
        Closing += MainWindow_Closing;
        Application.Current.SessionEnding += (_, _) =>
        {
            // Windows is shutting down or signing out: save and let it close.
            _quitting = true;
            SaveRunState(windowsShuttingDown: true);
            SaveStats();
            SavePlanNow();
            SaveSettingsQuietly();
            _tray.Visible = false;
        };
        RecalcTimes();

        ApplyStartupSetting();
        _reminderTimer.Tick += (_, _) => CheckReminder();
        _reminderTimer.Start();

        // Start finding installed apps in the background so the picker opens quickly.
        _ = LoadCatalogAsync();

        // Pick up a session that was running when Focus last closed (crash, restart, or force-close).
        Dispatcher.BeginInvoke(new Action(ResumeSavedRun), DispatcherPriority.Background);
    }

    // ---------- Saving and resuming a running session ----------

    private void SaveRunState(bool windowsShuttingDown = false)
    {
        var state = _runner.Capture();
        if (state == null) return;
        state.Mode = _mode.ToString();
        state.Locked = _lockedRun;
        state.Extreme = _extremeRun;
        state.WindowsShuttingDown = windowsShuttingDown;
        try { Storage.Save("run-state.json", state); }
        catch (Exception ex) { Log.Error("SaveRunState", ex); }
    }

    private void ResumeSavedRun()
    {
        try
        {
            if (_runner.IsRunning) return;
            if (!Storage.TryLoad<RunState>("run-state.json", out var state) || !state.IsActive) return;

            _mode = Enum.TryParse<RunMode>(state.Mode, out var mode) ? mode : RunMode.DayPlan;
            _resuming = true;
            bool resumed;
            try { resumed = _runner.Restore(state); }
            finally { _resuming = false; }

            if (!resumed)
            {
                _mode = RunMode.None;
                Storage.Delete("run-state.json");
                return;
            }
            _lockedRun = state.Locked || state.Extreme;
            _extremeRun = state.Extreme;
            if (_mode == RunMode.DayPlan)
            {
                _ui.PlanRunning = true;
                if (_planDate != DateTime.Today) LoadPlanForDate(DateTime.Today);
                else ShareRunningPlan();
            }
            SaveRunState();
            UpdateRunPanel();
            Log.Write("Resumed the session that was running when Focus last closed.");
            _sitesBlockedByUs = HostsBlocker.IsActive();
        }
        catch (Exception ex)
        {
            Log.Error("ResumeSavedRun", ex);
        }
    }

    /// <summary>In extreme mode, keeps a helper running that reopens Focus if it's force-closed.</summary>
    private void MaintainWatchdog()
    {
        if (!_extremeRun || _quitting) return;
        if (DateTime.UtcNow - _lastWatchdogCheckUtc < TimeSpan.FromSeconds(3)) return;
        _lastWatchdogCheckUtc = DateTime.UtcNow;
        try
        {
            if (_watchdog != null && !_watchdog.HasExited) return;
            _watchdog?.Dispose();
            _watchdog = null;
            if (Environment.ProcessPath is not string exe) return;
            _watchdog = Process.Start(new ProcessStartInfo(exe, $"--watchdog {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            Log.Error("MaintainWatchdog", ex);
            _watchdog = null;
        }
    }

    // ---------- Tray, quitting, start-up, reminder ----------

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Focus", null, (_, _) => ShowFromTray());
        menu.Items.Add("Quit Focus", null, (_, _) => QuitApp());

        var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Focus",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray();
        };
        tray.BalloonTipClicked += (_, _) =>
        {
            ShowFromTray();
            if (_reviewFromBalloon)
            {
                _reviewFromBalloon = false;
                OpenReview();
            }
        };
        return tray;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (info != null)
            {
                using var stream = info.Stream;
                return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        UpdateOverlayVisibility();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;

        // Closing the window hides it; Focus keeps running in the tray.
        e.Cancel = true;
        Hide();
        if (!_settings.ShownTrayTip)
        {
            _settings.ShownTrayTip = true;
            SaveSettingsQuietly();
            _tray.ShowBalloonTip(8000, "Focus is still running",
                "It's in the system tray by the clock, so reminders and blocking keep working. Right-click the icon to quit.",
                System.Windows.Forms.ToolTipIcon.None);
        }
    }

    private void QuitApp()
    {
        if (ExtremeNow)
        {
            ShowFromTray();
            Info($"Extreme lock is on, so Focus can't be closed until this focus session ends at {(DateTime.Now + _runner.Remaining):t}.\n\n" +
                 "If you need your apps, use the 3-minute or 8-minute break in the Focus window.", "Extreme lock");
            return;
        }
        if (_runner.IsRunning)
        {
            ShowFromTray();
            bool ok = _runner.Current?.IsFocus == true
                ? ConfirmEndFocus("quit Focus and end this focus session")
                : Ask("A break is running. Quit Focus?", "Quit Focus");
            if (!ok) return;
        }

        _quitting = true;
        RemoveQuickAdd();
        SetUpFocusKey(false);
        StopWebsiteBlocking();
        SaveStats();
        _runner.Stop();
        _blocker.End();
        SavePlanNow();
        SaveSettingsQuietly();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>Adds or removes Focus from the programs Windows starts at sign-in.</summary>
    private void ApplyStartupSetting()
    {
        if (IsTestBuild) return; // test runs from the terminal shouldn't register themselves
        try
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(runKey);
            if (_settings.StartWithWindows && Environment.ProcessPath is string exe)
                key.SetValue("Focus", $"\"{exe}\" --startup");
            else
                key.DeleteValue("Focus", throwOnMissingValue: false);
        }
        catch
        {
            // Not critical; Focus still works, it just won't start by itself.
        }
    }

    /// <summary>Shows the morning reminder once a day, at the set time or soon after if the PC was off.</summary>
    private void CheckReminder()
    {
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        CheckAutoStart();
        CheckNudges();
        CheckTaskReminders();
        CheckReviewTime();
        RunAutomations();
        UpdateGoals();
        CheckVocab();
        CheckNotices();
        try { Update4Tick(); } catch (Exception ex) { Log.Error("Update4Tick", ex); } // the phone's calendar, daily records, the timer's list
        if (SyncMerge.AnyWaiting) { TaskBin.Save(); ResolvedTasks.Save(); Vocab.Save(); Alphabet.Instance.Save(); }
        if (!_settings.MorningReminderOn) return;
        var now = DateTime.Now;
        var at = DateTime.Today + _settings.ReminderTimeOfDay;
        if (now < at || now >= at.AddHours(5)) return;

        string today = DateTime.Today.ToString("yyyy-MM-dd");
        if (_settings.LastReminderDate == today) return;
        _settings.LastReminderDate = today;
        SaveSettingsQuietly();

        if (_runner.IsRunning) return; // already focusing, no need to nag
        int overdue = TaskBin.All.Count(t => t.IsOverdue), dueToday = TaskBin.All.Count(t => t.IsDueToday);
        string taskNote = overdue + dueToday == 0 ? "" :
            $" You have {(dueToday > 0 ? $"{dueToday} task{(dueToday == 1 ? "" : "s")} due today" : "")}" +
            $"{(dueToday > 0 && overdue > 0 ? " and " : "")}{(overdue > 0 ? $"{overdue} overdue" : "")}.";
        _tray.ShowBalloonTip(15000, "Time to focus",
            "Plan your day and start your first session." + taskNote + " Click here to open Focus.",
            System.Windows.Forms.ToolTipIcon.None);
    }

    // ---------- Friction before ending a focus session ----------

    /// <summary>Asks twice before ending a focus session early. Breaks don't need it.</summary>
    private bool ConfirmEndFocus(string action)
    {
        var cur = _runner.Current;
        if (cur == null || !cur.IsFocus) return true;

        string left = Format.Clock(_runner.Remaining);
        if (!Ask($"You still have {left} left in \"{cur.Name}\".\n\nDo you really want to {action}?",
                "Stay focused?", defaultNo: true))
            return false;

        return Ask($"Are you sure? Sticking with it for {left} more is the whole point.\n\n" +
                   "Choose Yes only if you really need to stop now.",
                   "Last check", defaultNo: true);
    }

    // ---------- Navigation ----------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (SettingsPanel == null) return; // still loading
        PlanPanel.Visibility = NavPlan.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ListsPanel.Visibility = NavLists.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TrainingPanel.Visibility = NavTraining.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavTraining.IsChecked == true) RefreshTraining();
        CalendarPanel.Visibility = NavCalendar.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DashboardPanel.Visibility = NavDashboard.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HomePanel.Visibility = NavHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavHome.IsChecked == true) RefreshHome(scroll: true);
        if (NavPlan.IsChecked != true && BlockPopup.IsOpen) CloseBlockEditor();
        StatsPanel.Visibility = NavStats.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavStats.IsChecked == true) RefreshStats();
        if (_runner.IsRunning) RunPanel.Visibility = NavDashboard.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        // Tasks and Personal each have their own button; both open the Tasks page, on their own set.
        TasksPanel.Visibility = NavTasks.IsChecked == true || NavPersonal.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavTasks.IsChecked == true || NavPersonal.IsChecked == true) ShowTasksPageForSet();
        else TaskStores.Show(TaskStores.Main);
        UpdatePomodoroLayout(); // before the dashboard refresh, so the task list and countdown are in the right place
        if (NavDashboard.IsChecked == true) RefreshDashboard(full: true);
        if (NavCalendar.IsChecked == true)
        {
            _calDate = _planDate; // open the calendar on the day you were planning
            RefreshCalendarTab();
        }
        PomodoroPanel.Visibility = NavPomodoro.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FocusPanel.Visibility = NavFocus.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavSettings.IsChecked != true) FlushSettings(); // leaving Settings: save anything still being edited
        SettingsPanel.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsSavedText.Visibility = Visibility.Collapsed;
        if (NavSettings.IsChecked == true) FillSettingFields();
    }

    // ---------- Day plan ----------


    private TimeSpan ParseDayStart()
    {
        return TryParseTime(DayStartBox.Text, out var t) ? t : TimeSpan.FromHours(8);
    }

    private void RecalcTimes()
    {
        if (PlanSummary == null) return;
        TimeSpan start = ParseDayStart();
        TimeSpan t = start;
        _knownMinutes.Clear();
        foreach (var b in _plan)
        {
            _knownMinutes[b] = b.Minutes;
            b.StartLabel = DateTime.Today.Add(t).ToString("t");
            t += TimeSpan.FromMinutes(b.Minutes);
        }

        EmptyPlanHint.Visibility = _plan.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Timeline.Selected = _selected;
        Timeline.Show(_plan.ToList(), DateTime.Today + start);
        if (CalendarPanel != null && CalendarPanel.Visibility == Visibility.Visible && CalDayView.IsChecked == true &&
            _calDate.Date == _planDate)
        {
            CalDay.Selected = _selected;
            CalDay.Show(_plan.ToList(), _calDate + start);
        }
        if (_plan.Count == 0)
        {
            PlanSummary.Text = "";
            return;
        }
        int focus = _plan.Count(b => b.IsFocus);
        string plural = focus == 1 ? "session" : "sessions";
        PlanSummary.Text = $"{focus} focus {plural}, {Format.Duration(t - start)} total, ends {DateTime.Today.Add(t):t}";
    }

    private void DayStart_LostFocus(object sender, RoutedEventArgs e) => ApplyDayStart();

    private void DayStart_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyDayStart();
    }

    private void ApplyDayStart()
    {
        string text = DayStartBox.Text.Trim();
        if (text.Length == 0 || text.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            UseCurrentTime();
            return;
        }
        if (_startIsAuto && text == _autoStartText)
        {
            RecalcTimes(); // left untouched, so keep following the clock
            return;
        }
        _startIsAuto = false;
        DayStartBox.Text = DateTime.Today.Add(ParseDayStart()).ToString("t");
        UpdateDayStartMode();
        RecalcTimes();
        MarkPlanEdited();
        ReflowPlan();
    }

    private static string NowText() => DateTime.Now.ToString("t");

    private void UseCurrentTime()
    {
        _startIsAuto = true;
        _autoStartText = NowText();
        DayStartBox.Text = _autoStartText;
        UpdateDayStartMode();
        RecalcTimes();
    }

    private void FollowClock()
    {
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        if (!_startIsAuto || DayStartBox.IsKeyboardFocusWithin) return;
        // While a day plan runs, keep the times anchored to when it actually started.
        if (_mode == RunMode.DayPlan && _runner.IsRunning) return;
        string now = NowText();
        if (now == DayStartBox.Text) return;
        _autoStartText = now;
        DayStartBox.Text = now;
        RecalcTimes();
    }

    private void UpdateDayStartMode()
    {
        DayStartAutoText.Visibility = _startIsAuto ? Visibility.Visible : Visibility.Collapsed;
        DayStartNowBtn.Visibility = _startIsAuto ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DayStartNow_Click(object sender, RoutedEventArgs e)
    {
        UseCurrentTime();
        MarkPlanEdited();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditStructure()) return;
        if (_plan.Count == 0) return;
        if (Ask("Remove every session and break from today's plan? Saved layouts aren't affected.", "Clear plan"))
        {
            PushUndo(includePlan: true);
            ClearPlan();
        }
    }

    private void ClearPlan()
    {
        while (_plan.Count > 0) _plan.RemoveAt(_plan.Count - 1);
    }

    private void StartDay_Click(object sender, RoutedEventArgs e) => StartTodayPlan(automatic: false);

    /// <summary>
    /// Starts today's plan on the clock. When started automatically, nothing is asked:
    /// it only runs within the plan's own hours.
    /// </summary>
    private void StartTodayPlan(bool automatic)
    {
        if (_planDate != DateTime.Today)
        {
            if (!automatic)
                Info("Only today's plan can be started. To have this day start by itself, check \"Start this day automatically\".",
                    "Start my day");
            return;
        }
        if (_plan.Count == 0)
        {
            if (!automatic) Info("Add at least one session to your plan first.", "Start my day");
            return;
        }
        DateTime now = DateTime.Now;
        DateTime dayStart = _startIsAuto ? now : DateTime.Today + ParseDayStart();
        DateTime dayEnd = dayStart.AddMinutes(_plan.Sum(b => b.Minutes));
        if (automatic && (now < dayStart || now >= dayEnd)) return;

        if (now < dayStart)
        {
            if (!Ask($"This plan is set to start at {dayStart:t}.\n\nStart the first session now and move the whole day earlier?",
                    "Start my day"))
                return;
            dayStart = now;
        }
        else if (now >= dayEnd)
        {
            Info($"This plan finished at {dayEnd:t}. To run it from now, click \"Use current time\" next to the start time, or type a new start time.",
                "Start my day");
            return;
        }

        if (!automatic && !ConfirmReplaceRunning()) return;
        if (dayStart == now) UseCurrentTime(); // starting fresh: the times on screen begin now
        _settings.LastAutoStartDate = DateTime.Today.ToString("yyyy-MM-dd"); // don't auto-start again today
        SavePlanNow();

        // Runs on the clock: starting late joins whichever session or break is happening right now.
        _mode = RunMode.DayPlan;
        _lastUserActionUtc = DateTime.UtcNow;
        if (!_runner.StartOnSchedule(_plan, dayStart))
        {
            _mode = RunMode.None;
            return;
        }
        BeginLock();
        NavDashboard.IsChecked = true; // starting the day opens the dashboard
    }

    // ---------- Default day ----------

    private PlanBlock NewFocus(int minutes)
    {
        var b = new PlanBlock
        {
            Kind = BlockKind.Focus,
            Name = $"Focus session {_plan.Count(b => b.IsFocus) + 1}",
            Minutes = minutes
        };
        ApplyDefaultBlocking(b); // your default block lists (Settings, Planning)
        return b;
    }

    private static PlanBlock NewBreak(string name, int minutes) => new()
    {
        Kind = BlockKind.Break,
        Name = name,
        Minutes = minutes
    };

    // ---------- Saved layouts ----------

    private DayLayout SnapshotPlan(string name) => new()
    {
        Name = name,
        DayStart = ParseDayStart().ToString(@"hh\:mm", CultureInfo.InvariantCulture),
        StartsNow = _startIsAuto,
        Blocks = _plan.Select(b => b.Clone()).ToList()
    };

    private void ApplyLayout(DayLayout layout)
    {
        ClearPlan();
        foreach (var b in layout.Blocks) _plan.Add(b.Clone());

        // A saved layout keeps its own start time, so the day runs on schedule.
        if (layout.StartsNow || !TryParseTime(layout.DayStart, out var start)) UseCurrentTime();
        else SetPinnedStart(start);
    }

    private static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out time)) return true;
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var dt) ||
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out dt))
        {
            time = dt.TimeOfDay;
            return true;
        }
        return false;
    }

    private void SetPinnedStart(TimeSpan start)
    {
        _startIsAuto = false;
        DayStartBox.Text = DateTime.Today.Add(start).ToString("t");
        UpdateDayStartMode();
        RecalcTimes();
    }

    private void LayoutName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveLayout();
    }

    private void SaveLayout_Click(object sender, RoutedEventArgs e) => SaveLayout();

    private void SaveLayout()
    {
        string name = LayoutNameBox.Text.Trim();
        if (name.Length == 0)
        {
            Info("Give the layout a name first, like \"Tuesday\".", "Save layout");
            LayoutNameBox.Focus();
            return;
        }
        if (_plan.Count == 0)
        {
            Info("Your plan is empty, so there's nothing to save yet.", "Save layout");
            return;
        }

        var existing = _layouts.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null && !Ask($"Replace the saved layout \"{existing.Name}\" with this plan?", "Save layout"))
            return;

        var layout = SnapshotPlan(name);
        if (existing != null) _layouts[_layouts.IndexOf(existing)] = layout;
        else _layouts.Add(layout);
        PersistLayouts();
    }

    private void LoadLayout_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DayLayout layout) return;
        if (!CanEditStructure()) return;
        PushUndo(includePlan: true);
        LayoutsPopup.IsOpen = false;
        ApplyLayout(layout);
        LayoutNameBox.Text = layout.Name;
    }

    private void DeleteLayout_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DayLayout layout) return;
        if (!Ask($"Delete the saved layout \"{layout.Name}\"?", "Delete layout")) return;
        _layouts.Remove(layout);
        PersistLayouts();
    }

    private void PersistLayouts()
    {
        try { Storage.Save("layouts.json", _layouts.ToList()); }
        catch (Exception ex) { Info("Couldn't save your layouts: " + ex.Message, "Save layout"); }
    }

    private void UpdateLayoutHint() =>
        NoLayoutsHint.Visibility = _layouts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------- Pomodoro and quick focus ----------

    // ---------- Locked mode ----------

    private bool LockedNow => _lockedRun && _runner.Current?.IsFocus == true;

    private bool ExtremeNow => _extremeRun && _runner.Current?.IsFocus == true;

    private void BeginLock()
    {
        _lockedRun = _settings.LockedMode || _settings.ExtremeLock;
        _extremeRun = _settings.ExtremeLock;
        _settings.LastSessionDate = DateTime.Today.ToString("yyyy-MM-dd");
        SaveSettingsQuietly();
        if (_mode == RunMode.DayPlan) ShareRunningPlan();
        SaveRunState();
        UpdateRunPanel();
        StartWebsiteBlocking();
    }

    private void FillLockedChecks()
    {
        _syncingLockChecks = true;
        bool locked = _settings.LockedMode || _settings.ExtremeLock;
        foreach (var box in new[] { LockedCheckPlan, LockedCheckPomo, LockedCheckQuick }) box.IsChecked = locked;
        foreach (var box in new[] { ExtremeCheckPlan, ExtremeCheckPomo, ExtremeCheckQuick }) box.IsChecked = _settings.ExtremeLock;
        _syncingLockChecks = false;
    }

    private void LockedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingLockChecks || sender is not CheckBox box) return;
        _settings.LockedMode = box.IsChecked == true;
        if (!_settings.LockedMode) _settings.ExtremeLock = false; // extreme is a stronger kind of locked
        FillLockedChecks();
        SaveSettingsQuietly();
    }

    private void ExtremeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingLockChecks || sender is not CheckBox box) return;
        if (box.IsChecked == true)
        {
            bool sure = Ask(
                "Turn on extreme lock?\n\n" +
                "During focus sessions you won't be able to stop, skip, or quit Focus. If Focus is force-closed, " +
                "it reopens itself and carries on, and it picks the session back up after a restart.\n\n" +
                "If you really need your apps, you can take a 3-minute or 8-minute break, after two confirmations.",
                "Extreme lock", defaultNo: true);
            _settings.ExtremeLock = sure;
            if (sure) _settings.LockedMode = true;
        }
        else
        {
            _settings.ExtremeLock = false;
        }
        FillLockedChecks();
        SaveSettingsQuietly();
    }

    private void ShowLockedMessage() =>
        Info("Locked mode is on, so this focus session can't be stopped or skipped.\n\n" +
             "To end it early, right-click the Focus icon by the clock and choose Quit Focus.", "Locked");

    // ---------- Running session ----------

    private void Runner_BlockStarted(PlanBlock block)
    {
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        if (!_resuming) MoveUnfinishedToBin(_previousRunBlock);
        _previousRunBlock = block;
        _passEndsUtc = null;
        bool startedByYou = DateTime.UtcNow - _lastUserActionUtc < TimeSpan.FromSeconds(2);
        if (_settings.FlashOnChange && !startedByYou && !_resuming)
        {
            var accent = (Brush)FindResource(AccentKey(block));
            var tint = TryFindResource(block.Kind switch { BlockKind.Focus => "FocusTint", BlockKind.Meeting => "MeetingTint", _ => "BreakTint" }) as Brush;
            var now = DateTime.Now;
            string time = $"{now:t} to {(now + _runner.Remaining):t}  ({Math.Max(1, (int)Math.Round(_runner.Remaining.TotalMinutes))} min)";
            FlashWindow.ShowOnAllScreens(
                block.Kind switch { BlockKind.Focus => "Time to focus", BlockKind.Meeting => "Meeting time", _ => "Time for a break" },
                block.Kind switch { BlockKind.Break => $"{block.Name}. Step away from the screen.", _ => block.Name },
                accent, tint,
                block.Kind switch { BlockKind.Focus => "\uE916", BlockKind.Meeting => "\uE716", _ => "\uE708" },
                time,
                block.IsFocus ? block.Tasks.Where(t => !t.Done && t.Text.Trim().Length > 0).Select(t => t.Text).ToList() : new List<string>());
        }

        if (_settings.SoundsOn && !_resuming)
        {
            if (block.IsFocus) Sounds.PlayStart(_settings.Volume);
            else Sounds.PlayEnd(_settings.Volume);
        }

        CloseWarning();
        ApplyBlockingForCurrent(force: true, current: block);

        if (!_resuming) SaveRunState();
        HighlightRunningBlock();
    }

    private static string AccentKey(PlanBlock block) =>
        block.Kind switch { BlockKind.Focus => "FocusBrush", BlockKind.Meeting => "MeetingBrush", _ => "BreakBrush" };

    private void Blocker_WarningStarted(List<BlockedApp> openApps, int seconds)
    {
        CloseWarning();
        _warning = new CloseWarningWindow(openApps, seconds);
        _warning.CloseNowRequested += () => _blocker.CloseOpenAppsNow();
        _warning.Closed += (_, _) => _warning = null;
        _warning.Show();
    }

    private void CloseWarning()
    {
        var w = _warning;
        _warning = null;
        w?.Close();
    }

    private void Blocker_AppBlocked(BlockedApp app)
    {
        var cur = _runner.Current;
        string until = (DateTime.Now + _runner.Remaining).ToString("t");
        string detail = cur == null ? "" : $"Blocked until {until}. Back to {cur.Name}.";
        _toast.ShowMessage($"{app.Name} is blocked during this session", detail);
    }

    private void UpdateRunPanel()
    {
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        UpdatePomodoroLayout(); // switches the Pomodoro tab between setup and countdown as a Pomodoro starts or stops
        var cur = _runner.Current;
        if (cur == null)
        {
            MoveUnfinishedToBin(_previousRunBlock);
            _previousRunBlock = null;
            if (!_quitting) StopWebsiteBlocking();
            _blocker.End();
            _activeBlockKeys.Clear();
            CloseWarning();
            _ui.Running = false;
            _ui.PlanRunning = false;
            ApplyRunningEditRules();
            RefreshDashboard(full: true);
            _lockedRun = false;
            _extremeRun = false;
            _passEndsUtc = null;
            SaveStats();
            Storage.Delete("run-state.json");
            RunPanel.Visibility = Visibility.Collapsed;
            Title = "Focus";
            _tray.Text = "Focus";
            ShowOverlayIdle();
            UpdateOverlayVisibility();
            return;
        }

        if (_passEndsUtc is DateTime passEnds && DateTime.UtcNow >= passEnds) EndPass();
        MaintainWatchdog();
        bool planRunning = _mode == RunMode.DayPlan;
        if (!_ui.Running || _ui.PlanRunning != planRunning)
        {
            _ui.Running = true;
            _ui.PlanRunning = planRunning;
            ApplyRunningEditRules();
        }

        // The dashboard shows all of this itself, so the strip is hidden there.
        RunPanel.Visibility = NavDashboard.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        var accent = (Brush)FindResource(AccentKey(cur));
        RunStripe.Background = accent;
        RunKind.Foreground = accent;
        RunProgress.Foreground = accent;

        RunKind.Text = _mode switch
        {
            RunMode.Pomodoro => $"{cur.KindLabel}, round {_runner.Round}",
            RunMode.DayPlan => $"{cur.KindLabel}, {_runner.Index + 1} of {_runner.Count}",
            _ => cur.KindLabel
        };
        RunName.Text = cur.Name;

        TimeSpan remaining = _runner.Remaining;
        string minutesLeft = Format.MinutesLeft(remaining);
        RunTime.Text = minutesLeft;
        double total = TimeSpan.FromMinutes(cur.Minutes).TotalSeconds;
        double fractionLeft = total > 0 ? Math.Clamp(remaining.TotalSeconds / total, 0, 1) : 0;
        RunProgress.Value = 1 - fractionLeft;

        var next = _runner.Next;
        string endsAt = (DateTime.Now + remaining).ToString("t");
        RunNext.Text = _runner.IsPaused
            ? "Paused"
            : next == null
                ? $"Ends at {endsAt}, last one"
                : $"Ends at {endsAt}. Next up: {next.Name}, {next.Minutes} min";

        bool blocking = cur.IsFocus && cur.HasBlockedApps;
        RunBlocking.Visibility = blocking ? Visibility.Visible : Visibility.Collapsed;
        RunBlocking.Text = blocking ? cur.BlockedSummary : "";

        bool locked = LockedNow;
        PauseBtn.Content = _runner.IsPaused ? "Resume" : "Pause";
        // A day plan runs on the clock, so it can't be paused. Locked focus sessions can't be stopped.
        PauseBtn.Visibility = _runner.IsOnSchedule || locked ? Visibility.Collapsed : Visibility.Visible;
        SkipBtn.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        StopBtn.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        bool extreme = ExtremeNow;
        bool onPass = _passEndsUtc != null;
        LockedNote.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        LockedNote.Text = extreme
            ? "Extreme lock. Focus can't be stopped or closed until this session ends."
            : "Locked. To end this session early, quit Focus from its tray icon.";
        LockedNote.ToolTip = LockedNote.Text;
        // Short breaks from blocking are offered in every mode, during focus sessions that block something.
        PassButtons.Visibility = blocking && !onPass ? Visibility.Visible : Visibility.Collapsed;
        PassActivePanel.Visibility = blocking && onPass ? Visibility.Visible : Visibility.Collapsed;
        if (onPass && _passEndsUtc is DateTime pe)
            PassText.Text = $"Apps unblocked for {Format.Clock(pe - DateTime.UtcNow)}";
        Title = $"{minutesLeft} left  {cur.Name}";
        string trayText = $"Focus: {minutesLeft} left, {cur.Name}";
        _tray.Text = trayText.Length > 63 ? trayText[..63] : trayText;

        // The floating timer counts down in steps of ten seconds.
        string tens = _settings.OverlaySeconds ? Format.ClockTens(remaining) : Format.MinutesLeft(remaining);
        _overlay.Update(_runner.IsPaused ? $"{tens} paused" : tens, cur.Name, accent, fractionLeft, cur.IsFocus ? _runner.FocusNumber : 0);
        TrackFocusTime(cur);
        RefreshDashboard();
        UpdateOverlayVisibility();
    }

    private bool _overlayWasRunning;

    /// <summary>Your choice for now: hidden with its ✕ (false), brought back with the dashboard's 🌡 (true), or as Settings say (null).</summary>
    private bool? _overlayChoice;

    private void DashOverlay_Click(object sender, RoutedEventArgs e)
    {
        bool showing = _overlay.IsVisible;
        _overlayChoice = !showing;
        _settings.OverlayHidden = showing; // remembered, even after a restart
        SaveSettingsQuietly();
        if (!_runner.IsRunning) ShowOverlayIdle();
        UpdateOverlayTasks();
        UpdateOverlayVisibility();
    }

    /// <summary>The floating timer when nothing is running: a calm "Ready" (double-click opens Focus).</summary>
    private void ShowOverlayIdle()
    {
        try { _overlay.Update("Ready", "Nothing running", (Brush)FindResource("Muted"), 0); }
        catch (Exception ex) { Log.Error("ShowOverlayIdle", ex); }
    }

    private void UpdateOverlayVisibility()
    {
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        // Ending the day (or the session) unpins the floating timer's tasks.
        if (_overlayWasRunning && !_runner.IsRunning && _settings.OverlayPinned)
        {
            _settings.OverlayPinned = false;
            SaveSettingsQuietly();
            _overlay.SetPinned(false);
        }
        _overlayWasRunning = _runner.IsRunning;
        bool away = WindowState == WindowState.Minimized || !IsVisible;
        // On screen with the app (unless switched off in Settings); otherwise only while a session runs and Focus is out of the way.
        bool show = _overlayChoice ?? (_settings.ShowFloatingTimer && (_settings.OverlayAlwaysOn || (_runner.IsRunning && away)));
        if (show && !_overlay.IsVisible) ShowOverlay();
        else if (!show && _overlay.IsVisible) _overlay.Hide();
        if (_overlay.IsVisible) UpdateOverlayTasks(); // cheap: the list only rebuilds when tasks change
    }

    private void ShowOverlay()
    {
        _overlay.Show();
        _overlay.UpdateLayout();

        double left = _settings.OverlayLeft, top = _settings.OverlayTop;
        bool onScreen = !double.IsNaN(left) && !double.IsNaN(top)
            && left >= SystemParameters.VirtualScreenLeft
            && top >= SystemParameters.VirtualScreenTop
            && left + 40 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && top + 20 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        if (onScreen)
        {
            _overlay.Left = left;
            _overlay.Top = top;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            _overlay.Left = area.Right - _overlay.ActualWidth - 24;
            _overlay.Top = area.Top + 24;
        }
    }

    private void RestoreFromOverlay() => ShowFromTray();

    private void Runner_Finished()
    {
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        var mode = _mode;
        _mode = RunMode.None;
        if (_settings.SoundsOn) Sounds.PlayEnd(_settings.Volume);
        UpdateRunPanel();
        ShowFromTray();
        Info(mode == RunMode.DayPlan ? "That's your whole day done. Nice work." : "Focus session complete.", "Focus");
        if (mode == RunMode.DayPlan && _settings.On("review")) OpenReview();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (LockedNow) { ShowLockedMessage(); return; }
        if (_runner.IsPaused) _runner.Resume();
        else _runner.Pause();
        SaveRunState();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (LockedNow) { ShowLockedMessage(); return; }
        if (!ConfirmEndFocus("skip the rest of this focus session")) return;
        _lastUserActionUtc = DateTime.UtcNow;
        _runner.Skip();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (LockedNow) { ShowLockedMessage(); return; }
        if (!ConfirmEndFocus("stop this focus session")) return;
        _mode = RunMode.None;
        _runner.Stop();
    }

    private bool ConfirmReplaceRunning()
    {
        if (!_runner.IsRunning) return true;
        if (LockedNow)
        {
            ShowLockedMessage();
            return false;
        }
        bool ok = _runner.Current?.IsFocus == true
            ? ConfirmEndFocus("end this focus session and start something else")
            : Ask("A break is running. Stop it and start this instead?", "Focus");
        if (!ok) return false;
        _runner.Stop();
        return true;
    }

    // ---------- Settings ----------

    private void SaveSettingsQuietly()
    {
        try { Storage.Save("settings.json", _settings); } catch { }
    }

    private void TestStartSound_Click(object sender, RoutedEventArgs e) =>
        Sounds.PlayStart((int)Math.Round(VolumeSlider.Value));

    private void TestEndSound_Click(object sender, RoutedEventArgs e) =>
        Sounds.PlayEnd((int)Math.Round(VolumeSlider.Value));

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Storage.Folder);
            Process.Start(new ProcessStartInfo { FileName = Storage.Folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Info("Couldn't open the folder: " + ex.Message, "Settings");
        }
    }

    // ---------- Helpers ----------

    private bool Ask(string message, string title, bool defaultNo = false)
    {
        var fallback = defaultNo ? MessageBoxResult.No : MessageBoxResult.Yes;
        var result = IsVisible
            ? MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, fallback)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, fallback);
        return result == MessageBoxResult.Yes;
    }

    private void Info(string message, string title)
    {
        // While settings save themselves, a problem shows quietly under the categories instead of interrupting.
        if (_autoSaving && title == "Settings") { ShowSettingsStatus(message); return; }
        if (IsVisible) MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
