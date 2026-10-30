using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>The Home page, the Day plan's edit popup, and Ctrl+Enter / Esc on every popup.</summary>
public partial class MainWindow
{
    // ---------- Home ----------

    private void SetUpHome()
    {
        HomeDay.RangeStart = Defaults.CalendarStart;
        HomeDay.RangeEnd = Defaults.CalendarEnd;
        HomeDay.ShowNowLine = true;
        HomeDay.BlockClicked += block =>
        {
            // Clicking a block on Home opens it in the Day plan, ready to edit.
            NavPlan.IsChecked = true;
            if (_planDate != DateTime.Today) LoadPlanForDate(DateTime.Today);
            if (_plan.Contains(block)) OpenBlockEditor(block);
        };
        HomeShowPlanCheck.IsChecked = _settings.HomeShowsPlan;
        if (_settings.PlanTasksDayOnly) PlanTasksDay.IsChecked = true;
        RefreshHome(scroll: true);
    }

    private void HomeShowPlan_Changed(object sender, RoutedEventArgs e)
    {
        if (HomeDay == null) return;
        _settings.HomeShowsPlan = HomeShowPlanCheck.IsChecked == true;
        SaveSettingsQuietly();
        RefreshHome();
    }

    private void HomeTasksView_Checked(object sender, RoutedEventArgs e) => RefreshHome();
    private void HomeOpenDashboard_Click(object sender, RoutedEventArgs e) => NavDashboard.IsChecked = true;

    private void HomePlanDay_Click(object sender, RoutedEventArgs e)
    {
        NavPlan.IsChecked = true;
        if (_planDate != DateTime.Today) LoadPlanForDate(DateTime.Today);
    }

    private void HomeTaskAdd_TextChanged(object sender, TextChangedEventArgs e) =>
        HomeTaskAddHint.Visibility = HomeTaskAdd.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void HomeTaskAdd_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        string text = HomeTaskAdd.Text.Trim();
        if (text.Length == 0) return;
        var task = new BinTask { From = $"Added {DateTime.Now:ddd MMM d}" };
        TaskParser.Apply(task, text);
        if (task.Text.Length == 0) return;
        // Added from Home's Today list: it's for today.
        if (HomeTasksToday.IsChecked == true) task.PlannedFor = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        PushUndo();
        TaskBin.All.Add(task);
        TaskBin.Save();
        HomeTaskAdd.Text = "";
    }

    /// <summary>Updates the Home page: greeting, numbers, today's calendar, and your tasks.</summary>
    private void RefreshHome(bool scroll = false)
    {
        if (HomePanel == null || (HomePanel.Visibility != Visibility.Visible && !scroll)) return;
        try
        {
            var now = DateTime.Now;
            string part = now.Hour < 12 ? "Good morning" : now.Hour < 17 ? "Good afternoon" : "Good evening";
            HomeGreeting.Text = $"{part}. It's {now:dddd, MMMM d}.";

            // A soft wash of the accent color behind the greeting (follows light and dark mode).
            var panel = ((SolidColorBrush)FindResource("Panel")).Color;
            var tint = ((SolidColorBrush)FindResource("FocusTint")).Color;
            HomeHero.Background = new LinearGradientBrush(panel, tint, 20);

            _stats ??= FocusStats.Load();
            int done = TasksDoneOn(DateTime.Today);
            double focusMinutes = _stats.MinutesOn(DateTime.Today);
            int sinceMonday = ((int)DateTime.Today.DayOfWeek + 6) % 7;
            HomeDoneToday.Text = done.ToString();
            HomeFocusToday.Text = Format.HoursMinutes(focusMinutes);
            HomeWeek.Text = ResolvedTasks.CountSince(DateTime.Today.AddDays(-sinceMonday)).ToString();
            int streak = _settings.On("goals") ? Streak() : 0;
            HomeStreak.Text = streak.ToString();
            HomeStreakLabel.Text = streak == 1 ? "day streak" : "day streak";

            // What's happening now, or next.
            var (blocks, start) = TodaysSchedule();
            string nowLine;
            if (_runner.Current is PlanBlock cur)
                nowLine = $"Now: {cur.Name}, {Format.MinutesLeft(_runner.Remaining)} left.";
            else
            {
                var t = start;
                PlanBlock? next = null;
                TimeSpan nextAt = default;
                foreach (var b in blocks)
                {
                    if (t >= now.TimeOfDay && b.IsFocus) { next = b; nextAt = t; break; }
                    t += TimeSpan.FromMinutes(b.Minutes);
                }
                nowLine = next != null ? $"Next: {next.Name} at {DateTime.Today.Add(nextAt):t}." : "Nothing else planned today.";
            }
            HomeNowText.Text = nowLine;

            int toDo = TaskBin.All.Count(x => !x.IsSnoozed);
            HomeSubline.Text = done == 0 && focusMinutes < 1
                ? $"{toDo} tasks are waiting. Pick one and start a session. Small steps count."
                : $"{done} {(done == 1 ? "task" : "tasks")} finished and {Format.HoursMinutes(focusMinutes)} of focus so far today. Keep it rolling.";

            bool running = _runner.IsRunning;
            HomeStartBtn.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            HomeOpenDashBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

            // Today's calendar, with or without the plan.
            bool showPlan = HomeShowPlanCheck.IsChecked == true;
            HomeDay.Events = CalendarService.EventsOn(DateTime.Today);
            HomeDay.Show(showPlan ? blocks : Array.Empty<PlanBlock>(), DateTime.Today + (showPlan ? start : Defaults.CalendarStart));
            if (scroll) HomeDay.ScrollTo(now.TimeOfDay - TimeSpan.FromMinutes(45));
            HomeDay.UpdateNow();

            // Your tasks: today's (picked, due, overdue, or placed in today's sessions), or all of them.
            bool todayOnly = HomeTasksToday.IsChecked == true;
            var placed = new HashSet<string>(blocks.SelectMany(b => b.Tasks).Select(x => x.BinId).Where(id => id.Length > 0));
            var tasks = TaskBin.All
                .Where(x => !(x.IsSnoozed && _settings.On("upcoming")))
                .Where(x => !todayOnly || x.IsPlannedToday || x.IsDueToday || x.IsOverdue || placed.Contains(x.Id))
                .OrderBy(TaskBase.Rank).ThenBy(x => x.Due ?? DateTime.MaxValue).ThenBy(x => TaskBin.All.IndexOf(x))
                .ToList();
            HomeTaskList.ItemsSource = tasks;
            HomeTasksEmpty.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HomeTasksEmpty.Text = todayOnly
                ? "Nothing set for today yet. Add a task above, or switch to All and pick some with ☀ in the Tasks tab."
                : "No tasks. Add one above.";
        }
        catch (Exception ex)
        {
            Log.Error("RefreshHome", ex);
        }
    }

    // ---------- Day plan: the edit popup ----------

    /// <summary>Selects a block and opens its editor next to the mouse, with the title ready to type over.</summary>
    private void OpenBlockEditor(PlanBlock block)
    {
        SelectBlock(block);
        // Open once the click has fully finished (and the timeline has let go of the mouse);
        // opening mid-click is what made the editor sometimes stick or ignore clicks.
        System.Windows.Input.Mouse.Capture(null);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_selected, block) || !_plan.Contains(block)) return;
            BlockPopup.IsOpen = false;
            BlockPopup.PlacementTarget = Timeline;
            BlockPopup.IsOpen = true;
            FocusTitle(EditorTitleBox);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseBlockEditor()
    {
        if (!BlockPopup.IsOpen) return;
        EditorMinutes.Commit(); // keep a length that was typed but not yet entered
        BlockPopup.IsOpen = false;
    }

    /// <summary>True if a click landed inside one of the popups (rather than the main window).</summary>
    private bool IsInPopup(object source) =>
        source is DependencyObject d && PresentationSource.FromDependencyObject(d) is PresentationSource ps &&
        !ReferenceEquals(ps, PresentationSource.FromVisual(this));

    private void BlockPopupClose_Click(object sender, RoutedEventArgs e) => CloseBlockEditor();

    private void BlockPopup_Closed(object? sender, EventArgs e) => EditorMinutes.Commit();

    /// <summary>Ctrl+Enter or Esc saves and closes the block editor (changes are saved as you type anyway).</summary>
    private void BlockPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrlEnter = e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!ctrlEnter && e.Key != Key.Escape) return;
        e.Handled = true;
        CloseBlockEditor();
    }

    /// <summary>Ctrl+Enter saves and closes the task panel; Esc closes it without saving.</summary>
    private void TaskPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            TaskPopupSave_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            TaskPopup.IsOpen = false;
        }
    }

    /// <summary>
    /// The other popups: Ctrl+Enter does their main action (or just closes them), Esc closes them.
    /// Popups opened from inside the block editor keep it open underneath.
    /// </summary>
    private void SetUpPopupKeys()
    {
        void Keys(Popup popup, Action ctrlEnter)
        {
            if (popup.Child is not UIElement child) return;
            child.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; ctrlEnter(); }
                else if (e.Key == Key.Escape) { e.Handled = true; popup.IsOpen = false; }
            };
        }
        Keys(AdjustDayPopup, () => AdjustApply_Click(this, new RoutedEventArgs()));
        Keys(TemplatePopup, () => TemplateSave_Click(this, new RoutedEventArgs()));
        Keys(PickerPopup, () => PickerPopup.IsOpen = false);
        Keys(ListPickerPopup, () => ListPickerPopup.IsOpen = false);
        Keys(BinPickerPopup, () => BinPickerPopup.IsOpen = false);
        Keys(LayoutsPopup, () => LayoutsPopup.IsOpen = false);

        // The block editor closes when you click anywhere else in the window (the click still
        // goes through, so clicking another block opens that one), or switch to another app.
        PreviewMouseDown += (_, e) =>
        {
            if (BlockPopup.IsOpen && !IsInPopup(e.OriginalSource)) CloseBlockEditor();
        };
        Deactivated += (_, _) =>
        {
            if (BlockPopup.IsOpen && !PickerPopup.IsOpen && !ListPickerPopup.IsOpen && !BinPickerPopup.IsOpen && !TaskPopup.IsOpen)
                CloseBlockEditor();
        };
    }

    /// <summary>Kept for the pickers that open from inside the block editor (it stays open by itself now).</summary>
    private void KeepBlockEditorOpen() { }
}
