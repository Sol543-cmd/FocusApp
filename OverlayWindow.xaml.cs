using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>The small translucent timer that floats above other windows.</summary>
public partial class OverlayWindow : Window
{
    public event Action? OpenRequested;

    /// <summary>A task was ticked or unticked in the expanded list.</summary>
    public event Action<OverlayTask>? TaskToggled;

    /// <summary>Whether hovering shows the session's tasks (Settings, Features).</summary>
    public bool ShowTasksOnHover { get; set; } = true;

    private readonly System.Windows.Threading.DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private IReadOnlyList<OverlayTask> _tasks = Array.Empty<OverlayTask>();
    private string _shown = "";      // what the list shows now, so it only rebuilds when the tasks change
    private string _view = "session";   // "session", "today" or "personal"

    /// <summary>Pinned: the tasks stay open until you unpin them (or your day ends).</summary>
    public bool Pinned { get; private set; }
    public event Action<bool>? PinChanged;
    public event Action<string>? ViewChanged;        // "session", "today" or "personal"
    public event Action? HideRequested;              // the ✕

    /// <summary>Which tasks show: today's (true) or this session's. Owned here, so the switch flips at once.</summary>
    public bool TodayView => _view == "today";
    public string View => _view;

    /// <summary>What the Personal tab shows: "today", "week", "all", or "project:ID" (chosen with the caret beside it).</summary>
    public string PersonalScope { get; private set; } = "today";
    public event Action<string>? PersonalScopeChanged;
    /// <summary>Your personal projects (ID and name), for the caret's menu.</summary>
    public Func<IEnumerable<(string Id, string Name)>>? PersonalProjects { get; set; }

    public void SetPersonalScope(string scope)
    {
        PersonalScope = scope is "today" or "week" or "all" || scope.StartsWith("project:") ? scope : "today";
        UpdateViewLook();
    }

    private string PersonalScopeName()
    {
        switch (PersonalScope)
        {
            case "week": return "This week";
            case "all": return "All";
            case "today": return "Today";
        }
        string id = PersonalScope.StartsWith("project:") ? PersonalScope[8..] : "";
        return PersonalProjects?.Invoke().FirstOrDefault(p => p.Id == id).Name ?? "Project";
    }

    private void PersonalScope_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = PersonalScopeBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string scope, string text)
        {
            var item = new System.Windows.Controls.MenuItem { Header = text, IsCheckable = true, IsChecked = PersonalScope == scope };
            item.Click += (_, _) =>
            {
                PersonalScope = scope;
                _shown = "";
                PersonalScopeChanged?.Invoke(scope);
                if (_view != "personal") SwitchView("personal"); else { UpdateViewLook(); ViewChanged?.Invoke("personal"); }
            };
            menu.Items.Add(item);
        }
        Add("today", "Today");
        Add("week", "This week");
        Add("all", "All");
        var projects = PersonalProjects?.Invoke().ToList() ?? new();
        if (projects.Count > 0)
        {
            menu.Items.Add(new System.Windows.Controls.Separator());
            foreach (var (id, name) in projects) Add("project:" + id, name);
        }
        menu.IsOpen = true;
    }

    private const double RowHeight = 27;

    /// <summary>How many tasks show before the list scrolls (Settings; resets to this each time Focus opens).</summary>
    public void SetRows(int rows) => TaskScroll.MaxHeight = Math.Clamp(rows, 2, 40) * RowHeight + 4;

    /// <summary>Starts in today's view or this session's.</summary>
    public void SetView(string view) { _view = view is "today" or "personal" ? view : "session"; UpdateViewLook(); }

    private void UpdateViewLook()
    {
        foreach (var (button, view) in new[] { (SessionViewBtn, "session"), (TodayViewBtn, "today"), (PersonalViewBtn, "personal") })
        {
            button.FontWeight = _view == view ? FontWeights.SemiBold : FontWeights.Normal;
            button.Opacity = _view == view ? 1 : 0.65;
        }
        // The Personal tab shows its current choice, like "Personal · This week".
        // Trimmed to fit, so a long project name never pushes the caret (or the pin) out of reach.
        string label = PersonalScope == "today" ? "Personal" : $"Personal · {PersonalScopeName()}";
        PersonalViewBtn.Content = new System.Windows.Controls.TextBlock { Text = label, MaxWidth = 118, TextTrimming = TextTrimming.CharacterEllipsis };
        PersonalViewBtn.ToolTip = label.Length > 18 ? label : null;
    }
    private double _movedUpBy;

    public OverlayWindow()
    {
        InitializeComponent();
        BadgeText.FontFamily = SessionLook.NumeralFont;
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); Collapse(); };
    }

    /// <summary>The tasks to show when expanded: this session's (or the next one's in a break), or today's.</summary>
    public void SetTasks(IReadOnlyList<OverlayTask> tasks, string header, string view, string empty)
    {
        _tasks = tasks;
        if (view != _view) return; // an update meant for another view (you just switched): skip it
        TasksHeader.Text = header;
        TasksHeader.Visibility = header.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = empty;
        UpdateViewLook();
        if (TasksPanel.Visibility == Visibility.Visible) FillTasks();
        else if (Pinned) Expand();
        CaretBtn.Visibility = ShowTasksOnHover ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FillTasks()
    {
        // Rebuild only when something changed (rebuilding every second would swallow clicks).
        string now = string.Join("|", _tasks.Select(t => t.Key + (t.Done ? "1" : "0") + t.Text));
        if (now != _shown)
        {
            _shown = now;
            TaskList.ItemsSource = _tasks.ToList();
        }
        EmptyText.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskScroll.Visibility = _tasks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Expand(bool force = false)
    {
        if (TasksPanel.Visibility == Visibility.Visible) return;
        if (!force && !Pinned) return; // opens from the caret (or when pinned)
        FillTasks();
        if (!IsVisible) { TasksPanel.Visibility = Visibility.Visible; CaretBtn.Content = "▴"; _movedUpBy = 0; return; } // opens with the timer
        double before = ActualHeight;
        TasksPanel.Visibility = Visibility.Visible;
        CaretBtn.Content = "▴";
        CaretBtn.ToolTip = "Hide your tasks";
        UpdateLayout();
        // Near the bottom of the screen, grow upward instead.
        var area = SystemParameters.WorkArea;
        double grow = ActualHeight - before;
        _movedUpBy = Top + ActualHeight > area.Bottom ? Math.Min(grow, Top - area.Top) : 0;
        Top -= _movedUpBy;
    }

    private void Collapse()
    {
        if (Pinned || TasksPanel.Visibility != Visibility.Visible) return;
        TasksPanel.Visibility = Visibility.Collapsed;
        CaretBtn.Content = "▾";
        CaretBtn.ToolTip = "Show your tasks";
        Top += _movedUpBy;
        _movedUpBy = 0;
    }

    public void SetPinned(bool pinned)
    {
        Pinned = pinned;
        UpdatePinLook();
        if (pinned) Expand();
        else if (!IsMouseOver) Collapse();
    }

    private void UpdatePinLook()
    {
        PinBtn.Opacity = Pinned ? 1 : 0.45;
        PinBtn.ToolTip = Pinned ? "Unpin (the tasks fold up when the cursor leaves)" : "Keep the tasks open";
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        SetPinned(!Pinned);
        PinChanged?.Invoke(Pinned);
    }

    private void SessionView_Click(object sender, RoutedEventArgs e) => SwitchView("session");
    private void TodayView_Click(object sender, RoutedEventArgs e) => SwitchView("today");
    private void PersonalView_Click(object sender, RoutedEventArgs e) => SwitchView("personal");

    private void SwitchView(string view)
    {
        if (view == _view) return;
        _view = view;
        _shown = "";              // rebuild for the new view
        UpdateViewLook();         // flips right away
        ViewChanged?.Invoke(view);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

    /// <summary>Dragging the grip makes the list taller or shorter (for now; it's back to your default next time).</summary>
    private void ResizeGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        double now = double.IsInfinity(TaskScroll.MaxHeight) ? TaskScroll.ActualHeight : TaskScroll.MaxHeight;
        double area = SystemParameters.WorkArea.Height - 120;
        TaskScroll.MaxHeight = Math.Clamp(now + e.VerticalChange, RowHeight * 2, Math.Max(RowHeight * 2, area));
        // Show the extra room straight away, even if the list is short.
        TaskScroll.Height = double.NaN;
    }

    private void Task_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OverlayTask t) TaskToggled?.Invoke(t);
    }

    /// <param name="fractionLeft">1 at the start of a block, 0 when it ends.</param>
    /// <param name="focusNumber">For a focus session, its number (shown as a Roman numeral); 0 for breaks, meetings and idle.</param>
    public void Update(string time, string name, Brush accent, double fractionLeft, int focusNumber = 0)
    {
        TimeText.Text = time;
        bool focus = focusNumber > 0;
        // Focus sessions: FOCUS SESSION (or the session's own name) in small spaced capitals.
        NameText.Text = focus ? SessionLook.Label(name) : name;
        NameText.FontSize = focus ? 10.5 : 13;
        NameText.FontWeight = focus ? FontWeights.SemiBold : FontWeights.Normal;
        NameText.ToolTip = focus && SessionLook.IsNamed(name) ? name : null;
        Badge.Visibility = focus ? Visibility.Visible : Visibility.Collapsed;
        Dot.Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
        BadgeText.Text = focus ? SessionLook.Roman(focusNumber) : "";
        Badge.BorderBrush = accent;
        Dot.Fill = accent;
        Fill.Fill = accent;
        FillScale.ScaleX = Math.Clamp(fractionLeft, 0, 1);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Round the corners of the pill, including the time-left bar inside it.
        double r = e.NewSize.Height / 2;
        Root.Clip = new RectangleGeometry(new Rect(e.NewSize), r, r);
        Outline.CornerRadius = new CornerRadius(r);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TasksPanel.IsMouseOver) return; // using the tasks, not moving the timer
        if (e.ClickCount == 2)
        {
            OpenRequested?.Invoke();
            return;
        }
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!Pinned) _collapseTimer.Start(); // unpinned: folds up shortly after the cursor leaves
    }

    /// <summary>The caret drops your tasks down, or folds them up (unpinning them if they were pinned).</summary>
    private void Caret_Click(object sender, RoutedEventArgs e)
    {
        if (TasksPanel.Visibility == Visibility.Visible)
        {
            if (Pinned) { Pinned = false; PinChanged?.Invoke(false); UpdatePinLook(); }
            Collapse();
        }
        else Expand(force: true);
    }
}

/// <summary>A task as the floating timer shows it: a session task or one of today's tasks.</summary>
public sealed class OverlayTask
{
    public object Source { get; init; } = null!;
    /// <summary>Which list it's from ("session", "main" or "personal") and its ID: a tick finds the current task by these,
    /// since Focus replaces task objects with fresh copies (when the plan changes, a sync arrives, or a session is restored).</summary>
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    public string Key { get; init; } = "";
    public string Text { get; init; } = "";
    public bool IsHigh { get; init; }
    public bool Done { get; set; }
}
