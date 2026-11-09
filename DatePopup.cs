using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FocusApp;

/// <summary>
/// A small pop-up calendar for date fields: clicking the field opens it (month arrows, today highlighted), picking
/// a day fills the field. Typing still works the same ("friday", "10/14"); Esc or Enter closes the calendar.
/// </summary>
public static class DatePopup
{
    /// <summary>Gives a date text box its pop-up calendar. <paramref name="picked"/> runs after a day is picked.</summary>
    public static void Attach(TextBox box, Action? picked = null)
    {
        var calendar = new Calendar { IsTodayHighlighted = true, DisplayMode = CalendarMode.Month };
        var frame = new Border { Child = calendar, CornerRadius = new CornerRadius(8), Padding = new Thickness(4), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "Panel");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        var popup = new Popup
        {
            Child = frame, PlacementTarget = box, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, VerticalOffset = 2
        };
        bool filling = false;

        void Open()
        {
            if (popup.IsOpen || !box.IsEnabled) return;
            var current = TaskParser.ParseDate(box.Text);
            filling = true;
            calendar.SelectedDate = current;
            calendar.DisplayDate = current ?? DateTime.Today;
            filling = false;
            popup.IsOpen = true;
        }

        box.PreviewMouseLeftButtonUp += (_, _) => Open();
        box.PreviewKeyDown += (_, e) =>
        {
            if (!popup.IsOpen) return;
            if (e.Key is Key.Escape or Key.Enter or Key.Tab) popup.IsOpen = false;
            if (e.Key == Key.Escape) e.Handled = true; // only closes the calendar, not the editor
        };
        box.LostKeyboardFocus += (_, e) => { if (e.NewFocus is not System.Windows.Media.Visual v || !frame.IsAncestorOf(v)) popup.IsOpen = false; };

        // WPF's calendar captures the mouse while a day is pressed. If the pop-up closes before the button comes up, the
        // capture stays with a hidden calendar and the whole window ignores the mouse until it's minimized: so the
        // capture is always let go before closing, and whenever the pop-up closes.
        void Release()
        {
            if (Mouse.Captured is DependencyObject held && (held == calendar || frame.IsAncestorOf(held) || held is CalendarItem or CalendarDayButton or Calendar))
                Mouse.Capture(null);
        }
        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (filling || calendar.SelectedDate is not DateTime day) return;
            box.Text = day.ToString("d");
            box.CaretIndex = box.Text.Length;
            Release();
            // Close once the click has finished (after the mouse button comes up), then hand the mouse back.
            box.Dispatcher.BeginInvoke(new Action(() =>
            {
                Release();
                popup.IsOpen = false;
                box.Focus();
                picked?.Invoke();
            }), System.Windows.Threading.DispatcherPriority.Input);
        };
        calendar.PreviewMouseUp += (_, _) => Release();
        calendar.GotMouseCapture += (_, _) =>
        {
            // A day button holding the mouse after it's been clicked: let go as soon as the button is up.
            if (Mouse.LeftButton == MouseButtonState.Released) Release();
        };
        popup.Closed += (_, _) => Release();
    }
}
