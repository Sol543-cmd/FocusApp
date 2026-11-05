using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FocusApp;

/// <summary>Click a calendar meeting shown on top of your day (in any day view) to add it to your day.</summary>
public partial class MainWindow
{
    private void WireMeetingOverlays()
    {
        foreach (var t in new[] { Timeline, DashDay, CalDay, HomeDay })
            if (t != null) t.EventClicked += OfferAddMeeting;
    }

    private void OfferAddMeeting(CalendarEvent ev, FrameworkElement card)
    {
        var day = ev.Start.Date;
        bool running = _mode == RunMode.DayPlan && _runner.IsRunning && day == DateTime.Today;
        var menu = new ContextMenu { PlacementTarget = card, Placement = PlacementMode.Bottom };
        var item = new MenuItem { Header = "Add to my day" };
        if (!running && day != _planDate)
        {
            item.IsEnabled = false;
            item.Header = "Open that day's plan to add it";
        }
        item.Click += (_, _) =>
        {
            try
            {
                var raw = ev.Start.TimeOfDay;
                if (running) AdjustRunningDayForCalendar(only: (t, s) => t == ev.Title && s == raw, cancelledToo: false);
                else FitMeetingsIntoPlan(new List<(TimeSpan Start, string Title)> { (raw, ev.Title) }, new List<PlanBlock>());
            }
            catch (Exception ex) { Log.Error("Add meeting to day", ex); }
        };
        menu.Items.Add(item);
        menu.IsOpen = true;
    }
}
