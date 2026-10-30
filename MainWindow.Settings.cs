using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>
/// The Settings page: categories on the left (General, Appearance, Planning ...), one page at a time on the
/// right, a search box that finds any setting, "Reset this page", and changes that save themselves.
/// </summary>
public partial class MainWindow
{
    private bool _fillingSettings;   // filling the fields in: changes then aren't yours, so they aren't saved
    private bool _autoSaving;        // saving by itself: problems show quietly under the categories
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string _settingsPage = "general";

    private void SetUpSettingsPage()
    {
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            if (_fillingSettings) return;
            if (TypingInNumberBox()) return; // saved when you leave the box (or switch pages)
            AutoSaveSettings();
        };
        // Any change on the settings pages saves shortly afterwards: ticks, choices, sliders, buttons,
        // and typing (once you leave the box).
        RoutedEventHandler changed = (_, _) => QueueAutoSave();
        SettingsScroll.AddHandler(ToggleButton.CheckedEvent, changed, true);
        SettingsScroll.AddHandler(ToggleButton.UncheckedEvent, changed, true);
        SettingsScroll.AddHandler(ButtonBase.ClickEvent, changed, true);
        SettingsScroll.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => QueueAutoSave()), true);
        SettingsScroll.AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => QueueAutoSave()), true);
        SettingsScroll.AddHandler(UIElement.LostFocusEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is TextBox) QueueAutoSave(); }), true);
        // Typing saves too, a moment after you pause (number boxes wait until you've finished with them).
        SettingsScroll.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => QueueAutoSave()), true);
        // Switching away from Focus saves anything still being edited.
        Deactivated += (_, _) => FlushSettings();
    }

    /// <summary>
    /// Saves straight away (including a number still being typed): when you switch Settings pages, leave
    /// Settings, switch to another program, or press Save changes.
    /// </summary>
    private void FlushSettings()
    {
        if (_fillingSettings || SettingsPanel == null || SettingsPanel.Visibility != Visibility.Visible) return;
        _autoSaveTimer.Stop();
        AutoSaveSettings();
    }

    private void SaveSettingsPage_Click(object sender, RoutedEventArgs e)
    {
        _autoSaveTimer.Stop();
        AutoSaveSettings();
        if (SettingsSavedText.Text == "All changes saved") ShowSettingsStatus("Saved");
    }

    /// <summary>A number box you're still typing in: its save waits until you leave it (so the number isn't rewritten mid-typing).</summary>
    private static bool TypingInNumberBox()
    {
        if (System.Windows.Input.Keyboard.FocusedElement is not DependencyObject focused) return false;
        for (DependencyObject? d = focused; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is NumberBox) return true;
        return false;
    }

    private void QueueAutoSave()
    {
        if (_fillingSettings || SettingsPanel.Visibility != Visibility.Visible) return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void AutoSaveSettings()
    {
        _autoSaving = true;
        try { SaveSettings_Click(this, new RoutedEventArgs()); }
        finally { _autoSaving = false; }
    }

    /// <summary>Called as the fields are filled in, so the filling itself isn't taken as a change.</summary>
    private void BeginFillingSettings()
    {
        _fillingSettings = true;
        Dispatcher.BeginInvoke(new Action(() => _fillingSettings = false), DispatcherPriority.ContextIdle);
        if (TaskLook_default == null) return;
        TaskLook_default.IsChecked = _settings.TaskLook is not ("compact" or "paper");
        TaskLook_compact.IsChecked = _settings.TaskLook == "compact";
        TaskLook_paper.IsChecked = _settings.TaskLook == "paper";
        FillPersonalLookSettings();
    }

    /// <summary>Shows "All changes saved", or (quietly, in red) what couldn't be saved.</summary>
    private void ShowSettingsStatus(string? problem)
    {
        SettingsSavedText.Text = problem ?? "All changes saved";
        if (problem != null) SettingsSavedText.SetResourceReference(TextBlock.ForegroundProperty, "PriorityHigh");
        else SettingsSavedText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        SettingsSavedText.Visibility = Visibility.Visible;
    }

    private void TaskLook_Checked(object sender, RoutedEventArgs e)
    {
        if (_fillingSettings || sender is not FrameworkElement { Tag: string look }) return;
        _settings.TaskLook = look;
        TaskLook.Apply(look);
    }

    // ---------- Pages ----------

    private void SettingsNav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key } || SettingsScroll == null) return;
        FlushSettings(); // save what you changed on the page you're leaving
        _settingsPage = key;
        if (SettingsSearchBox.Text.Length > 0) SettingsSearchBox.Text = ""; // leaving search shows the chosen page
        else ShowSettingsPage(key);
    }

    private IEnumerable<StackPanel> SettingsPages() =>
        new[] { "general", "appearance", "planning", "sessions", "alerts", "tasks", "calendar", "greek", "data", "features", "help" }
            .Select(k => FindName("SettingsPage_" + k) as StackPanel).Where(p => p != null)!;

    private void ShowSettingsPage(string key)
    {
        foreach (var page in SettingsPages())
        {
            page.Visibility = (string)page.Tag == key ? Visibility.Visible : Visibility.Collapsed;
            foreach (var section in Sections(page)) section.Visibility = Visibility.Visible;
        }
        SettingsNoResults.Visibility = Visibility.Collapsed;
        SettingsScroll.ScrollToHome();
    }

    private static IEnumerable<FrameworkElement> Sections(Panel page) =>
        page.Children.OfType<FrameworkElement>().Where(c => c.Tag as string == "section");

    // ---------- Search ----------

    /// <summary>Typing shows every section, on any page, that mentions what you typed (Enter opens the first one's page).</summary>
    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = SettingsSearchBox.Text.Trim();
        SettingsSearchHint.Visibility = SettingsSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (q.Length == 0) { ShowSettingsPage(_settingsPage); return; }
        bool any = false;
        foreach (var page in SettingsPages())
        {
            bool pageHit = false;
            foreach (var section in Sections(page))
            {
                bool hit = !HiddenForGood(section) && TextOf(section).Contains(q, StringComparison.CurrentCultureIgnoreCase);
                section.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
                pageHit |= hit;
            }
            page.Visibility = pageHit ? Visibility.Visible : Visibility.Collapsed;
            any |= pageHit;
        }
        SettingsNoResults.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        SettingsScroll.ScrollToHome();
    }

    private void SettingsSearch_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { SettingsSearchBox.Text = ""; e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            var first = SettingsPages().FirstOrDefault(p => p.Visibility == Visibility.Visible);
            if (first == null) return;
            string key = (string)first.Tag;
            var nav = SettingsNav.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == key);
            SettingsSearchBox.Text = "";
            if (nav != null) nav.IsChecked = true;
            ShowSettingsPage(key);
        }
    }

    /// <summary>Sections that are permanently hidden (like the old website settings) never appear in search.</summary>
    private static bool HiddenForGood(FrameworkElement section) =>
        section is Panel p && p.Children.Count == 1 && p.Children[0] is FrameworkElement only && only.Visibility == Visibility.Collapsed;

    private static string TextOf(DependencyObject root)
    {
        var parts = new List<string>();
        void Walk(object node)
        {
            switch (node)
            {
                case TextBlock t: parts.Add(t.Text); break;
                case HeaderedContentControl h when h.Header is string hs: parts.Add(hs); break;
                case ContentControl c when c.Content is string cs: parts.Add(cs); break;
            }
            if (node is FrameworkElement { ToolTip: string tip }) parts.Add(tip);
            if (node is DependencyObject d)
                foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
        }
        Walk(root);
        return string.Join(" ", parts);
    }

    // ---------- Reset this page ----------

    /// <summary>The settings each page holds (remembered positions, dates and other state are never reset).</summary>
    private static readonly Dictionary<string, string[]> PageSettings = new()
    {
        ["general"] = new[] { "StartWithWindows", "MorningReminderOn", "ReminderTime", "WorkdayStart", "WorkdayEnd", "AutoStartWorkdays", "CountWeekends", "GoalTasks", "GoalFocusHours", "NudgesOn" },
        ["appearance"] = new[] { "Theme", "ColorTheme", "DisplayScale", "TaskFontSize", "CalendarSpacing", "TaskLook", "PersonalTheme", "PersonalAccent" },
        ["planning"] = new[] { "WeekShowsPlan", "HomeShowsPlan", "PlanTasksDayOnly", "DefaultPlanLists", "DefaultPlanApps" },
        ["sessions"] = new[] { "FocusMinutes", "BreakMinutes", "PomodoroLists", "QuickFocusLists", "LockedMode", "ExtremeLock", "ExtendMinutes" },
        ["alerts"] = new[] { "SoundsOn", "Volume", "ShowFloatingTimer", "OverlayAlwaysOn", "OverlayRows", "AlertSeconds", "WarningSeconds", "OverlaySeconds", "FlashOnChange" },
        ["tasks"] = new[] { "WaitingReminderDays" },
        ["calendar"] = new[] { "NewMeetings" },
        ["greek"] = new[] { "VocabPerDay", "VocabWords", "VocabFrom", "VocabTo", "VocabAutoAdd", "VocabRetireAfter", "VocabRetireThreshold",
                            "AlphaPerDay", "AlphaLetters", "AlphaPairs", "AlphaFrom", "AlphaTo", "ConceptFrom", "ConceptTo", "ConceptPerDay", "ConceptCount", "VocabAwayOn", "VocabAwayMinutes",
                            "AlphaAwayOn", "AlphaAwayMinutes", "ConceptAwayOn", "ConceptAwayMinutes", "VocabExtras", "AlphaExtras", "ConceptExtras" },
        ["data"] = new[] { "RecordFallbackDays" },
    };

    private void ResetSettingsPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key } || !PageSettings.TryGetValue(key, out var names)) return;
        string label = SettingsNav.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == key)?.Content as string ?? key;
        if (MessageBox.Show(this, $"Put the {label} settings back to how they started?", "Settings", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var fresh = new AppSettings();
        foreach (var name in names)
        {
            var prop = typeof(AppSettings).GetProperty(name);
            if (prop == null || !prop.CanWrite) continue;
            var value = prop.GetValue(fresh);
            // Lists are copied, so the defaults are never shared.
            if (value is System.Collections.IList list && value.GetType().IsGenericType)
                value = Activator.CreateInstance(value.GetType(), list);
            prop.SetValue(_settings, value);
        }
        TaskLook.Apply(_settings.TaskLook);
        ApplyPersonalLook();
        FillSettingFields();
        FillPomodoroFields();
        _fillingSettings = false;
        AutoSaveSettings();
        ShowSettingsStatus($"{label} settings reset");
    }
}
