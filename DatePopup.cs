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

        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (filling || calendar.SelectedDate is not DateTime day) return;
            box.Text = day.ToString("d");
            box.CaretIndex = box.Text.Length;
            popup.IsOpen = false;
            box.Focus();
            picked?.Invoke();
        };
        // WPF's calendar keeps hold of the mouse after a click, so the next click elsewhere would be swallowed.
        calendar.PreviewMouseUp += (_, _) => { if (Mouse.Captured is CalendarItem) Mouse.Capture(null); };
    }
}
