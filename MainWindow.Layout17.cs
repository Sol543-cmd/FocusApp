using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Documents;

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

    private System.Windows.Controls.Primitives.Popup? _morePopup;

    /// <summary>The ⋯ menu: a small themed panel under the button, one row per page with its icon and shortcut.</summary>
    private void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_morePopup?.IsOpen == true) { _morePopup.IsOpen = false; return; }
        var list = new StackPanel();
        void Add(string glyph, string text, RadioButton page, string keys = "")
        {
            var row = new DockPanel();
            var key = new TextBlock { Text = keys, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            key.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            DockPanel.SetDock(key, Dock.Right);
            row.Children.Add(key);
            var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Width = 28, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.ForegroundProperty, page.IsChecked == true ? "FocusBrush" : "Muted");
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, FontWeight = page.IsChecked == true ? FontWeights.SemiBold : FontWeights.Normal });
            var item = new Border { Child = row, Padding = new Thickness(12, 8, 14, 8), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            item.MouseEnter += (_, _) => item.SetResourceReference(Border.BackgroundProperty, "Panel2");
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, _) => { if (_morePopup != null) _morePopup.IsOpen = false; page.IsChecked = true; };
            list.Children.Add(item);
        }
        if (_settings.On("home")) Add("\uE80F", "Home", NavHome);
        if (StatsOn) Add("\uE9D2", "Stats", NavStats);
        Add("\uE72E", "Block lists", NavLists);
        var line = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        list.Children.Add(line);
        Add("\uE713", "Settings", NavSettings, "Ctrl+,");
        var card = new Border { Child = list, Width = 230, Padding = new Thickness(6), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(10, 2, 10, 14),
                                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black } };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.SetResourceReference(TextElement.ForegroundProperty, "Text");
        _morePopup = new System.Windows.Controls.Primitives.Popup
        {
            Child = card, PlacementTarget = MoreMenuBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            HorizontalOffset = MoreMenuBtn.ActualWidth - 250, StaysOpen = false, AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade
        };
        _morePopup.IsOpen = true;
    }

    // ---------- Focus's own title bar: minimize, maximize and close ----------

    private void WinMin_Click(object sender, MouseButtonEventArgs e) => WindowState = WindowState.Minimized;

    private void WinMax_Click(object sender, MouseButtonEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void WinClose_Click(object sender, MouseButtonEventArgs e) => Close();

    /// <summary>Maximized, Windows pushes the window's edges just past the screen: a margin keeps everything in view.</summary>
    private void UpdateTitleBarForState()
    {
        if (WindowState == WindowState.Maximized)
        {
            var t = SystemParameters.WindowResizeBorderThickness;
            RootGrid.Margin = new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4);
            WinMaxGlyph.Text = "\uE923";
        }
        else
        {
            RootGrid.Margin = new Thickness(0);
            WinMaxGlyph.Text = "\uE922";
        }
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
