using System;
using System.Collections.Generic;
using System.Linq;

namespace FocusApp;

/// <summary>Today's practice rounds and their times, for the day views and the Training page.</summary>
public partial class MainWindow
{
    private List<(TimeSpan At, string Name)> TodaysPracticeRounds()
    {
        var list = new List<(TimeSpan, string)>();
        try
        {
            if (PracticePausedToday()) return list;
            var cfg = Course.Config;
            // Once the day's lessons are done, their times drop off.
            if (cfg.On && Course.BookFor(DateTime.Today) != null && !Course.AllDoneToday()) list.AddRange(TrainingTimes().Select(t => (t, "Book lesson")));
            foreach (var (p, name) in new[] { ("words", "Greek words"), ("alphabet", "Greek alphabet"), ("concepts", "Greek concepts") })
                list.AddRange(SlotsFor(PerDayFor(p), p).Select(s => (s.TimeOfDay, name)));
            if (cfg.AllBooksAuto && cfg.Courses.Count > 0) list.AddRange((ParseTimes(string.Join(",", cfg.AllBooksTimes)) ?? new List<TimeSpan>()).Select(t => (t, "Books practice")));
            if (cfg.CardReviewOn) list.AddRange((ParseTimes(string.Join(",", cfg.CardReviewTimes)) ?? new List<TimeSpan>()).Select(t => (t, "Book flashcards")));
            if (cfg.TimelineOn && cfg.Courses.Count > 0) list.AddRange((ParseTimes(string.Join(",", cfg.TimelineTimes)) ?? new List<TimeSpan>()).Select(t => (t, "Timeline")));
        }
        catch (Exception ex) { Log.Error("Today's practice rounds", ex); }
        return list.OrderBy(x => x.Item1).ToList();
    }

    /// <summary>Redraws the Day plan and the Dashboard (after a display setting changes).</summary>
    private void RefreshDayViews()
    {
        try { RecalcTimes(); RefreshDashboard(full: true); }
        catch (Exception ex) { Log.Error("Refresh day views", ex); }
    }
}
