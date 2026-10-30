using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace FocusApp;

/// <summary>The dashboard: today's calendar with a moving "now" line, the ring timer, and focus totals.</summary>
public partial class MainWindow
{
    private DateTime _lastDashRenderUtc = DateTime.MinValue;
    private int _lastDashBlockIndex = -2;

    private void SetUpDashboard()
    {
        DashDay.RangeStart = Defaults.CalendarStart;
        DashDay.RangeEnd = Defaults.CalendarEnd;
        DashDay.ShowNowLine = true;
        DashDay.PixelsPerMinute = 1.5;
        DashWeek.RangeStart = Defaults.CalendarStart;
        DashWeek.RangeEnd = Defaults.CalendarEnd;
        DashRightColumn.Width = new GridLength(Math.Clamp(_settings.DashRightWidth, 300, 1000));
        DashWeek.DayClicked += day =>
        {
            _calDate = day;
            CalDayView.IsChecked = true;
            NavCalendar.IsChecked = true;
        };
    }

    private void DashView_Checked(object sender, RoutedEventArgs e) => RefreshDashboard(full: true);

    /// <summary>
    /// Updates the dashboard. The ring timer and numbers update every tick; the calendar redraws
    /// when the session changes, or once a minute.
    /// </summary>
    private void RefreshDashboard(bool full = false)
    {
        // Runs for the dashboard, and for the Pomodoro tab (which shows the dashboard's countdown and tasks).
        if (DashboardPanel == null || (DashboardPanel.Visibility != Visibility.Visible && PomodoroPanel?.Visibility != Visibility.Visible)) return;

        var (blocks, start) = TodaysSchedule();
        bool week = DashWeekView.IsChecked == true;
        if (full || _runner.Index != _lastDashBlockIndex || DateTime.UtcNow - _lastDashRenderUtc > TimeSpan.FromMinutes(1))
        {
            _lastDashRenderUtc = DateTime.UtcNow;
            _lastDashBlockIndex = _runner.Index;
            DashDay.Visibility = week ? Visibility.Collapsed : Visibility.Visible;
            DashWeek.Visibility = week ? Visibility.Visible : Visibility.Collapsed;
            DashShowPlanCheck.Visibility = week ? Visibility.Visible : Visibility.Collapsed;
            DashWeek.ShowPlan = _settings.WeekShowsPlan;
            DashTitle.Text = week ? "This week" : "Today, " + DateTime.Today.ToString("dddd MMMM d");
            if (week) DashWeek.Show(DateTime.Today, PlanFor);
            else
            {
                DashDay.Events = CalendarService.EventsOn(DateTime.Today);
                DashDay.Show(blocks, DateTime.Today + start);
                if (full) DashDay.ScrollTo(DateTime.Now.TimeOfDay - TimeSpan.FromMinutes(45));
            }
        }
        DashDay.UpdateNow();

        // Ring timer and the current session.
        var cur = _runner.Current;
        if (cur != null)
        {
            var remaining = _runner.Remaining;
            double total = TimeSpan.FromMinutes(cur.Minutes).TotalSeconds;
            DashTimer.AccentKey = AccentKey(cur);
            DashTimer.FractionLeft = total > 0 ? remaining.TotalSeconds / total : 0;
            DashTimer.Running = !_runner.IsPaused;
            DashKind.Text = cur.KindLabel.ToUpperInvariant() == "FOCUS" ? "Focus session" : cur.KindLabel;
            DashKind.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, AccentKey(cur));
            DashName.Text = cur.Name;
            DashMinutes.Text = Format.MinutesLeft(remaining);
            DashMinutesCaption.Text = _runner.IsPaused ? "Paused" : $"until {(DateTime.Now + remaining):t}";
            DashStartBtn.Visibility = Visibility.Collapsed;

            // Session details and controls (the same ones as the strip shown on other tabs).
            DashNext.Text = RunNext.Text;
            DashBlocking.Text = RunBlocking.Text;
            DashBlocking.Visibility = RunBlocking.Visibility;
            DashControls.Visibility = Visibility.Visible;
            DashPauseBtn.Content = PauseBtn.Content;
            DashPauseBtn.Visibility = PauseBtn.Visibility;
            DashSkipBtn.Visibility = SkipBtn.Visibility;
            DashStopBtn.Visibility = StopBtn.Visibility;
            DashLockedNote.Text = LockedNote.Text;
            DashLockedNote.Visibility = LockedNote.Visibility;
            DashPassButtons.Visibility = PassButtons.Visibility;
            DashPassActive.Visibility = PassActivePanel.Visibility;
            DashPassText.Text = PassText.Text;
        }
        else
        {
            DashTimer.AccentKey = "FocusBrush";
            DashTimer.FractionLeft = 1;
            DashTimer.Running = false;
            DashKind.Text = "Nothing running";
            DashKind.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Muted");
            DashName.Text = _planDate == DateTime.Today ? "Ready when you are." : "";
            DashMinutes.Text = "";
            DashMinutesCaption.Text = "Start today's plan to begin";
            DashStartBtn.Visibility = Visibility.Visible;
            DashNext.Text = "";
            DashBlocking.Visibility = Visibility.Collapsed;
            DashControls.Visibility = Visibility.Collapsed;
            DashLockedNote.Visibility = Visibility.Collapsed;
            DashPassButtons.Visibility = Visibility.Collapsed;
            DashPassActive.Visibility = Visibility.Collapsed;
        }

        RefreshDashTasks();

        // Totals (only focus that blocks at least one app counts).
        _stats ??= FocusStats.Load();
        DashToday.Text = Format.HoursMinutes(_stats.MinutesOn(DateTime.Today));
        DashWeekTotal.Text = Format.HoursMinutes(_stats.MinutesInWeekOf(DateTime.Today));
        DashLeft.Text = Format.HoursMinutes(BlockingFocusMinutesLeftToday(blocks, start));
    }

    /// <summary>Today's blocks and start time: the running day if there is one, otherwise today's plan.</summary>
    private (IReadOnlyList<PlanBlock> Blocks, TimeSpan Start) TodaysSchedule()
    {
        if (_mode == RunMode.DayPlan && _runner.IsRunning && _runner.ScheduleStartLocal is DateTime s)
            return (_runner.Blocks, s.TimeOfDay);
        return PlanFor(DateTime.Today);
    }

    /// <summary>Minutes of blocking focus still ahead today (the rest of the current one, plus later ones).</summary>
    private double BlockingFocusMinutesLeftToday(IReadOnlyList<PlanBlock> blocks, TimeSpan start)
    {
        // Pomodoro and quick focus aren't on a schedule: count what's left of the current session.
        if (_runner.IsRunning && _mode != RunMode.DayPlan)
        {
            var cur = _runner.Current;
            return cur != null && cur.IsFocus && cur.HasBlockedApps ? _runner.Remaining.TotalMinutes : 0;
        }

        var now = DateTime.Now.TimeOfDay;
        double minutes = 0;
        var t = start;
        foreach (var b in blocks)
        {
            var end = t + TimeSpan.FromMinutes(b.Minutes);
            if (b.IsFocus && b.HasBlockedApps && end > now)
                minutes += (end - (t > now ? t : now)).TotalMinutes;
            t = end;
        }
        return minutes;
    }
}
