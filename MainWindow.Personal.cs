using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// Personal tasks: their own page (views, projects, an editor), their own quick-add box (Ctrl+Win+P),
/// their own look, and a Personal list in Pomodoro. Separate from your main tasks in every other way.
/// </summary>
public partial class MainWindow
{
    private const int PersonalHotkeyId = 0x4650; // "FP"
    private string _personalKeys = "";
    private readonly HashSet<TaskItem> _hookedPersonalItems = new();

    private void SetUpPersonal()
    {
        PersonalTasks.Load();
        CleanWorkOutOfPersonal();
        TaskStores.Personal.Bin.CollectionChanged += (_, _) => { if (NavPersonal.IsChecked == true) RefreshTasksTab(); };
        PersonalTasks.Changed += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (NavPersonal.IsChecked == true) RefreshTasksTab();
            if (PomoPersonalHost.Visibility == Visibility.Visible) RefreshPomoPersonal();
        }));
        BuildAccentChoices();
        ApplyPersonalLook();
        ThemeManager.Changed += () => Dispatcher.BeginInvoke(new Action(ApplyPersonalLook)); // follows light and dark mode
        SetUpPersonalHotkey();
    }

    // ---------- The Personal look ----------

    /// <summary>Accent colors for Personal: (light mode, dark mode).</summary>
    private static readonly (string Name, string Light, string Dark)[] Accents =
    {
        ("Green", "#2F9E5E", "#5CC88A"), ("Teal", "#13897C", "#4CC3B5"), ("Blue", "#246FE0", "#6FA3F0"),
        ("Violet", "#7C5CD6", "#A88BF5"), ("Amber", "#C77D12", "#E8A84A"), ("Red", "#DC4C3E", "#F07A6E"),
    };

    /// <summary>The Personal theme (Calm, Classic or Monochrome, in the current light or dark mode) with its own accent.</summary>
    private ResourceDictionary PersonalLookResources()
    {
        string theme = _settings.PersonalTheme is "Classic" or "Mono" or "Calm" ? _settings.PersonalTheme : "Calm";
        bool dark = ThemeManager.IsDark;
        var look = new ResourceDictionary();
        try
        {
            look.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Themes/{(theme == "Classic" ? "" : theme)}{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute)
            });
        }
        catch (Exception ex) { Log.Error("PersonalLook theme", ex); }
        var accent = Accents.FirstOrDefault(a => a.Name == _settings.PersonalAccent);
        if (accent.Name == null) accent = Accents[0];
        var color = (Color)ColorConverter.ConvertFromString(dark ? accent.Dark : accent.Light);
        look["FocusBrush"] = new SolidColorBrush(color);
        look["FocusTint"] = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B));
        look["NavSelected"] = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        return look;
    }

    private void ApplyPersonalLook()
    {
        try
        {
            var look = PersonalLookResources();
            PomoPersonalHost.Resources = look;
            if (NavPersonal.IsChecked == true) TasksPanel.Resources = PersonalLookResources();
        }
        catch (Exception ex) { Log.Error("ApplyPersonalLook", ex); }
    }

    private void BuildAccentChoices()
    {
        PersonalAccents.Children.Clear();
        foreach (var (name, light, _) in Accents)
        {
            var dot = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 6, 0),
                                   Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light)) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var choice = new RadioButton { Content = row, Tag = name, GroupName = "paccent", Margin = new Thickness(0, 0, 4, 4), IsChecked = _settings.PersonalAccent == name };
            choice.SetResourceReference(StyleProperty, "NavButton");
            choice.Checked += (_, _) =>
            {
                if (_fillingSettings) return;
                _settings.PersonalAccent = name;
                ApplyPersonalLook();
            };
            PersonalAccents.Children.Add(choice);
        }
    }

    private void PersonalTheme_Checked(object sender, RoutedEventArgs e)
    {
        if (_fillingSettings || sender is not FrameworkElement { Tag: string theme }) return;
        _settings.PersonalTheme = theme;
        ApplyPersonalLook();
    }

    /// <summary>Called as Settings fill in: shows the current Personal look.</summary>
    private void FillPersonalLookSettings()
    {
        if (PersonalTheme_Calm == null) return;
        PersonalTheme_Classic.IsChecked = _settings.PersonalTheme == "Classic";
        PersonalTheme_Mono.IsChecked = _settings.PersonalTheme == "Mono";
        PersonalTheme_Calm.IsChecked = _settings.PersonalTheme is not ("Classic" or "Mono");
        foreach (var r in PersonalAccents.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Tag == _settings.PersonalAccent;
    }

    // ---------- Quick add: Ctrl+Win+P ----------

    /// <summary>The shortcuts you can choose for personal quick add: (name, modifiers, key).</summary>
    private static readonly (string Name, uint Mods, uint Key)[] PersonalKeyChoices =
    {
        ("Ctrl+Alt+Space", 0x2 | 0x1, 0x20), ("Ctrl+Alt+P", 0x2 | 0x1, 0x50), ("Ctrl+Shift+Alt+P", 0x2 | 0x4 | 0x1, 0x50),
        ("Ctrl+Alt+N", 0x2 | 0x1, 0x4E), ("Ctrl+Win+P", 0x2 | 0x8, 0x50),
    };

    private bool _personalHookAdded;

    /// <summary>
    /// Registers your chosen shortcut; if another app already has it, tries the others in turn and says which one works.
    /// </summary>
    private void SetUpPersonalHotkey()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            if (!_personalHookAdded) { System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(PersonalHotkeyHook); _personalHookAdded = true; }
            UnregisterHotKey(handle, PersonalHotkeyId);
            _personalKeys = "";
            const uint noRepeat = 0x4000;
            var order = PersonalKeyChoices.OrderBy(c => c.Name == _settings.PersonalHotkey ? 0 : 1).ToList();
            foreach (var (name, mods, key) in order)
                if (RegisterHotKey(handle, PersonalHotkeyId, mods | noRepeat, key)) { _personalKeys = name; break; }
        }
        catch (Exception ex) { Log.Error("SetUpPersonalHotkey", ex); }
        if (PersonalHotkeyBox.Items.Count == 0)
            foreach (var c in PersonalKeyChoices) PersonalHotkeyBox.Items.Add(c.Name);
        _fillingSettings = true;
        PersonalHotkeyBox.SelectedItem = _settings.PersonalHotkey;
        _fillingSettings = false;
        PersonalQuickAddInfo.Text = _personalKeys.Length == 0
            ? "None of these shortcuts could be set up (other apps already use them)."
            : _personalKeys == _settings.PersonalHotkey
                ? $"Press {_personalKeys} anywhere in Windows to add a personal task (it goes to Personal only)."
                : $"{_settings.PersonalHotkey} is already used by another app, so {_personalKeys} opens personal quick add instead.";
    }

    private void PersonalHotkey_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSettings || PersonalHotkeyBox.SelectedItem is not string chosen || chosen == _settings.PersonalHotkey) return;
        _settings.PersonalHotkey = chosen;
        SaveSettingsQuietly();
        SetUpPersonalHotkey();
    }

    private IntPtr PersonalHotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == PersonalHotkeyId)
        {
            handled = true;
            OpenPersonalQuickAdd();
        }
        return IntPtr.Zero;
    }

    private void OpenPersonalQuickAdd()
    {
        var box = new QuickAddWindow(text => PersonalTasks.Add(text, $"Quick add, {DateTime.Now:ddd MMM d}"));
        box.Resources.MergedDictionaries.Add(PersonalLookResources());
        box.Title = "Add a personal task";
        box.SetHeading("Add a personal task");
        box.Show();
    }

    /// <summary>
    /// Takes any work task (or finished work task) out of Personal's files. While a mistake had the Personal page
    /// showing the work list, ticking or editing tasks there could put work tasks into Personal. Work's own files
    /// were never changed, so only Personal needs tidying; this runs as Focus starts and does nothing once it's clean.
    /// </summary>
    private void CleanWorkOutOfPersonal()
    {
        try
        {
            var workIds = new HashSet<string>(TaskStores.Main.Bin.Select(t => t.Id));
            var workKeys = new HashSet<string>(TaskStores.Main.Bin.Where(t => t.CreatedAt != null).Select(t => t.Text + "|" + t.CreatedAt!.Value.Ticks)
                .Concat(TaskStores.Main.Resolved.Where(r => r.CreatedAt != null).Select(r => r.Text + "|" + r.CreatedAt!.Value.Ticks)));
            var workProjects = new HashSet<string>(TaskStores.Main.Projects.Select(p => p.Id));
            var strayTasks = TaskStores.Personal.Bin.Where(t => workIds.Contains(t.Id)).ToList();
            var strayDone = TaskStores.Personal.Resolved.Where(r => r.CreatedAt != null && workKeys.Contains(r.Text + "|" + r.CreatedAt.Value.Ticks)).ToList();
            var strayProjects = TaskStores.Personal.Projects.Where(p => workProjects.Contains(p.Id)).ToList();
            if (strayTasks.Count + strayDone.Count + strayProjects.Count == 0) return;
            foreach (var t in strayTasks) TaskStores.Personal.Bin.Remove(t);
            foreach (var r in strayDone) TaskStores.Personal.Resolved.Remove(r);
            foreach (var p in strayProjects) TaskStores.Personal.Projects.Remove(p);
            PersonalTasks.Save();
            Log.Write($"Personal: took out {strayTasks.Count} work tasks, {strayDone.Count} finished work tasks and {strayProjects.Count} work projects that had got in.");
        }
        catch (Exception ex) { Log.Error("CleanWorkOutOfPersonal", ex); }
    }

    // ---------- The Personal page: the full Tasks page, showing the Personal set ----------

    /// <summary>The set the Tasks page shows: Personal when its own sidebar button is chosen, otherwise Work.</summary>
    private TaskStore TasksPageSet => NavPersonal?.IsChecked == true ? TaskStores.Personal : TaskStores.Main;

    /// <summary>
    /// Opens the Tasks page for Work (the Tasks button) or Personal (the Personal button): the same page with every
    /// view, export, search, board and detail, on that set's tasks, in Personal's own colors and without the stats.
    /// </summary>
    private void ShowTasksPageForSet()
    {
        bool personal = NavPersonal.IsChecked == true;
        var before = TaskStores.Current;
        TaskStores.Show(personal ? TaskStores.Personal : TaskStores.Main);
        if (!ReferenceEquals(before, TaskStores.Current))
        {
            _binFilter = "all"; // project and tag filters belong to one set
            if (SearchBox != null) SearchBox.Text = "";
        }
        if (_binView != null && !ReferenceEquals(BinList.ItemsSource, _binView)) BinList.ItemsSource = _binView; // this set's own list
        TasksPanel.Resources = personal ? PersonalLookResources() : new ResourceDictionary();
        TasksTitle.Text = personal ? "Personal" : "Tasks";
        if (personal) TasksTitle.SetResourceReference(TextBlock.ForegroundProperty, "FocusBrush");
        else TasksTitle.ClearValue(TextBlock.ForegroundProperty);
        TasksStatsRow.Visibility = personal ? Visibility.Collapsed : Visibility.Visible;  // no stats for personal tasks
        TodayPanel.Visibility = personal || !_settings.On("todayPlanning") ? Visibility.Collapsed : Visibility.Visible; // about focus sessions
        RefreshProjects();
        RefreshTasksTab();
    }

    /// <summary>One personal task: a round checkbox (finishes it), its text and details. Click the text to edit.</summary>
    private FrameworkElement PersonalRow(BinTask t, bool compactForPomodoro)
    {
        var dock = new DockPanel();
        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0), ToolTip = "Done" };
        check.SetResourceReference(StyleProperty, "TaskCheck");
        if (t.IsHigh) check.SetResourceReference(BorderBrushProperty, "PriorityHigh");
        check.Click += (_, _) => { PersonalTasks.Finish(t); if (compactForPomodoro) RefreshPomoPersonal(); };
        DockPanel.SetDock(check, Dock.Left);
        dock.Children.Add(check);
        if (compactForPomodoro)
        {
            var add = new Button { Content = "Add to session", Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Top,
                                   ToolTip = "Add it to the running Pomodoro's focus session" };
            add.Click += (_, _) => AddPersonalToSession(t);
            DockPanel.SetDock(add, Dock.Right);
            dock.Children.Add(add);
        }
        var text = new StackPanel { Cursor = compactForPomodoro ? Cursors.Arrow : Cursors.Hand };
        text.Children.Add(new TextBlock { Text = t.Text, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        var bits = new List<string>();
        if (PersonalTasks.Book.Find(t.Project) is Project proj) bits.Add("#" + proj.Name);
        if (t.Due is DateTime due) bits.Add(due.Date == DateTime.Today ? "today" : due.Date == DateTime.Today.AddDays(1) ? "tomorrow" : due.ToString("ddd MMM d"));
        if (t.Repeat.Length > 0) bits.Add("repeats");
        if (t.Notes.Trim().Length > 0) bits.Add("notes");
        if (bits.Count > 0)
        {
            var meta = new TextBlock { Text = string.Join("   ", bits), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
            meta.SetResourceReference(TextBlock.ForegroundProperty, t.IsOverdue ? "PriorityHigh" : "Muted");
            text.Children.Add(meta);
        }
        dock.Children.Add(text);
        // The same looks as your main tasks (Default, Compact or Paper), in Personal's colors.
        var row = new Border { Child = dock };
        switch (TaskLook.Current)
        {
            case "compact": row.Padding = new Thickness(8, 3, 4, 3); row.Margin = new Thickness(0, 0, 0, 2); row.CornerRadius = new CornerRadius(6); row.SetResourceReference(Border.BackgroundProperty, "Panel2"); break;
            case "paper": row.Padding = new Thickness(4, 8, 4, 8); row.BorderThickness = new Thickness(0, 0, 0, 1); row.SetResourceReference(Border.BorderBrushProperty, "Line"); break;
            default: row.Padding = new Thickness(10, 8, 6, 8); row.Margin = new Thickness(0, 0, 0, 6); row.CornerRadius = new CornerRadius(8); row.SetResourceReference(Border.BackgroundProperty, "Panel2"); break;
        }
        return row;
    }

    // ---------- Pomodoro: the Personal list ----------

    private void PomoList_Checked(object sender, RoutedEventArgs e)
    {
        if (PomoPersonalHost == null || PomoTasksHost == null) return;
        bool personal = PomoListPersonal.IsChecked == true;
        PomoPersonalHost.Visibility = personal ? Visibility.Visible : Visibility.Collapsed;
        PomoTasksHost.Visibility = personal ? Visibility.Collapsed : Visibility.Visible;
        if (personal) RefreshPomoPersonal();
    }

    private void RefreshPomoPersonal()
    {
        PomoPersonalHost.Children.Clear();
        var tasks = PersonalTasks.View("today").ToList();
        string heading = "Today";
        if (tasks.Count == 0) { tasks = PersonalTasks.View("all").ToList(); heading = "All personal tasks"; }
        var head = new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "FocusBrush");
        PomoPersonalHost.Children.Add(head);
        if (tasks.Count == 0)
        {
            var none = new TextBlock { Text = "No personal tasks yet. Add them on the Personal page or with " + (_personalKeys.Length > 0 ? _personalKeys : "the Personal page") + ".", TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            PomoPersonalHost.Children.Add(none);
            return;
        }
        foreach (var t in tasks) PomoPersonalHost.Children.Add(PersonalRow(t, compactForPomodoro: true));
    }

    /// <summary>Adds a personal task to the running Pomodoro's focus session; finishing it there ticks it off in Personal.</summary>
    private void AddPersonalToSession(BinTask t)
    {
        if (!(_runner.IsRunning && _mode == RunMode.Pomodoro && _runner.Current is PlanBlock cur && cur.IsFocus))
        {
            _toast.ShowMessage("Start the Pomodoro first", "Personal tasks join the focus session that's running.");
            return;
        }
        if (cur.Tasks.Any(x => x.PersonalId == t.Id)) return;
        var item = new TaskItem { Text = t.Text, PersonalId = t.Id };
        cur.Tasks.Add(item);
        HookPersonalItem(item);
        SaveRunState();
        RefreshDashTasks();
        _toast.ShowMessage("Added to this session", t.Text);
    }

    /// <summary>Session tasks that came from Personal tick their personal task off when they're done.</summary>
    private void HookPersonalItem(TaskItem item)
    {
        if (!_hookedPersonalItems.Add(item)) return;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TaskItem.Done) && item.Done && item.PersonalId.Length > 0)
                Dispatcher.BeginInvoke(new Action(() => PersonalTasks.Finish(item.PersonalId)));
        };
    }

    /// <summary>
    /// Keeps session tasks connected to their lists (checked as the session ticks, and after a restart):
    /// ticking one that came from Personal finishes it there; ticking one that came from your task list finishes it
    /// there straight away, so it leaves the Tasks tab, the dashboard and every session it was in.
    /// </summary>
    private void HookPersonalItemsInSession()
    {
        var blocks = (_runner.IsRunning ? _runner.Blocks : Array.Empty<PlanBlock>()).Concat(_plan).Distinct();
        foreach (var item in blocks.SelectMany(b => b.Tasks))
        {
            if (item.PersonalId.Length > 0) HookPersonalItem(item);
            else if (item.BinId.Length > 0) HookLinkedItem(item);
        }
    }

    private readonly HashSet<TaskItem> _hookedLinkedItems = new();

    private void HookLinkedItem(TaskItem item)
    {
        if (!_hookedLinkedItems.Add(item)) return;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(TaskItem.Done) || !item.Done) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                using var onWork = TaskStores.Use(TaskStores.Main); // session tasks link to your work tasks
                if (FindLinked(item) is not BinTask original) return;
                PushUndo(includePlan: true);
                FinishListTask(original, $"Ticked in a session, {DateTime.Now:ddd MMM d}", item.Spent);
                RefreshTasksTab();
                UpdateOverlayTasks();
            }));
        };
    }
}
