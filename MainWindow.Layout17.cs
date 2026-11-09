using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace FocusApp;

/// <summary>
/// 1.7.17 layout: the sidebar holds Day plan, Calendar, Tasks, Personal, Training and Pomodoro; the Focus logo opens the
/// Dashboard; Settings, Block lists and Stats (and Home, when it's on) are in the ⋯ menu at the top right beside the
/// notification bell; the Tasks page's side bar can be collapsed or pinned; Pomodoro took in Quick focus.
/// </summary>
public partial class MainWindow
{
    private bool StatsOn => _settings.On("stats") || _settings.On("workload") || _settings.On("statsGame");

    private void Logo_Click(object sender, MouseButtonEventArgs e) => NavDashboard.IsChecked = true;

    private void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreMenuBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string text, RadioButton page, string? keys = null)
        {
            var item = new MenuItem { Header = text, InputGestureText = keys ?? "", IsChecked = page.IsChecked == true };
            item.Click += (_, _) => page.IsChecked = true;
            menu.Items.Add(item);
        }
        if (_settings.On("home")) Add("Home", NavHome);
        if (StatsOn) Add("Stats", NavStats);
        Add("Block lists", NavLists);
        menu.Items.Add(new Separator());
        Add("Settings", NavSettings, "Ctrl+,");
        menu.IsOpen = true;
    }

    // ---------- Tasks: the side bar, pinned open or collapsed to a strip ----------

    private bool _tasksSideOpen;

    private void ApplyTasksSide()
    {
        bool pinned = _settings.TasksSidePinned;
        if (pinned)
        {
            TasksSideColumn.Width = new GridLength(270);
            Grid.SetColumn(TasksSide, 1); Grid.SetColumnSpan(TasksSide, 1);
            TasksSide.Width = double.NaN;
            TasksSide.HorizontalAlignment = HorizontalAlignment.Stretch;
            TasksSide.Margin = new Thickness(16, 0, 0, 0);
            TasksSide.Effect = null;
            Panel.SetZIndex(TasksSide, 0);
            TasksSide.Visibility = Visibility.Visible;
            TasksSideStrip.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Collapsed: the list takes the room; the bar opens over the list's right edge when you point at the strip.
            TasksSideColumn.Width = new GridLength(36);
            Grid.SetColumn(TasksSide, 0); Grid.SetColumnSpan(TasksSide, 2);
            TasksSide.Width = 270;
            TasksSide.HorizontalAlignment = HorizontalAlignment.Right;
            TasksSide.Margin = new Thickness(0);
            TasksSide.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black };
            Panel.SetZIndex(TasksSide, 20);
            TasksSide.Visibility = _tasksSideOpen ? Visibility.Visible : Visibility.Collapsed;
            TasksSideStrip.Visibility = Visibility.Visible;
        }
        TasksSidePinBtn.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
        TasksSideCollapseBtn.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TasksSideStrip_Open(object sender, MouseEventArgs e)
    {
        if (_settings.TasksSidePinned) return;
        _tasksSideOpen = true;
        ApplyTasksSide();
    }

    private void TasksSide_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_settings.TasksSidePinned || !_tasksSideOpen) return;
        // A menu or pop-up opened from the bar keeps it open.
        if (TasksSide.ContextMenu?.IsOpen == true || Mouse.Captured != null) return;
        _tasksSideOpen = false;
        ApplyTasksSide();
    }

    private void TasksSidePin_Click(object sender, RoutedEventArgs e)
    {
        _settings.TasksSidePinned = true;
        _tasksSideOpen = false;
        SaveSettingsQuietly();
        ApplyTasksSide();
    }

    private void TasksSideCollapse_Click(object sender, RoutedEventArgs e)
    {
        _settings.TasksSidePinned = false;
        _tasksSideOpen = false;
        SaveSettingsQuietly();
        ApplyTasksSide();
    }

    // ---------- Pomodoro (with Quick focus) ----------

    private void PomoForever_Changed(object sender, RoutedEventArgs e) => UpdatePomoSetup();

    /// <summary>"Keep going" hides the count; one session needs no break length.</summary>
    private void UpdatePomoSetup()
    {
        if (PomoCountBox == null || PomoBreakField == null) return;
        bool forever = PomoForeverCheck.IsChecked == true;
        PomoCountBox.IsEnabled = !forever;
        PomoBreakField.Visibility = forever || PomoCountBox.Value > 1 ? Visibility.Visible : Visibility.Collapsed;
        PomoStartBtn.Content = forever ? "Start Pomodoro" : PomoCountBox.Value == 1 ? "Start focus" : $"Start {PomoCountBox.Value} sessions";
    }
}
