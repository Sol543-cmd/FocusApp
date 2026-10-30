using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>
/// Middle-button scrolling in every window, like a web browser: hold the wheel down and move the mouse
/// (the further from where you pressed, the faster), and let go to stop. A quick click of the wheel
/// keeps it scrolling as you move until you click again or press Esc.
/// </summary>
public static class AutoScroll
{
    private static ScrollViewer? _viewer;
    private static Window? _window;
    private static System.Drawing.Point _anchor;
    private static DateTime _started;
    private static bool _moved, _sticky;
    private static Popup? _marker;
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(15) };

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnDown), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(OnUp), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) => { if (e.Key == Key.Escape && _viewer != null) { Stop(); e.Handled = true; } }), true);
        Timer.Tick += (_, _) => Step();
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewer != null) { Stop(); e.Handled = true; return; } // any click ends scrolling
        if (e.ChangedButton != MouseButton.Middle || sender is not Window window) return;
        var viewer = FindViewer(e.OriginalSource as DependencyObject);
        if (viewer == null || (viewer.ScrollableHeight <= 0 && viewer.ScrollableWidth <= 0)) return;
        _viewer = viewer;
        _window = window;
        _anchor = System.Windows.Forms.Control.MousePosition;
        _started = DateTime.Now;
        _moved = _sticky = false;
        window.Deactivated += OnDeactivated;
        ShowMarker(window, viewer);
        Mouse.Capture(window, CaptureMode.SubTree);
        Timer.Start();
        e.Handled = true;
    }

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewer == null || e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        // A quick click without moving: keep scrolling until the next click. Otherwise, letting go stops.
        if (!_moved && DateTime.Now - _started < TimeSpan.FromMilliseconds(350)) { _sticky = true; Mouse.Capture(null); return; }
        Stop();
    }

    private static void OnDeactivated(object? sender, EventArgs e) => Stop();

    private static void Step()
    {
        if (_viewer == null) return;
        if (!_sticky && Mouse.MiddleButton != MouseButtonState.Pressed && DateTime.Now - _started > TimeSpan.FromMilliseconds(400)) { Stop(); return; }
        var now = System.Windows.Forms.Control.MousePosition;
        double dx = now.X - _anchor.X, dy = now.Y - _anchor.Y;
        if (Math.Abs(dx) > 6 || Math.Abs(dy) > 6) _moved = true;
        static double Speed(double d) => Math.Abs(d) <= 8 ? 0 : Math.Sign(d) * Math.Pow(Math.Abs(d) - 8, 1.25) * 0.09;
        double vy = Speed(dy), vx = Speed(dx);
        if (vy != 0 && _viewer.ScrollableHeight > 0) _viewer.ScrollToVerticalOffset(Math.Clamp(_viewer.VerticalOffset + vy, 0, _viewer.ScrollableHeight));
        if (vx != 0 && _viewer.ScrollableWidth > 0) _viewer.ScrollToHorizontalOffset(Math.Clamp(_viewer.HorizontalOffset + vx, 0, _viewer.ScrollableWidth));
    }

    private static void Stop()
    {
        Timer.Stop();
        if (_window != null) _window.Deactivated -= OnDeactivated;
        if (_marker != null) _marker.IsOpen = false;
        if (Mouse.Captured != null && _window != null && Mouse.Captured == _window) Mouse.Capture(null);
        _viewer = null;
        _window = null;
        _sticky = false;
    }

    /// <summary>A small round marker where you pressed, showing which way it can scroll.</summary>
    private static void ShowMarker(Window window, ScrollViewer viewer)
    {
        string arrows = viewer.ScrollableHeight > 0 && viewer.ScrollableWidth > 0 ? "✥" : viewer.ScrollableHeight > 0 ? "⇕" : "⇔";
        var text = new TextBlock { Text = arrows, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var dot = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1), Child = text, Opacity = 0.92 };
        dot.SetResourceReference(Border.BackgroundProperty, "Panel");
        dot.SetResourceReference(Border.BorderBrushProperty, "Muted");
        _marker ??= new Popup { AllowsTransparency = true, Placement = PlacementMode.MousePoint, StaysOpen = true, IsHitTestVisible = false };
        _marker.Child = dot;
        _marker.PlacementTarget = window;
        _marker.HorizontalOffset = -15;
        _marker.VerticalOffset = -15;
        _marker.IsOpen = false;
        _marker.IsOpen = true;
    }

    private static ScrollViewer? FindViewer(DependencyObject? from)
    {
        // Walk up from what was clicked to the nearest scroll area that can actually scroll.
        while (from != null)
        {
            if (from is ScrollViewer sv && (sv.ScrollableHeight > 0 || sv.ScrollableWidth > 0)) return sv;
            from = from is Visual || from is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(from) : LogicalTreeHelper.GetParent(from);
        }
        return null;
    }
}
