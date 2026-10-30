using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace FocusApp;

/// <summary>Pomodoro and quick focus, blocking, short breaks, settings, nudges, and focus-time tracking.</summary>
public partial class MainWindow
{
    private readonly HashSet<string> _activeBlockKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingTheme;
    private FocusStats? _stats;
    private DateTime _lastTrackUtc = DateTime.MinValue;
    private DateTime _lastStatsSaveUtc = DateTime.MinValue;

    // ---------- Blocking ----------

    /// <summary>
    /// Makes the blocker match the current block: its own apps plus its block lists' apps,
    /// unless a short break is on. Only restarts the blocker when the set of apps changes.
    /// </summary>
    private void ApplyBlockingForCurrent(bool force = false, PlanBlock? current = null)
    {
        var cur = current ?? _runner.Current;
        var apps = cur != null && cur.IsFocus && _passEndsUtc == null ? cur.EffectiveApps() : new List<BlockedApp>();
        var keys = new HashSet<string>(apps.Select(a => a.Key), StringComparer.OrdinalIgnoreCase);
        if (!force && keys.SetEquals(_activeBlockKeys)) return;

        _activeBlockKeys.Clear();
        _activeBlockKeys.UnionWith(keys);
        if (apps.Count > 0) _blocker.Begin(apps, WarningSeconds);
        else _blocker.End();
    }

    // ---------- Pomodoro and quick focus ----------

    private void UpdateQuickBlockButtons()
    {
        PomoBlockBtn.Content = AppsLabel(_settings.PomodoroBlocked);
        PomoListBtn.Content = ListsLabel(_settings.PomodoroLists);
        QuickBlockBtn.Content = AppsLabel(_settings.QuickFocusBlocked);
        QuickListBtn.Content = ListsLabel(_settings.QuickFocusLists);
    }

    private static string AppsLabel(List<BlockedApp> apps) =>
        apps.Count == 0 ? "+ Block apps" : BlockedApp.Summary(apps);

    private static string ListsLabel(List<string> ids)
    {
        var names = ids.Select(BlockLists.Find).Where(l => l != null).Select(l => l!.Name).ToList();
        if (names.Count == 0) return "+ Block list";
        string shown = string.Join(", ", names.Take(2));
        return names.Count > 2 ? $"Lists: {shown} and {names.Count - 2} more" : $"Lists: {shown}";
    }

    private void PomoBlock_Click(object sender, RoutedEventArgs e) =>
        OpenPicker((UIElement)sender, _settings.PomodoroBlocked, chosen =>
        {
            _settings.PomodoroBlocked = chosen;
            UpdateQuickBlockButtons();
            SaveSettingsQuietly();
        });

    private void PomoList_Click(object sender, RoutedEventArgs e) =>
        OpenListPicker((UIElement)sender, _settings.PomodoroLists, ids =>
        {
            _settings.PomodoroLists = ids;
            UpdateQuickBlockButtons();
            SaveSettingsQuietly();
        });

    private void QuickBlock_Click(object sender, RoutedEventArgs e) =>
        OpenPicker((UIElement)sender, _settings.QuickFocusBlocked, chosen =>
        {
            _settings.QuickFocusBlocked = chosen;
            UpdateQuickBlockButtons();
            SaveSettingsQuietly();
        });

    private void QuickList_Click(object sender, RoutedEventArgs e) =>
        OpenListPicker((UIElement)sender, _settings.QuickFocusLists, ids =>
        {
            _settings.QuickFocusLists = ids;
            UpdateQuickBlockButtons();
            SaveSettingsQuietly();
        });

    private void StartPomodoro_Click(object sender, RoutedEventArgs e)
    {
        PomoFocusBox.Commit();
        PomoBreakBox.Commit();
        if (!ConfirmReplaceRunning()) return;
        _mode = RunMode.Pomodoro;
        _lastUserActionUtc = DateTime.UtcNow;
        _runner.Start(new[]
        {
            new PlanBlock
            {
                Kind = BlockKind.Focus, Name = "Pomodoro", Minutes = PomoFocusBox.Value,
                BlockedApps = _settings.PomodoroBlocked.Select(a => a.Copy()).ToList(),
                BlockListIds = new List<string>(_settings.PomodoroLists)
            },
            new PlanBlock { Kind = BlockKind.Break, Name = "Break", Minutes = PomoBreakBox.Value }
        }, loop: true);
        BeginLock();
    }

    private void StartQuickFocus_Click(object sender, RoutedEventArgs e)
    {
        QuickMinutesBox.Commit();
        if (!ConfirmReplaceRunning()) return;
        string name = QuickNameBox.Text.Trim();
        _mode = RunMode.QuickFocus;
        _lastUserActionUtc = DateTime.UtcNow;
        _runner.Start(new[]
        {
            new PlanBlock
            {
                Kind = BlockKind.Focus, Name = name.Length > 0 ? name : "Focus", Minutes = QuickMinutesBox.Value,
                BlockedApps = _settings.QuickFocusBlocked.Select(a => a.Copy()).ToList(),
                BlockListIds = new List<string>(_settings.QuickFocusLists)
            }
        }, loop: false);
        BeginLock();
    }

    // ---------- Short breaks from blocking (3 and 8 minutes) ----------

    private void Pass3_Click(object sender, RoutedEventArgs e) => TakePass(Defaults.ShortBreakA);
    private void Pass8_Click(object sender, RoutedEventArgs e) => TakePass(Defaults.ShortBreakB);

    /// <summary>
    /// Unblocks apps for a few minutes. In regular mode it starts right away. In locked and
    /// extreme mode it takes two confirmations and typing the phrase.
    /// </summary>
    private void TakePass(int minutes)
    {
        var cur = _runner.Current;
        if (cur == null || !cur.IsFocus || !cur.HasBlockedApps || _passEndsUtc != null) return;

        if (_lockedRun)
        {
            if (!Ask($"Take a {minutes}-minute break from blocking?\n\n" +
                     $"Your blocked apps will open up for {minutes} minutes, then close again. \"{cur.Name}\" keeps running.",
                    "Short break", defaultNo: true))
                return;
            if (!Ask("Second check: are you sure you need your apps right now?",
                    "Confirm short break", defaultNo: true))
                return;
            var dialog = new PhraseDialog();
            if (IsVisible) dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
        }

        // The session may have changed while you were deciding.
        if (!ReferenceEquals(_runner.Current, cur) || _passEndsUtc != null) return;

        _passEndsUtc = DateTime.UtcNow.AddMinutes(minutes);
        CloseWarning();
        ApplyBlockingForCurrent(force: true);
        UpdateRunPanel();
    }

    private void EndPass_Click(object sender, RoutedEventArgs e) => EndPass();

    /// <summary>Ends a short break and turns blocking back on (with the usual 10-second warning).</summary>
    private void EndPass()
    {
        if (_passEndsUtc == null) return;
        _passEndsUtc = null;
        var cur = _runner.Current;
        if (cur != null && cur.IsFocus && cur.HasBlockedApps)
            _toast.ShowMessage("Short break is over", $"Back to {cur.Name}. Blocked apps are closing.");
        ApplyBlockingForCurrent(force: true);
        UpdateRunPanel();
    }

    // ---------- Settings ----------

    private void FillSettingFields()
    {
        BeginFillingSettings();
        SetFocusBox.Value = _settings.FocusMinutes;
        SetBreakBox.Value = _settings.BreakMinutes;
        SoundsCheck.IsChecked = _settings.SoundsOn;
        VolumeSlider.Value = _settings.Volume;
        OverlayCheck.IsChecked = _settings.ShowFloatingTimer;
        OverlayAlwaysCheck.IsChecked = _settings.OverlayAlwaysOn;
        OverlayRowsBox.Value = Math.Clamp(_settings.OverlayRows, 2, 30);
        FlashCheck.IsChecked = _settings.FlashOnChange;
        ReminderCheck.IsChecked = _settings.MorningReminderOn;
        ReminderTimeBox.Text = DateTime.Today.Add(_settings.ReminderTimeOfDay).ToString("t");
        NudgeCheck.IsChecked = _settings.NudgesOn;

        FillInstallInfo();
        BuildFeatureChecks();
        FillNewerSettings();
        GoalTasksBox.Value = _settings.GoalTasks;
        GoalHoursBox.Value = (int)Math.Round(_settings.GoalFocusHours);
        FillSiteSettings();
        FillVocabSettings();
        WaitingDaysBox.Value = _settings.WaitingReminderDays;
        UpdateKeysInfo();
        Scale90.IsChecked = _settings.DisplayScale < 0.95;
        Scale100.IsChecked = _settings.DisplayScale >= 0.95 && _settings.DisplayScale < 1.07;
        Scale115.IsChecked = _settings.DisplayScale >= 1.07 && _settings.DisplayScale < 1.22;
        Scale130.IsChecked = _settings.DisplayScale >= 1.22;
        TaskFont14.IsChecked = _settings.TaskFontSize < 16;
        TaskFont17.IsChecked = _settings.TaskFontSize >= 16 && _settings.TaskFontSize < 19;
        TaskFont20.IsChecked = _settings.TaskFontSize >= 19;
        Spacing80.IsChecked = _settings.CalendarSpacing < 0.9;
        Spacing100.IsChecked = _settings.CalendarSpacing >= 0.9 && _settings.CalendarSpacing < 1.15;
        Spacing130.IsChecked = _settings.CalendarSpacing >= 1.15;
        WorkStartBox.Text = DateTime.Today.Add(_settings.WorkdayStartTime).ToString("t");
        WorkEndBox.Text = DateTime.Today.Add(_settings.WorkdayEndTime).ToString("t");
        OverlaySecondsCheck.IsChecked = _settings.OverlaySeconds;
        AlertSecondsBox.Value = _settings.AlertSeconds;
        WarningSecondsBox.Value = _settings.WarningSeconds;

        _syncingTheme = true;
        ThemeLight.IsChecked = _settings.Theme == "Light";
        ThemeDark.IsChecked = _settings.Theme == "Dark";
        ThemeSystem.IsChecked = _settings.Theme != "Light" && _settings.Theme != "Dark";
        _syncingTheme = false;
    }

    private void FillPomodoroFields()
    {
        PomoFocusBox.Value = _settings.FocusMinutes;
        PomoBreakBox.Value = _settings.BreakMinutes;
        QuickMinutesBox.Value = _settings.FocusMinutes;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SetFocusBox.Commit();
        SetBreakBox.Commit();
        AlertSecondsBox.Commit();
        WarningSecondsBox.Commit();
        if (SaveVocabSettings() is string vocabProblem) { Info(vocabProblem, "Settings"); return; }
        GoalTasksBox.Commit();
        GoalHoursBox.Commit();
        WaitingDaysBox.Commit();
        _settings.GoalTasks = GoalTasksBox.Value;
        _settings.GoalFocusHours = GoalHoursBox.Value;
        _settings.WaitingReminderDays = WaitingDaysBox.Value;
        _settings.BlockedSites = _editBlockedSites.Select(SiteRules.Clean).Where(x => x.Contains('.')).Distinct().ToList();
        if (!TryParseTime(WorkStartBox.Text, out var workStart) || !TryParseTime(WorkEndBox.Text, out var workEnd) ||
            workEnd - workStart < TimeSpan.FromHours(1))
        {
            Info("Enter the working day like 6:45 AM to 4:30 PM (at least an hour long).", "Settings");
            WorkStartBox.Focus();
            return;
        }
        if (!DateTime.TryParse(ReminderTimeBox.Text.Trim(), CultureInfo.CurrentCulture,
                DateTimeStyles.NoCurrentDateDefault, out var reminderAt))
        {
            Info("Enter the reminder time like 7:00 AM.", "Settings");
            ReminderTimeBox.Focus();
            return;
        }

        _settings.FocusMinutes = SetFocusBox.Value;
        _settings.BreakMinutes = SetBreakBox.Value;
        _settings.SoundsOn = SoundsCheck.IsChecked == true;
        _settings.Volume = (int)Math.Round(VolumeSlider.Value);
        // Only a change to the floating timer's own settings overrides hiding it with its ✕
        // (settings save themselves all the time, so saving alone mustn't bring it back).
        bool timerSettingsChanged = _settings.ShowFloatingTimer != (OverlayCheck.IsChecked == true) ||
                                    _settings.OverlayAlwaysOn != (OverlayAlwaysCheck.IsChecked == true);
        _settings.ShowFloatingTimer = OverlayCheck.IsChecked == true;
        _settings.OverlayAlwaysOn = OverlayAlwaysCheck.IsChecked == true;
        OverlayRowsBox.Commit();
        if (_settings.OverlayRows != OverlayRowsBox.Value)
        {
            _settings.OverlayRows = OverlayRowsBox.Value;
            _overlay.SetRows(_settings.OverlayRows); // only when you change it (so a list you've dragged taller stays that way)
        }
        if (timerSettingsChanged) { _overlayChoice = null; _settings.OverlayHidden = false; }
        _settings.FlashOnChange = FlashCheck.IsChecked == true;
        _settings.MorningReminderOn = ReminderCheck.IsChecked == true;
        _settings.ReminderTime = reminderAt.TimeOfDay.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        _settings.NudgesOn = NudgeCheck.IsChecked == true;
        _settings.AutoStartWorkdays = AutoStartWorkdaysCheck.IsChecked == true;
        _settings.DisplayScale = Scale90.IsChecked == true ? 0.9 : Scale115.IsChecked == true ? 1.15 : Scale130.IsChecked == true ? 1.3 : 1.0;
        _settings.TaskFontSize = TaskFont17.IsChecked == true ? 17 : TaskFont20.IsChecked == true ? 20 : 15;
        _settings.CalendarSpacing = Spacing80.IsChecked == true ? 0.75 : Spacing130.IsChecked == true ? 1.35 : 1.0;
        _settings.WorkdayStart = workStart.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        _settings.WorkdayEnd = workEnd.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        _settings.OverlaySeconds = OverlaySecondsCheck.IsChecked == true;
        _settings.AlertSeconds = AlertSecondsBox.Value;
        _settings.WarningSeconds = WarningSecondsBox.Value;

        try
        {
            SaveFeatureChecks();
            SaveNewerSettings();
            Storage.Save("settings.json", _settings);
            ApplyCustomization();
            ApplyFeatures();
            ApplyStartupSetting();
            if (!_autoSaving) FillSettingFields(); // saving as you go: leave the fields as you're editing them
            FillPomodoroFields();
            UpdateOverlayVisibility();
            ShowSettingsStatus(null);
        }
        catch (Exception ex)
        {
            Info("Couldn't save settings: " + ex.Message, "Settings");
        }
    }

    private void FillInstallInfo()
    {
        if (IsTestBuild)
        {
            InstallInfoText.Text = "This is a test copy (run with dotnet run). Install and uninstall work in the final FocusApp.exe.";
            UninstallBtn.IsEnabled = false;
            return;
        }
        InstallInfoText.Text = Installer.IsRunningInstalledCopy
            ? $"Installed for you in {Installer.InstallFolder}. To install on another PC, copy FocusApp.exe there and open it."
            : "This copy isn't installed. Open FocusApp.exe again and choose Install, or keep using it from here.";
        UninstallBtn.IsEnabled = true;
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (IsTestBuild) return;
        if (_runner.IsRunning)
        {
            Info("A session is running. Uninstall Focus after it ends.", "Uninstall");
            return;
        }
        if (!Ask("Uninstall Focus from this PC? It will close and be removed from the Start menu and from starting with Windows.",
                "Uninstall Focus", defaultNo: true))
            return;
        bool deleteData = Ask("Also delete your plans, layouts, block lists, calendars and settings?\n\n" +
                              "Choose No to keep them, so they're there if you install Focus again.",
                              "Uninstall Focus", defaultNo: true);
        try
        {
            SavePlanNow();
            SaveSettingsQuietly();
            Installer.Uninstall(deleteData);
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall", ex);
            Info("Couldn't finish uninstalling: " + ex.Message, "Uninstall");
            return;
        }
        _quitting = true;
        _tray.Visible = false;
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>Applies display size, task text size, calendar spacing and alert length.</summary>
    private void ApplyCustomization()
    {
        double scale = _settings.DisplayScale;
        var transform = new ScaleTransform(scale, scale);
        RootGrid.LayoutTransform = transform;
        foreach (var popup in new[] { PickerPopup, ListPickerPopup, BinPickerPopup, AdjustDayPopup, TaskPopup })
            if (popup.Child is FrameworkElement child) child.LayoutTransform = transform;

        Application.Current.Resources["TaskFontSize"] = _settings.TaskFontSize;

        double spacing = _settings.CalendarSpacing;
        Timeline.PixelsPerMinute = 1.8 * spacing;
        CalDay.PixelsPerMinute = 1.8 * spacing;
        DashDay.PixelsPerMinute = 1.5 * spacing;
        if (HomeDay != null) { HomeDay.PixelsPerMinute = 1.5 * spacing; HomeDay.RequestRender(); }
        CalWeek.PixelsPerMinute = 1.3 * spacing;
        DashWeek.PixelsPerMinute = 1.3 * spacing;
        Timeline.RequestRender();
        CalDay.RequestRender();
        DashDay.RequestRender();
        CalWeek.RequestRender();
        DashWeek.RequestRender();

        FlashWindow.HoldSeconds = _settings.AlertSeconds;
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingTheme || ThemeLight == null) return;
        _settings.Theme = ThemeLight.IsChecked == true ? "Light" : ThemeDark.IsChecked == true ? "Dark" : "System";
        ThemeManager.Apply(_settings.Theme);
        SaveSettingsQuietly();
        Timeline.RequestRender();
    }

    // ---------- Nudges on unused days ----------

    /// <summary>
    /// On workdays, if no session has started yet, sends a gentle reminder at each nudge time
    /// (within an hour of it, so a PC that was off doesn't get a pile of them).
    /// </summary>
    private void CheckNudges()
    {
        if (!_settings.NudgesOn || _runner.IsRunning) return;
        var now = DateTime.Now;
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        string today = now.ToString("yyyy-MM-dd");
        if (_settings.LastSessionDate == today) return;

        for (int i = Defaults.NudgeTimes.Length - 1; i >= 0; i--)
        {
            var at = DateTime.Today + Defaults.NudgeTimes[i];
            if (now < at) continue;
            if (now > at.AddHours(1)) return;
            string key = $"{today}#{i}";
            if (_settings.LastNudgeKey == key) return;

            _settings.LastNudgeKey = key;
            SaveSettingsQuietly();
            _tray.ShowBalloonTip(12000, "Ready to focus?",
                Defaults.NudgeMessages[i % Defaults.NudgeMessages.Length] + " Click to open Focus.",
                System.Windows.Forms.ToolTipIcon.None);
            return;
        }
    }

    // ---------- Focus time tracking ----------

    /// <summary>
    /// Counts time spent in focus sessions that block at least one app (paused time and
    /// short breaks don't count). Used for the daily and weekly focus totals.
    /// </summary>
    private void TrackFocusTime(PlanBlock cur)
    {
        var now = DateTime.UtcNow;
        var last = _lastTrackUtc;
        _lastTrackUtc = now;
        if (last == DateTime.MinValue) return;
        var delta = now - last;
        if (delta <= TimeSpan.Zero || delta > TimeSpan.FromSeconds(5)) return; // ignore sleep and gaps
        if (!cur.IsFocus || _runner.IsPaused || _passEndsUtc != null || !cur.HasBlockedApps) return;

        _stats ??= FocusStats.Load();
        _stats.Add(DateTime.Today, delta);
        if (now - _lastStatsSaveUtc > TimeSpan.FromMinutes(1))
        {
            _stats.Save();
            _lastStatsSaveUtc = now;
        }
    }

    private void SaveStats()
    {
        _stats?.Save();
        _lastTrackUtc = DateTime.MinValue;
    }
}
