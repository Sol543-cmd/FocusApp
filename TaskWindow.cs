using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>Today's views (Default, Ordered, By project): ordering and the project headings, shared by every task list.</summary>
public static class TaskGroups
{
    /// <summary>Orders today's tasks for a view: Ordered by your own order (new ones after), By project by the project order.</summary>
    public static List<BinTask> Order(IEnumerable<BinTask> tasks, string view, IList<string> order)
    {
        var list = tasks.ToList();
        if (view == "ordered")
        {
            var pos = order.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
            return list.Select((t, i) => (t, i)).OrderBy(x => pos.TryGetValue(x.t.Id, out int p) ? p : int.MaxValue).ThenBy(x => x.i).Select(x => x.t).ToList();
        }
        if (view == "project")
        {
            var projects = Projects.All.Select(p => p.Id).ToList();
            int Rank(BinTask t) { int i = projects.IndexOf(t.Project); return i < 0 ? int.MaxValue : i; }
            return list.Select((t, i) => (t, i)).OrderBy(x => Rank(x.t)).ThenBy(x => x.i).Select(x => x.t).ToList();
        }
        return list;
    }

    public static string Heading(BinTask t) => t.ProjectName.Length > 0 ? t.ProjectName : "No Project";

    private static GroupStyle? _style;

    /// <summary>A list's items, grouped under project headings when they carry one (OverlayTask.Group).</summary>
    public static System.Collections.IEnumerable Source(List<OverlayTask> tasks, ItemsControl list)
    {
        if (tasks.All(t => t.Group.Length == 0)) return tasks;
        if (list.GroupStyle.Count == 0) list.GroupStyle.Add(Style());
        var view = new ListCollectionView(tasks);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OverlayTask.Group)));
        return view;
    }

    /// <summary>A project heading: its color dot, its name and a thin line.</summary>
    public static GroupStyle Style()
    {
        if (_style != null) return _style;
        var template = (DataTemplate)XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<DockPanel Margin='2,10,0,4'>" +
            "<Ellipse Width='8' Height='8' Margin='0,0,8,0' VerticalAlignment='Center' Fill='{Binding Items[0].GroupColor}'/>" +
            "<TextBlock Text='{Binding Name}' FontSize='12' FontWeight='SemiBold' Foreground='{DynamicResource Muted}' VerticalAlignment='Center' Margin='0,0,8,0'/>" +
            "<Rectangle Height='1' Fill='{DynamicResource Line}' VerticalAlignment='Center'/>" +
            "</DockPanel></DataTemplate>");
        _style = new GroupStyle { HeaderTemplate = template };
        return _style;
    }
}

/// <summary>
/// The task window: a real window (taskbar button, resizable, remembers its place) for a side screen. On top, the
/// thermometer's bar (time left draining, the session's numeral, time and name); then how much of today is done; then a
/// list you choose like the Dashboard's (Session, Today, All, Undated), Today in the view set in Tasks. Tick tasks
/// off here; when Today is Ordered, drag them into order. The pin keeps the window on top of others.
/// </summary>
public class TaskWindow : Window
{
    private readonly Func<string, List<OverlayTask>> _tasks;
    private readonly Action<OverlayTask> _toggled;
    private readonly Action<string, List<string>> _reordered;
    private readonly Func<string, string> _listMode;
    private readonly Func<(int Done, int Total, string Left)> _progress;
    private readonly AppSettings _settings;
    private readonly Action _save;

    private readonly Grid _pill = new() { Height = 40, Margin = new Thickness(12, 12, 12, 8) };
    private readonly Rectangle _fill = new() { Opacity = 0.45, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _badge = new() { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.3), Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _badgeText = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = new() { FontSize = 13, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _dayLeft = new() { FontSize = 12 }, _dayRight = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border _dayFill = new() { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ItemsControl _list = new();
    private readonly TextBlock _empty = new() { Margin = new Thickness(16, 8, 16, 8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _viewText = new() { FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pin = new() { Padding = new Thickness(8, 4, 8, 4), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13 };
    private string _shown = "";
    private double _fraction;

    public TaskWindow(AppSettings settings, Action save, Func<string, List<OverlayTask>> tasks, Action<OverlayTask> toggled,
                      Action<string, List<string>> reordered, Func<string, string> listMode, Func<(int Done, int Total, string Left)> progress)
    {
        _settings = settings; _save = save; _tasks = tasks; _toggled = toggled; _reordered = reordered; _listMode = listMode; _progress = progress;
        if (_settings.TaskWindowView == "all") _settings.TaskWindowView = "list"; // All is now List
        Title = "Focus · Tasks";
        Width = Math.Max(240, settings.TaskWindowWidth); Height = Math.Max(300, settings.TaskWindowHeight);
        MinWidth = 220; MinHeight = 260;
        if (!double.IsNaN(settings.TaskWindowLeft) && !double.IsNaN(settings.TaskWindowTop) && OnScreen(settings.TaskWindowLeft, settings.TaskWindowTop))
        { WindowStartupLocation = WindowStartupLocation.Manual; Left = settings.TaskWindowLeft; Top = settings.TaskWindowTop; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = settings.TaskWindowOnTop;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        FontSize = 14;

        // The bar: like the thermometer.
        var bg = new Rectangle(); bg.SetResourceReference(Shape.FillProperty, "Panel");
        _pill.Children.Add(bg);
        _pill.Children.Add(_fill);
        var outline = new Border { BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(20) };
        outline.SetResourceReference(Border.BorderBrushProperty, "Muted");
        _pill.Children.Add(outline);
        _badge.Child = _badgeText;
        var row = new DockPanel { Margin = new Thickness(8, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, LastChildFill = true };
        DockPanel.SetDock(_badge, Dock.Left); DockPanel.SetDock(_time, Dock.Left);
        row.Children.Add(_badge); row.Children.Add(_time); row.Children.Add(_name);
        _pill.Children.Add(row);
        _pill.SizeChanged += (_, e) =>
        {
            _pill.Clip = new RectangleGeometry(new Rect(e.NewSize), e.NewSize.Height / 2, e.NewSize.Height / 2);
            _fill.Width = e.NewSize.Width * _fraction;
        };

        // Today's progress.
        var day = new StackPanel { Margin = new Thickness(14, 0, 14, 4) };
        var labels = new Grid();
        _dayLeft.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _dayRight.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        labels.Children.Add(_dayLeft); labels.Children.Add(_dayRight);
        day.Children.Add(labels);
        var track = new Grid { Height = 5, Margin = new Thickness(0, 5, 0, 0) };
        var back = new Border { CornerRadius = new CornerRadius(3) }; back.SetResourceReference(Border.BackgroundProperty, "Panel2");
        _dayFill.SetResourceReference(Border.BackgroundProperty, "FocusBrush");
        track.Children.Add(back); track.Children.Add(_dayFill);
        track.SizeChanged += (_, e) => UpdateProgress(e.NewSize.Width);
        day.Children.Add(track);

        // What it lists, and the pin.
        var head = new DockPanel { Margin = new Thickness(14, 10, 10, 4) };
        _pin.SetResourceReference(StyleProperty, "IconButton");
        _pin.Click += (_, _) => { if (_edge.Length > 0) return; Topmost = !Topmost; _settings.TaskWindowOnTop = Topmost; _save(); PaintPin(); };
        PaintPin();
        DockPanel.SetDock(_pin, Dock.Right);
        head.Children.Add(_pin);
        _dockBtn.SetResourceReference(StyleProperty, "IconButton");
        _dockBtn.Click += (_, _) => DockMenu();
        DockPanel.SetDock(_dockBtn, Dock.Right);
        head.Children.Add(_dockBtn);
        PaintDock();
        var picker = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left };
        var chev = new TextBlock { Text = " ⌄", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, -4, 0, 0) };
        chev.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        picker.Children.Add(_viewText); picker.Children.Add(chev);
        picker.MouseLeftButtonUp += (_, _) => ViewMenu(picker);
        head.Children.Add(picker);

        _list.ItemTemplate = RowTemplate();
        _list.Margin = new Thickness(8, 0, 8, 8);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var listPanel = new StackPanel();
        listPanel.Children.Add(_list); listPanel.Children.Add(_empty);
        var scroll = new ScrollViewer { Content = listPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var root = new DockPanel();
        DockPanel.SetDock(_pill, Dock.Top); DockPanel.SetDock(day, Dock.Top); DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(_pill); root.Children.Add(day); root.Children.Add(head);
        // Working Memory below the tasks; the splitter sets how much room each gets, as a share of the window, so both stretch with it.
        double share = _settings.WorkingMemoryHeight is > 0.05 and < 0.95 ? _settings.WorkingMemoryHeight : 0.3;
        var body = new Grid();
        var memRow = new RowDefinition { Height = new GridLength(share, GridUnitType.Star), MinHeight = 70 };
        var listRow = new RowDefinition { Height = new GridLength(1 - share, GridUnitType.Star), MinHeight = 40 };
        body.RowDefinitions.Add(listRow);
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        body.RowDefinitions.Add(memRow);
        var splitter = new GridSplitter { Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, Cursor = Cursors.SizeNS, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        splitter.DragCompleted += (_, _) =>
        {
            double l = listRow.ActualHeight, m = memRow.ActualHeight;
            if (l + m > 0) { _settings.WorkingMemoryHeight = Math.Round(m / (l + m), 3); _save(); }
            listRow.Height = new GridLength(l, GridUnitType.Star); memRow.Height = new GridLength(m, GridUnitType.Star);
        };
        var memory = new WorkingMemoryBox { Margin = new Thickness(8, 0, 8, 8) };
        Grid.SetRow(scroll, 0); Grid.SetRow(splitter, 1); Grid.SetRow(memory, 2);
        body.Children.Add(scroll); body.Children.Add(splitter); body.Children.Add(memory);
        root.Children.Add(body);
        Content = root;

        Closing += (_, _) =>
        {
            if (_edge.Length > 0) { _settings.TaskWindowWidth = ActualWidth; RemoveBar(); }
            else if (WindowState == WindowState.Normal) { _settings.TaskWindowLeft = Left; _settings.TaskWindowTop = Top; _settings.TaskWindowWidth = Width; _settings.TaskWindowHeight = Height; }
            _save();
        };
        // The strip always goes back to Windows when the window closes or Focus quits.
        Closed += (_, _) => RemoveBar();
        if (Application.Current != null) Application.Current.Exit += (_, _) => RemoveBar();
        // Docked last time: dock again (on the screen the window opens on).
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            if (_settings.TaskWindowDock is "left" or "right") Dispatcher.BeginInvoke(new Action(() => DockTo(_settings.TaskWindowDock)), DispatcherPriority.Loaded);
        };
        // Minimized, the strip is given back; restored, it's taken again. Docked windows don't maximize.
        StateChanged += (_, _) =>
        {
            if (_edge.Length == 0) return;
            if (WindowState == WindowState.Maximized) { WindowState = WindowState.Normal; return; }
            if (WindowState == WindowState.Minimized) { RemoveBar(); return; }
            Dispatcher.BeginInvoke(new Action(() => DockTo(_edge)), DispatcherPriority.Loaded);
        };
        // Docked and made wider or narrower (drag its inner edge): the reserved strip follows.
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); if (_edge.Length > 0 && _registered) SetBarPos(); };
        SizeChanged += (_, _) => { if (_edge.Length > 0 && !_placing) { _resizeTimer.Stop(); _resizeTimer.Start(); } };
        Refresh();
    }

    private static bool OnScreen(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 50 && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
        top > SystemParameters.VirtualScreenTop - 20 && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;

    private void PaintPin()
    {
        _pin.Content = Topmost ? "" : "";
        _pin.ToolTip = _edge.Length > 0 ? "Docked: always visible" : Topmost ? "On top of other windows (click to let it go behind)" : "Keep on top of other windows";
        _pin.Opacity = Topmost ? 1 : 0.6;
    }

    private static readonly (string Key, string Name)[] Views = { ("session", "Session"), ("today", "Today"), ("tomorrow", "Tomorrow"), ("list", "List"), ("undated", "Undated") };

    private void ViewMenu(FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        foreach (var (key, label) in Views)
        {
            string name = key == "tomorrow" ? NextDay.Name : label;
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = _settings.TaskWindowView == key };
            item.Click += (_, _) => { _settings.TaskWindowView = key; _save(); _shown = ""; Refresh(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>The thermometer's bar: called with the same values the floating timer gets.</summary>
    public void UpdateTimer(string time, string name, Brush accent, double fractionLeft, int focusNumber)
    {
        _time.Text = time;
        _name.Text = name;
        bool focus = focusNumber > 0;
        _badge.Visibility = focus ? Visibility.Visible : Visibility.Collapsed;
        _badge.BorderBrush = accent;
        _badgeText.Text = focus ? SessionLook.Roman(focusNumber) : "";
        _fill.Fill = accent;
        _fraction = Math.Clamp(fractionLeft, 0, 1);
        _fill.Width = _pill.ActualWidth * _fraction;
        _fill.Height = double.NaN;
    }

    private void UpdateProgress(double width)
    {
        var (done, total, left) = _progress();
        _dayLeft.Text = total == 0 ? "Today" : $"{done} of {total} tasks";
        _dayRight.Text = left;
        _dayFill.Width = total == 0 ? 0 : width * Math.Clamp((double)done / total, 0, 1);
    }

    /// <summary>Redraws the list (only when it changed, so clicks aren't swallowed) and today's progress.</summary>
    public void Refresh()
    {
        string view = _settings.TaskWindowView;
        // Default shows just the list's name; otherwise its view, as set in Tasks ("List · By project").
        string title = view switch { "session" => "Session", "list" => "List", "undated" => "Undated", "tomorrow" => NextDay.Name, _ => "Today" };
        string mode = view == "session" ? "default" : _listMode(view);
        if (mode != "default") title += mode == "ordered" ? " · Ordered" : " · By project";
        _viewText.Text = title;
        var tasks = _tasks(view);
        // Narrow: no project headings, so the names keep their room.
        if (ActualWidth > 0 && ActualWidth < 280) tasks = tasks.Select(t => new OverlayTask { Source = t.Source, Kind = t.Kind, Id = t.Id, Key = t.Key, Text = t.Text, IsHigh = t.IsHigh, Done = t.Done }).ToList();
        string now = view + mode + (ActualWidth < 280 ? "n" : "w") + string.Join("|", tasks.Select(t => t.Key + (t.Done ? "1" : "0") + t.Text + t.Group));
        if (now != _shown)
        {
            _shown = now;
            _list.ItemsSource = TaskGroups.Source(tasks, _list);
        }
        _empty.Text = view switch
        {
            "session" => "No tasks for this session (or nothing running).",
            "undated" => "Every task has a date.",
            "list" => "No tasks.",
            "tomorrow" => "Nothing planned or due tomorrow.",
            _ => "Nothing picked, due or overdue today."
        };
        _empty.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress(_dayFill.Parent is FrameworkElement fe ? fe.ActualWidth : 0);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if ((info.PreviousSize.Width < 280) != (info.NewSize.Width < 280)) { _shown = ""; Refresh(); }
    }

    /// <summary>A row: a round check, the task, and (Today, Ordered) a grip to drag it into order.</summary>
    private DataTemplate RowTemplate()
    {
        var template = new DataTemplate(typeof(OverlayTask));
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        dock.SetValue(MarginProperty, new Thickness(4, 5, 4, 5)); // a little roomier than the floating timer
        dock.SetValue(BackgroundProperty, Brushes.Transparent);
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetValue(DockPanel.DockProperty, Dock.Left);
        check.SetResourceReference(StyleProperty, "TaskCheck");
        check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(OverlayTask.Done)) { Mode = BindingMode.TwoWay });
        check.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
        check.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, _) =>
        {
            if ((s as FrameworkElement)?.DataContext is OverlayTask t) { _toggled(t); _shown = ""; Dispatcher.BeginInvoke(new Action(Refresh)); }
        }));
        dock.AppendChild(check);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(OverlayTask.Text)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.FontSizeProperty, 14.0);
        text.SetValue(TextBlock.LineHeightProperty, 19.0);
        dock.AppendChild(text);
        dock.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) => { _down = e.GetPosition(this); _pressed = (s as FrameworkElement)?.DataContext as OverlayTask; }));
        dock.AddHandler(MouseMoveEvent, new MouseEventHandler((s, e) =>
        {
            if (_pressed == null || e.LeftButton != MouseButtonState.Pressed || !CanReorder) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.Y - _down.Y) < 6) return;
            var task = _pressed; _pressed = null;
            DragDrop.DoDragDrop((DependencyObject)s, new DataObject(typeof(OverlayTask), task), DragDropEffects.Move);
        }));
        dock.SetValue(AllowDropProperty, true);
        dock.AddHandler(DropEvent, new DragEventHandler((s, e) =>
        {
            if (e.Data.GetData(typeof(OverlayTask)) is not OverlayTask moving || (s as FrameworkElement)?.DataContext is not OverlayTask target || moving.Key == target.Key) return;
            string list = _settings.TaskWindowView;
            var ids = _tasks(list).Select(t => t.Id).ToList();
            ids.Remove(moving.Id);
            int at = ids.IndexOf(target.Id);
            ids.Insert(at < 0 ? ids.Count : at, moving.Id);
            _reordered(list, ids);
            _shown = ""; Refresh();
        }));
        template.VisualTree = dock;
        return template;
    }

    // ---------- Docking: an app bar, like the taskbar ----------
    // Docked to the left or right edge of a screen, the window reserves that strip: maximized and snapped windows use the
    // space that's left instead of covering it. The same Windows feature the taskbar uses (SHAppBarMessage).

    private readonly Button _dockBtn = new() { Padding = new Thickness(8, 4, 8, 4), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Margin = new Thickness(0, 0, 2, 0) };
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private string _edge = "";
    private bool _registered, _placing;
    private int _callback;
    private IntPtr _hwnd;

    private void PaintDock()
    {
        _dockBtn.Content = _edge == "left" ? "" : _edge == "right" ? "" : "";
        _dockBtn.ToolTip = _edge.Length > 0 ? $"Docked to the {_edge} of the screen: click to move or undock" : "Dock to the side of the screen";
        _dockBtn.Opacity = _edge.Length > 0 ? 1 : 0.6;
    }

    private void DockMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _dockBtn, Placement = PlacementMode.Bottom };
        void Add(string text, string edge, bool on)
        {
            var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = on };
            item.Click += (_, _) =>
            {
                if (edge.Length == 0) { if (_edge.Length > 0) Undock(); return; }
                if (_edge.Length > 0 && _edge != edge) RemoveBar();
                DockTo(edge);
            };
            menu.Items.Add(item);
        }
        Add("Dock to the left", "left", _edge == "left");
        Add("Dock to the right", "right", _edge == "right");
        menu.Items.Add(new Separator());
        Add("Undock", "", _edge.Length == 0);
        menu.IsOpen = true;
    }

    private void DockTo(string edge)
    {
        try
        {
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            if (WindowState == WindowState.Minimized) return; // docks again when restored
            _edge = edge;
            _settings.TaskWindowDock = edge;
            _save();
            Topmost = true; // a docked bar stays visible
            if (!_registered)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (_callback == 0) _callback = RegisterWindowMessage("FocusTaskWindowAppBar");
                var abd = NewData(hwnd);
                abd.uCallbackMessage = _callback;
                SHAppBarMessage(ABM_NEW, ref abd);
                _registered = true;
            }
            SetBarPos();
        }
        catch (Exception ex) { Log.Error("Task window dock", ex); }
        PaintDock(); PaintPin();
    }

    private void Undock()
    {
        string was = _edge;
        RemoveBar();
        _edge = "";
        _settings.TaskWindowDock = "";
        _save();
        Topmost = _settings.TaskWindowOnTop;
        // Back to a normal floating window, a little in from the edge.
        Height = Math.Max(400, Math.Min(_settings.TaskWindowHeight, SystemParameters.WorkArea.Height - 80));
        Top = Top + 40;
        Left += was == "right" ? -40 : 40;
        PaintDock(); PaintPin();
    }

    /// <summary>Gives the reserved strip back to Windows.</summary>
    private void RemoveBar()
    {
        if (!_registered) return;
        try { var abd = NewData(_hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle); SHAppBarMessage(ABM_REMOVE, ref abd); }
        catch (Exception ex) { Log.Error("Task window undock", ex); }
        _registered = false;
    }

    /// <summary>Asks Windows for the strip at the edge of this window's screen, takes it, and moves the window into it.
    /// Works in the screen's own pixels, so screens with different scaling get the right width.</summary>
    private void SetBarPos()
    {
        if (!_registered) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return;
        var screen = info.rcMonitor;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int width = (int)Math.Round(Math.Clamp(ActualWidth > 0 ? ActualWidth : _settings.TaskWindowWidth, 220, 700) * scale);
        var abd = NewData(hwnd);
        abd.uEdge = _edge == "left" ? ABE_LEFT : ABE_RIGHT;
        abd.rc = _edge == "left" ? new RECT { left = screen.left, top = screen.top, right = screen.left + width, bottom = screen.bottom }
                                 : new RECT { left = screen.right - width, top = screen.top, right = screen.right, bottom = screen.bottom };
        SHAppBarMessage(ABM_QUERYPOS, ref abd);
        if (_edge == "left") abd.rc.right = abd.rc.left + width; else abd.rc.left = abd.rc.right - width;
        SHAppBarMessage(ABM_SETPOS, ref abd);
        _placing = true;
        SetWindowPos(hwnd, IntPtr.Zero, abd.rc.left, abd.rc.top, abd.rc.right - abd.rc.left, abd.rc.bottom - abd.rc.top, 0x0004 | 0x0010); // keep z-order, don't activate
        Dispatcher.BeginInvoke(new Action(() => _placing = false), DispatcherPriority.Background);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // The taskbar or another bar moved: take our place again.
        if (_registered && _callback != 0 && msg == _callback && wParam.ToInt64() == ABN_POSCHANGED)
            Dispatcher.BeginInvoke(new Action(SetBarPos), DispatcherPriority.Background);
        return IntPtr.Zero;
    }

    private static APPBARDATA NewData(IntPtr hwnd) => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };

    private const int ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABE_LEFT = 0, ABE_RIGHT = 2, ABN_POSCHANGED = 1;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public int uCallbackMessage; public int uEdge; public RECT rc; public IntPtr lParam; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string msg);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, int flags);

    private Point _down;
    private OverlayTask? _pressed;
    private bool CanReorder => _settings.TaskWindowView != "session" && _listMode(_settings.TaskWindowView) == "ordered";
}
