using System;
using System.Collections.Generic;
using System.Linq;

namespace FocusApp;

/// <summary>
/// The game side of Stats: points, levels, streaks, records and badges. Everything is worked out
/// from your history (finished tasks and focus time), so it's the same on every computer and can't
/// drift out of step.
/// </summary>
public sealed class Progress
{
    public record Level(string Name, int From);
    public record Badge(string Icon, string Name, string Description, bool Earned, string ProgressText);

    /// <summary>Levels, like Todoist's karma levels.</summary>
    public static readonly Level[] Levels =
    {
        new("Starter", 0), new("Steady", 500), new("Focused", 1500), new("Pro", 4000),
        new("Expert", 9000), new("Master", 20000), new("Grandmaster", 45000), new("Zen", 90000)
    };

    // Points: 10 per task finished, 1 per 5 minutes of focus with blocking, 25 per day the goal was met.
    public const int PointsPerTask = 10, PointsPerGoalDay = 25;
    public const double PointsPerFocusMinute = 0.2;

    public int Points { get; private set; }
    public int PointsThisWeek { get; private set; }
    public int PointsToday { get; private set; }
    public Level CurrentLevel { get; private set; } = Levels[0];
    public Level? NextLevel { get; private set; }
    public double LevelProgress { get; private set; }

    public int CurrentStreak { get; private set; }
    public int LongestStreak { get; private set; }
    public int GoalDaysThisMonth { get; private set; }

    public int TotalTasks { get; private set; }
    public double TotalFocusHours { get; private set; }
    public (DateTime Day, int Count) BestTaskDay { get; private set; }
    public (DateTime Day, double Hours) BestFocusDay { get; private set; }
    public (DateTime WeekOf, int Count) BestWeek { get; private set; }
    public string BestWeekday { get; private set; } = "";
    public string PeakHour { get; private set; } = "";
    public List<(string Name, string Color, int Count)> TopProjects { get; private set; } = new();
    public double AverageTasksPerWorkday { get; private set; }
    public List<Badge> Badges { get; } = new();

    public static Progress Compute(IReadOnlyList<ResolvedTask> done, Dictionary<DateTime, double> focusByDay,
                                   int goalTasks, double goalHours, DateTime today, bool includeWeekends = true)
    {
        var p = new Progress();
        var tasksByDay = done.GroupBy(t => t.ResolvedAt.Date).ToDictionary(g => g.Key, g => g.Count());
        bool Met(DateTime d) => tasksByDay.GetValueOrDefault(d) >= goalTasks || focusByDay.GetValueOrDefault(d) >= goalHours * 60;
        static bool Weekend(DateTime d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        var allDays = tasksByDay.Keys.Concat(focusByDay.Keys).Distinct().OrderBy(d => d).ToList();
        int DayPoints(DateTime d) =>
            tasksByDay.GetValueOrDefault(d) * PointsPerTask +
            (int)(focusByDay.GetValueOrDefault(d) * PointsPerFocusMinute) +
            (Met(d) ? PointsPerGoalDay : 0);

        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        p.Points = allDays.Sum(DayPoints);
        p.PointsThisWeek = allDays.Where(d => d >= monday).Sum(DayPoints);
        p.PointsToday = DayPoints(today);

        p.CurrentLevel = Levels.Last(l => p.Points >= l.From);
        int li = Array.IndexOf(Levels, p.CurrentLevel);
        p.NextLevel = li + 1 < Levels.Length ? Levels[li + 1] : null;
        p.LevelProgress = p.NextLevel == null ? 1 : (p.Points - p.CurrentLevel.From) / (double)(p.NextLevel.From - p.CurrentLevel.From);

        // Streaks: weekends never break one (but count when the goal was met).
        int run = 0;
        if (allDays.Count > 0)
            for (var d = allDays[0]; d <= today; d = d.AddDays(1))
            {
                if (!includeWeekends && Weekend(d)) continue; // weekends left out: they neither add nor break
                if (Met(d)) run++;
                else if (!Weekend(d) && d != today) run = 0;
                p.LongestStreak = Math.Max(p.LongestStreak, run);
            }
        int current = Met(today) && (includeWeekends || !Weekend(today)) ? 1 : 0;
        for (var d = today.AddDays(-1); d >= today.AddDays(-1000); d = d.AddDays(-1))
        {
            if (!includeWeekends && Weekend(d)) continue;
            if (Met(d)) current++;
            else if (!Weekend(d)) break;
        }
        p.CurrentStreak = current;
        p.GoalDaysThisMonth = Enumerable.Range(0, today.Day).Select(i => new DateTime(today.Year, today.Month, 1).AddDays(i))
            .Count(d => (includeWeekends || !Weekend(d)) && Met(d));

        // Records and habits.
        p.TotalTasks = done.Count;
        p.TotalFocusHours = focusByDay.Values.Sum() / 60;
        if (tasksByDay.Count > 0) { var b = tasksByDay.MaxBy(kv => kv.Value); p.BestTaskDay = (b.Key, b.Value); }
        if (focusByDay.Count > 0) { var b = focusByDay.MaxBy(kv => kv.Value); p.BestFocusDay = (b.Key, b.Value / 60); }
        var weeks = done.GroupBy(t => t.ResolvedAt.Date.AddDays(-(((int)t.ResolvedAt.DayOfWeek + 6) % 7))).ToList();
        if (weeks.Count > 0) { var w = weeks.MaxBy(g => g.Count())!; p.BestWeek = (w.Key, w.Count()); }

        var workdays = allDays.Where(d => !Weekend(d)).ToList();
        if (workdays.Count > 0) p.AverageTasksPerWorkday = workdays.Average(d => tasksByDay.GetValueOrDefault(d));
        if (done.Count >= 10)
        {
            var dow = done.GroupBy(t => t.ResolvedAt.DayOfWeek).MaxBy(g => g.Count())!;
            p.BestWeekday = dow.Key + "s";
            var hour = done.GroupBy(t => t.ResolvedAt.Hour).MaxBy(g => g.Count())!.Key;
            p.PeakHour = DateTime.Today.AddHours(hour).ToString("h tt") + " to " + DateTime.Today.AddHours(hour + 1).ToString("h tt");
        }
        p.TopProjects = done.Where(t => t.ResolvedAt >= today.AddDays(-30) && t.Project.Length > 0)
            .GroupBy(t => t.Project)
            .Select(g => (Projects.Find(g.Key)?.Name ?? "Deleted project", Projects.Find(g.Key)?.Color ?? "#999999", g.Count()))
            .OrderByDescending(x => x.Item3).Take(5).ToList();

        // Badges.
        void Add(string icon, string name, string desc, int have, int need, string unit) =>
            p.Badges.Add(new Badge(icon, name, desc, have >= need, have >= need ? "Earned" : $"{Math.Min(have, need)} of {need} {unit}"));
        int hours = (int)p.TotalFocusHours;
        bool perfectWeek = Enumerable.Range(0, 104).Any(w =>
        {
            var start = monday.AddDays(-7 * (w + 1));
            return Enumerable.Range(0, 5).All(i => Met(start.AddDays(i)));
        }) || (today.DayOfWeek == DayOfWeek.Friday || Weekend(today)) && Enumerable.Range(0, 5).All(i => Met(monday.AddDays(i)));
        int early = done.Count(t => t.ResolvedAt.Hour < 8);
        int late = done.Count(t => t.ResolvedAt.Hour >= 20);
        int bigDay = p.BestTaskDay.Count;

        Add("✓", "First step", "Finish your first task", p.TotalTasks, 1, "task");
        Add("🔟", "Getting going", "Finish 10 tasks", p.TotalTasks, 10, "tasks");
        Add("💯", "Centurion", "Finish 100 tasks", p.TotalTasks, 100, "tasks");
        Add("🏔", "Mountain mover", "Finish 500 tasks", p.TotalTasks, 500, "tasks");
        Add("⏱", "Deep diver", "10 hours of focus with blocking", hours, 10, "hours");
        Add("🧠", "Flow state", "100 hours of focus with blocking", hours, 100, "hours");
        Add("🔥", "On a roll", "A 7-day streak", p.LongestStreak, 7, "days");
        Add("🌋", "Unstoppable", "A 30-day streak", p.LongestStreak, 30, "days");
        Add("⚡", "Big day", "Finish 10 tasks in one day", bigDay, 10, "in a day");
        Add("🌅", "Early bird", "Finish 5 tasks before 8 AM", early, 5, "tasks");
        Add("🌙", "Night owl", "Finish 5 tasks after 8 PM", late, 5, "tasks");
        p.Badges.Add(new Badge("🏆", "Perfect week", "Meet your goal every weekday of a week", perfectWeek, perfectWeek ? "Earned" : "Not yet"));
        return p;
    }
}
