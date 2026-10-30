using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FocusApp;

/// <summary>
/// Personal tasks: the Personal set (see TaskStores), a list completely separate from your work tasks (its own
/// projects, finished list and files, synced like the others). The Personal page is the full Tasks page showing
/// this set; this class is the short way in for the rest of Focus (Pomodoro's list, the floating timer, quick add).
/// </summary>
public static class PersonalTasks
{
    private static TaskStore Set => TaskStores.Personal;
    public static ObservableCollection<BinTask> All => Set.Bin;
    public static List<ResolvedTask> Finished => Set.Resolved;
    public static List<Project> ProjectList => Set.Projects;
    public static event Action? Changed;

    public const string TasksFile = "personal-tasks.json", FinishedFile = "personal-finished.json", ProjectsFile = "personal-projects.json";

    /// <summary>#Project and /Section in personal tasks refer to Personal's projects.</summary>
    public static readonly TaskParser.IProjectBook Book = new PersonalBook();

    private sealed class PersonalBook : TaskParser.IProjectBook
    {
        public Project? Find(string? id) { using (TaskStores.Use(TaskStores.Personal)) return Projects.Find(id); }
        public Project? Match(string text) { using (TaskStores.Use(TaskStores.Personal)) return Projects.Match(text); }
        public Project Add(string name) { using (TaskStores.Use(TaskStores.Personal)) return Projects.Add(name); }
    }

    public static void Load()
    {
        using (TaskStores.Use(TaskStores.Personal))
        {
            TaskBin.Load();
            ResolvedTasks.Load();
            Projects.Load();
        }
    }

    /// <summary>Saves the Personal set (merging with OneDrive's copies, so the other computer's and the phone's changes are kept).</summary>
    public static void Save()
    {
        using (TaskStores.Use(TaskStores.Personal))
        {
            ResolvedTasks.Save();
            Projects.Save();
            TaskBin.Save();
        }
        Changed?.Invoke();
    }

    public static void Reload() => Save();

    public static bool WasFinished(BinTask t)
    {
        using (TaskStores.Use(TaskStores.Personal)) return ResolvedTasks.WasFinished(t);
    }

    /// <summary>Adds a task typed like the work list's: #Project, /Section, @label, !, dates and repeats.</summary>
    public static BinTask? Add(string input, string from = "Personal")
    {
        BinTask task;
        using (TaskStores.Use(TaskStores.Personal))
        {
            task = new BinTask { From = from };
            TaskParser.Apply(task, input, createProjects: true); // #Project means a Personal project here
            if (task.Text.Length == 0) return null;
            TaskBin.All.Add(task);
        }
        Save();
        return task;
    }

    public static Project AddProject(string name)
    {
        using (TaskStores.Use(TaskStores.Personal)) return Projects.Add(name);
    }

    /// <summary>Ticks a task off: it moves to Personal's finished list (a repeating task comes back on its next date).</summary>
    public static void Finish(BinTask task)
    {
        if (!All.Contains(task)) return;
        using (TaskStores.Use(TaskStores.Personal))
        {
            ResolvedTasks.All.Add(new ResolvedTask
            {
                Text = task.Text, From = task.From, Project = task.Project, CreatedAt = task.CreatedAt, ResolvedAt = DateTime.Now,
                Tags = task.Tags.ToList()
            });
            TaskBin.All.Remove(task);
            if (task.Repeat.Length > 0 && RepeatNext(task) is DateTime next)
            {
                var again = SyncMerge.Copy(new[] { task })[0];
                again.Id = Guid.NewGuid().ToString("N");
                again.CreatedAt = null;
                again.Due = next;
                again.PlannedFor = "";
                TaskBin.All.Add(again);
            }
        }
        Save();
    }

    public static void Finish(string id)
    {
        if (All.FirstOrDefault(t => t.Id == id) is BinTask t) Finish(t);
    }

    public static void Delete(BinTask task)
    {
        All.Remove(task);
        Save();
    }

    private static DateTime? RepeatNext(BinTask task)
    {
        try { return task.RepeatRule?.Next(task.Due ?? DateTime.Today); }
        catch { return null; }
    }

    // ---------- Views (for Pomodoro's list and the floating timer) ----------

    public static bool IsToday(BinTask t) => t.IsPlannedToday || t.IsDueToday || t.IsOverdue;

    /// <summary>From today to Sunday.</summary>
    private static bool IsThisWeek(DateTime? day)
    {
        if (day is not DateTime d) return false;
        var end = DateTime.Today.AddDays(6 - ((int)DateTime.Today.DayOfWeek + 6) % 7);
        return d.Date >= DateTime.Today && d.Date <= end;
    }

    public static IEnumerable<BinTask> View(string view, string? projectId = null)
    {
        var all = All.Where(t => !t.IsSnoozed);
        if (!string.IsNullOrEmpty(projectId)) all = all.Where(t => t.Project == projectId);
        all = view switch
        {
            "today" => all.Where(IsToday),
            "week" => all.Where(t => IsToday(t) || IsThisWeek(t.PlannedDate) || IsThisWeek(t.Due)),
            "undated" => all.Where(t => t.IsUndated),
            "upcoming" => all.Where(t => t.Due != null && t.Due.Value.Date > DateTime.Today),
            _ => all
        };
        return all.OrderBy(TaskBase.Rank).ThenBy(t => t.Due ?? DateTime.MaxValue).ThenBy(t => t.CreatedAt ?? DateTime.MaxValue);
    }

    public static int Count(string view, string? projectId = null) => View(view, projectId).Count();
}
