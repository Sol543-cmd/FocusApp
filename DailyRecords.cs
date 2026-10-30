using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FocusApp;

/// <summary>One block from a day that ran: what it was and how it ended (done, skipped or stopped).</summary>
public class BlockLogEntry
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTime Start { get; set; }
    public int Minutes { get; set; }
    public string Status { get; set; } = "done";
    public List<string> Tasks { get; set; } = new();
}

/// <summary>A meeting shared with the phone app (calendar-share.json).</summary>
public class SharedEvent
{
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool AllDay { get; set; }
    public bool Busy { get; set; } = true;
}

/// <summary>
/// The daily record: a Markdown file per day with focus time, tasks, the plan and meetings.
/// Written for Obsidian too (properties, checkboxes, links between days, project tags), and
/// everything under "## My notes" is yours: it's kept whenever the file is rewritten.
/// </summary>
public static class DailyRecords
{
    public const string NotesHeading = "## My notes";

    public static string FileName(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " Focus.md";

    /// <summary>Everything from "## My notes" down, from an existing file (or a fresh empty section).</summary>
    public static string ExtractNotes(string? existing)
    {
        if (string.IsNullOrEmpty(existing)) return NotesHeading + "\n\n";
        int i = existing.IndexOf("\n" + NotesHeading, StringComparison.Ordinal);
        if (i < 0 && existing.StartsWith(NotesHeading, StringComparison.Ordinal)) i = -1;
        else if (i < 0) return NotesHeading + "\n\n";
        return existing[(i + 1)..].TrimEnd() + "\n";
    }

    private static string Tag(string name) =>
        new string(name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    public record DayData(
        DateTime Day, double FocusMinutes, List<ResolvedTask> Finished, List<string> Added,
        List<BlockLogEntry> Log, List<(TimeSpan Start, PlanBlock Block)> Plan, List<SharedEvent> Meetings,
        bool GoalMet, int Streak, int Points, int GoalTasks, double GoalHours);

    public static string Build(DayData d, string? existing)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        int sessions = d.Log.Count(l => l.Kind == "Focus" && l.Status == "done");

        // Properties (Obsidian reads these; Dataview and Bases can chart them).
        sb.Append("---\n");
        sb.Append($"date: {d.Day.ToString("yyyy-MM-dd", inv)}\n");
        sb.Append($"focus_minutes: {Math.Round(d.FocusMinutes).ToString(inv)}\n");
        sb.Append($"focus_sessions: {sessions}\n");
        sb.Append($"tasks_done: {d.Finished.Count}\n");
        sb.Append($"goal_met: {(d.GoalMet ? "true" : "false")}\n");
        sb.Append($"streak: {d.Streak}\n");
        sb.Append($"points: {d.Points}\n");
        sb.Append("tags: [focus-daily]\n");
        sb.Append("---\n\n");

        sb.Append($"# {d.Day:dddd, MMMM d, yyyy}\n\n");
        sb.Append($"← [[{FileName(d.Day.AddDays(-1))[..^3]}]] · [[{FileName(d.Day.AddDays(1))[..^3]}]] →\n\n");

        sb.Append("## Summary\n\n");
        sb.Append($"- **Focus with blocking:** {Format.HoursMinutes(d.FocusMinutes)}\n");
        sb.Append($"- **Focus sessions completed:** {sessions}\n");
        sb.Append($"- **Tasks finished:** {d.Finished.Count}\n");
        sb.Append($"- **Daily goal** ({d.GoalTasks} tasks or {d.GoalHours:0.#} hours): {(d.GoalMet ? "met ✅" : "not met")}\n");
        sb.Append($"- **Streak:** {d.Streak} {(d.Streak == 1 ? "day" : "days")}\n");
        sb.Append($"- **Points earned:** {d.Points}\n\n");

        sb.Append("## Tasks finished\n\n");
        if (d.Finished.Count == 0) sb.Append("_None._\n");
        foreach (var t in d.Finished.OrderBy(t => t.ResolvedAt))
        {
            var extras = new List<string>();
            if (Projects.Find(t.Project) is Project p) extras.Add("#project/" + Tag(p.Name));
            extras.AddRange(t.Tags.Select(x => "#" + Tag(x)));
            if (t.Spent > 0) extras.Add(t.Spent == 1 ? "(1 session)" : $"({t.Spent} sessions)");
            sb.Append($"- [x] {t.Text}{(extras.Count > 0 ? " " + string.Join(" ", extras) : "")} ({t.ResolvedAt:t})\n");
        }
        sb.Append('\n');

        sb.Append("## Tasks added\n\n");
        if (d.Added.Count == 0) sb.Append("_None._\n");
        foreach (var a in d.Added) sb.Append($"- {a}\n");
        sb.Append('\n');

        sb.Append("## The plan\n\n");
        if (d.Plan.Count == 0 && d.Log.Count == 0) sb.Append("_No plan recorded for this day._\n");
        else if (d.Log.Count > 0)
        {
            foreach (var l in d.Log.OrderBy(l => l.Start))
            {
                string mark = l.Status switch { "done" => "[x]", "skipped" => "[-]", _ => "[ ]" };
                string status = l.Status switch { "done" => "", "skipped" => " (skipped)", _ => " (stopped early)" };
                sb.Append($"- {mark} {l.Start:t} {l.Name}, {l.Minutes} min{status}\n");
                foreach (var task in l.Tasks) sb.Append($"    - {task}\n");
            }
        }
        else
        {
            foreach (var (start, b) in d.Plan)
            {
                sb.Append($"- {DateTime.Today.Add(start):t} {b.Name}, {b.Minutes} min\n");
                foreach (var task in b.Tasks.Where(x => x.Text.Trim().Length > 0)) sb.Append($"    - [{(task.Done ? "x" : " ")}] {task.Text}\n");
            }
        }
        sb.Append('\n');

        sb.Append("## Meetings\n\n");
        if (d.Meetings.Count == 0) sb.Append("_None._\n");
        foreach (var m in d.Meetings.OrderBy(m => m.Start))
            sb.Append(m.AllDay ? $"- All day: {m.Title}\n" : $"- {m.Start:t} to {m.End:t}: {m.Title}\n");
        sb.Append('\n');

        sb.Append(ExtractNotes(existing));
        return sb.ToString();
    }
}
