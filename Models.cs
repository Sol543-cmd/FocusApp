using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FocusApp;

public enum BlockKind { Focus, Break, Meeting }

public enum TaskPriority { Normal, High, Low }

/// <summary>
/// What every task has, wherever it lives (on a session or in the bin): text, an optional
/// priority, due date, estimate and tags. Nothing but the text is required.
/// </summary>
public abstract class TaskBase : INotifyPropertyChanged
{
    private string _text = "";
    private TaskPriority _priority;
    private DateTime? _due;
    private double _estimate;
    private List<string> _tags = new();
    private string _plannedFor = "";
    private int _spent;
    private string _repeat = "";
    private DateTime? _remindAt;
    private string _notes = "";
    private List<Subtask> _subtasks = new();
    private DateTime? _snoozedUntil;
    private string _status = "";
    private string _waitingOn = "";
    private string _project = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>When the task was created (tasks from before this was tracked get the day they were first seen).</summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>The project this task belongs to (a project Id), or empty.</summary>
    public string Project { get => _project; set { _project = value ?? ""; Changed(); } }

    /// <summary>The section within the task's project (a section Id), or empty.</summary>
    public string Section { get => _section; set { _section = value ?? ""; Changed(); } }
    private string _section = "";

    /// <summary>When the task was set to Waiting (for the "still waiting" reminder).</summary>
    public DateTime? WaitingSince { get; set; }

    [JsonIgnore] public int AgeDays => CreatedAt is DateTime c ? (int)(DateTime.Today - c.Date).TotalDays : 0;
    [JsonIgnore] public FocusApp.Project? ProjectInfo => Projects.Find(Project);
    [JsonIgnore] public string ProjectName => ProjectInfo?.Name ?? "";
    [JsonIgnore] public string ProjectColor => ProjectInfo?.Color ?? "Transparent";
    [JsonIgnore] public bool HasProject => ProjectInfo != null;
    [JsonIgnore] public string SectionName => ProjectInfo?.Sections.FirstOrDefault(x => x.Id == Section)?.Name ?? "";
    [JsonIgnore] public bool IsUndated => Due == null && PlannedFor.Length == 0;
    [JsonIgnore] public string CreatedText => CreatedAt is DateTime c ? $"Added {c:dddd, MMMM d, yyyy}" : "";

    // Screen-only state (never saved): selected with the keyboard or Ctrl/Shift+click, or being renamed in place.
    private bool _isSelected, _isEditing;
    [JsonIgnore] public bool IsSelected { get => _isSelected; set { _isSelected = value; Changed(); } }
    [JsonIgnore] public bool IsEditing { get => _isEditing; set { _isEditing = value; Changed(); } }

    /// <summary>Optional repeat, in plain words like "every friday" (see RepeatRule).</summary>
    public string Repeat { get => _repeat; set { _repeat = value ?? ""; Changed(); } }

    /// <summary>Optional reminder at a specific date and time.</summary>
    public DateTime? RemindAt { get => _remindAt; set { _remindAt = value; Changed(); } }

    public string Notes { get => _notes; set { _notes = value ?? ""; Changed(); } }

    /// <summary>Steps inside the task.</summary>
    public List<Subtask> Subtasks { get => _subtasks; set { _subtasks = value ?? new(); Changed(); } }

    /// <summary>Hidden from your task list until this day (it shows in Upcoming meanwhile).</summary>
    public DateTime? SnoozedUntil { get => _snoozedUntil; set { _snoozedUntil = value?.Date; Changed(); } }

    /// <summary>Where it sits on the board: "" (to do), "doing" or "waiting".</summary>
    public string Status { get => _status; set { _status = value ?? ""; Changed(); } }

    /// <summary>For "waiting" tasks: who or what you're waiting on.</summary>
    public string WaitingOn { get => _waitingOn; set { _waitingOn = value ?? ""; Changed(); } }

    [JsonIgnore] public bool IsSnoozed => SnoozedUntil != null && SnoozedUntil > DateTime.Today;
    [JsonIgnore] public RepeatRule? RepeatRule => FocusApp.RepeatRule.Parse(Repeat, Due ?? DateTime.Today);
    [JsonIgnore] public DateTime? PlannedDate =>
        DateTime.TryParseExact(PlannedFor, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public string Text { get => _text; set { _text = value ?? ""; Changed(); } }

    public TaskPriority Priority { get => _priority; set { _priority = value; Changed(); } }

    /// <summary>Optional. Only the date part is used.</summary>
    public DateTime? Due { get => _due; set { _due = value?.Date; Changed(); } }

    /// <summary>
    /// Optional estimate in focus sessions: 0 = none, 0.34 = quick (several fit in one session), 1 to 4 = sessions.
    /// </summary>
    public double Estimate { get => _estimate; set { _estimate = Math.Clamp(value, 0, 8); Changed(); } }

    public List<string> Tags { get => _tags; set { _tags = value ?? new(); Changed(); } }

    /// <summary>The day ("yyyy-MM-dd") you picked this for, from your task list. Empty if not picked.</summary>
    public string PlannedFor { get => _plannedFor; set { _plannedFor = value ?? ""; Changed(); } }

    /// <summary>How many focus sessions this task has been worked on.</summary>
    public int Spent { get => _spent; set { _spent = Math.Max(0, value); Changed(); } }

    // ---------- For showing on screen ----------

    [JsonIgnore] public bool IsOverdue => Due != null && Due < DateTime.Today;
    [JsonIgnore] public bool IsDueToday => Due != null && Due == DateTime.Today;
    [JsonIgnore] public bool IsPlannedToday => PlannedFor == DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    /// <summary>Set for today (picked or due), so the task row offers only Tomorrow.</summary>
    [JsonIgnore] public bool IsSetForToday => IsPlannedToday || IsDueToday;
    /// <summary>Set for tomorrow (picked or due), so the task row offers only Today.</summary>
    [JsonIgnore] public bool IsSetForTomorrow =>
        PlannedFor == DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) || (Due != null && Due.Value.Date == DateTime.Today.AddDays(1));
    [JsonIgnore] public bool IsHigh => Priority == TaskPriority.High;
    [JsonIgnore] public bool IsLow => Priority == TaskPriority.Low;

    /// <summary>Small details under the task, like "Due Fri, 2 sessions, #client". Empty if there are none.</summary>
    [JsonIgnore]
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (HasProject) parts.Add(SectionName.Length > 0 ? $"{ProjectName} / {SectionName}" : ProjectName);
            if (IsOverdue) parts.Add($"Overdue (was due {Due:ddd MMM d})");
            else if (IsDueToday) parts.Add("Due today");
            else if (Due is DateTime d) parts.Add(d < DateTime.Today.AddDays(7) ? $"Due {d:dddd}" : $"Due {d:MMM d}");
            if (IsSnoozed) parts.Add($"Snoozed until {SnoozedUntil:ddd MMM d}");
            if (RemindAt is DateTime r) parts.Add(r.Date == DateTime.Today ? $"Reminder {r:t}" : $"Reminder {r:ddd} {r:t}");
            if (Repeat.Length > 0) parts.Add(RepeatRule?.Describe() ?? "Repeats");
            if (Status == "doing") parts.Add("Doing");
            if (Status == "waiting") parts.Add(WaitingOn.Length > 0 ? $"Waiting on {WaitingOn}" : "Waiting");
            if (Subtasks.Count > 0) parts.Add($"{Subtasks.Count(s => s.Done)} of {Subtasks.Count} steps");
            if (Notes.Trim().Length > 0) parts.Add("Has notes");
            if (Estimate > 0) parts.Add(EstimateLabel(Estimate));
            if (Spent > 0) parts.Add(Spent == 1 ? "worked 1 session" : $"worked {Spent} sessions");
            parts.AddRange(Tags.Select(t => "#" + t));
            return string.Join(", ", parts);
        }
    }

    [JsonIgnore] public bool HasDetails => Details.Length > 0;

    public static string EstimateLabel(double estimate) =>
        estimate <= 0 ? "" : estimate < 1 ? "quick" : estimate == 1 ? "1 session" : $"{estimate:0} sessions";

    /// <summary>Copies the details (not the ID) from another task, for moving between the bin and sessions.</summary>
    public void CopyDetailsFrom(TaskBase other)
    {
        Text = other.Text;
        Priority = other.Priority;
        Due = other.Due;
        Estimate = other.Estimate;
        Tags = new List<string>(other.Tags);
        PlannedFor = other.PlannedFor;
        Spent = other.Spent;
        Repeat = other.Repeat;
        RemindAt = other.RemindAt;
        Notes = other.Notes;
        Subtasks = other.Subtasks.Select(s => new Subtask { Text = s.Text, Done = s.Done }).ToList();
        SnoozedUntil = other.SnoozedUntil;
        Status = other.Status;
        WaitingOn = other.WaitingOn;
        Project = other.Project;
        Section = other.Section;
        WaitingSince = other.WaitingSince;
        CreatedAt = other.CreatedAt;
    }

    /// <summary>Lets the screen know the steps changed (after ticking one).</summary>
    public void RefreshDetails() => Changed(nameof(Subtasks));

    /// <summary>Sort order for lists: overdue, due today, picked for today, high priority, then by due date.</summary>
    public static int Rank(TaskBase t) =>
        t.IsOverdue ? 0 : t.IsDueToday ? 1 : t.IsPlannedToday && t.IsHigh ? 2 : t.IsPlannedToday ? 3 : t.IsHigh ? 4 : t.IsLow ? 7 : t.Due != null ? 5 : 6;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Details)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDetails)));
    }
}

/// <summary>
/// Reads the shortcuts you can type when adding a task: #tag, ! (high priority), !low,
/// due:fri / due:10/14 / due:tomorrow. Everything else is the task's text.
/// </summary>
public static class TaskParser
{
    /// <summary>Turns natural-language reading on or off (Settings, Features).</summary>
    public static bool NaturalDates { get; set; } = true;

    /// <summary>Turns the @label / #Project / /Section / ! / due: shortcuts on or off (Settings, Features).</summary>
    public static bool Shortcuts { get; set; } = true;

    /// <summary>A note about something typed that couldn't be used (like a section that doesn't exist), or empty.</summary>
    public static string LastNotice { get; private set; } = "";

    /// <summary>
    /// Reads what you type when adding a task, like Todoist: @label adds a tag, #Project puts it in a
    /// project (by name or shortcut; a new name creates the project), /Section picks an existing section of
    /// that project, ! and !low set the priority, due:fri sets a due date, and everyday words set dates,
    /// times and repeats ("tomorrow at 3pm", "every friday").
    /// </summary>
    public static void Apply(TaskBase task, string input) => Apply(task, input, createProjects: true);

    /// <summary>Which project list #Project and /Section refer to: your main projects, unless another is given (like Personal's).</summary>
    public interface IProjectBook
    {
        Project? Find(string? id);
        Project? Match(string text);
        Project Add(string name);
    }

    private sealed class MainBook : IProjectBook
    {
        public Project? Find(string? id) => Projects.Find(id);
        public Project? Match(string text) => Projects.Match(text);
        public Project Add(string name) => Projects.Add(name);
    }

    public static readonly IProjectBook MainProjects = new MainBook();

    public static void Apply(TaskBase task, string input, bool createProjects) => Apply(task, input, createProjects, MainProjects);

    public static void Apply(TaskBase task, string input, bool createProjects, IProjectBook book)
    {
        LastNotice = "";
        var original = input.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var lower = original.Select(w => w.ToLowerInvariant()).ToList();
        var keep = new List<string>();
        DateTime? date = null;
        TimeSpan? time = null;
        RepeatRule? repeat = null;
        string repeatText = "", sectionWanted = "";
        var today = DateTime.Today;

        for (int i = 0; i < original.Count; i++)
        {
            string w = original[i], lw = lower[i];
            if (Shortcuts)
            {
                if (w.Length > 1 && w[0] == '@') { task.Tags.Add(w[1..].Trim(',', '.')); continue; }
                if (w.Length > 1 && w[0] == '#')
                {
                    // Longest existing project name or shortcut first ("#Client A" uses two words).
                    Project? found = null;
                    int used = 1;
                    for (int n = Math.Min(6, original.Count - i); n >= 1 && found == null; n--)
                    {
                        string candidate = string.Join(' ', original.Skip(i).Take(n))[1..].Trim(',', '.');
                        if (book.Match(candidate) is Project p) { found = p; used = n; }
                    }
                    if (found == null && createProjects)
                    {
                        string name = w[1..].Trim(',', '.');
                        if (name.Length > 0) found = book.Add(name);
                    }
                    if (found != null) { task.Project = found.Id; i += used - 1; continue; }
                }
                if (w.Length > 1 && w[0] == '/' && !w.Contains("//") && !w.Skip(1).Any(char.IsDigit))
                {
                    sectionWanted = w[1..].Trim(',', '.');
                    continue;
                }
                if (lw is "!" or "!high" or "!!") { task.Priority = TaskPriority.High; continue; }
                if (lw == "!low") { task.Priority = TaskPriority.Low; continue; }
                if (lw.StartsWith("due:") && ParseDate(w[4..]) is DateTime due) { date = due; continue; }
            }
            if (NaturalDates)
            {
                // In task text a repeat must start with "every" or "each", so titles like "Weekly report" stay intact.
                if (repeat == null && lw is "every" or "each" &&
                    RepeatRule.TryParse(lower, i, out var rule, out int usedRepeat, date ?? today) && usedRepeat > 0)
                {
                    repeat = rule;
                    repeatText = string.Join(' ', lower.Skip(i).Take(usedRepeat));
                    i += usedRepeat - 1;
                    continue;
                }
                if (time == null && DateWords.TryTime(lower, i, out int usedTime) is TimeSpan t)
                {
                    time = t;
                    i += usedTime - 1;
                    continue;
                }
                int skip = lw is "on" or "by" or "due" ? 1 : 0;
                if (date == null && DateWords.TryDate(lower, i + skip, out int usedDate, today, inText: true) is DateTime d)
                {
                    date = d;
                    i += skip + usedDate - 1;
                    continue;
                }
            }
            keep.Add(w);
        }

        // /Section: only an existing section of the task's project.
        if (sectionWanted.Length > 0)
        {
            var project = book.Find(task.Project);
            var section = project?.Sections.FirstOrDefault(x => x.Name.Equals(sectionWanted, StringComparison.CurrentCultureIgnoreCase));
            if (section != null) task.Section = section.Id;
            else
            {
                keep.Add("/" + sectionWanted);
                LastNotice = project == null
                    ? $"/{sectionWanted} needs a project: add #Project before it."
                    : $"No section named {sectionWanted} in {project.Name}.";
            }
        }

        if (repeat != null)
        {
            task.Repeat = repeatText;
            date ??= repeat.Type == RepeatRule.Kind.Days && !repeat.WeekdaysOnly ? today : repeat.Next(today.AddDays(-1));
        }
        if (time != null)
        {
            date ??= today;
            task.RemindAt = date.Value.Date + time.Value;
        }
        if (date != null) task.Due = date;
        task.Text = string.Join(' ', keep);
        task.Tags = task.Tags.Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>"today", "tomorrow", a weekday ("fri", "friday": the next one), or a date. Null if empty or unreadable.</summary>
    public static DateTime? ParseDate(string? text)
    {
        text = (text ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) return null;
        var words = RepeatRule.Words(text);
        if (DateWords.TryDate(words, 0, out int used, DateTime.Today) is DateTime phrase && used == words.Count) return phrase;
        if (text is "today" or "tod") return DateTime.Today;
        if (text is "tomorrow" or "tom" or "tmr") return DateTime.Today.AddDays(1);
        foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek)))
        {
            string name = d.ToString().ToLowerInvariant();
            if (text == name || (text.Length >= 3 && name.StartsWith(text)))
            {
                int ahead = ((int)d - (int)DateTime.Today.DayOfWeek + 7) % 7;
                return DateTime.Today.AddDays(ahead);
            }
        }
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
        {
            // A date without a year that has already passed means next year.
            if (!text.Any(char.IsDigit) || date.Year != DateTime.Today.Year || date.Date >= DateTime.Today.AddDays(-30)) return date.Date;
            return date.Date.AddYears(1);
        }
        return null;
    }
}

/// <summary>A step inside a task.</summary>
public class Subtask : INotifyPropertyChanged
{
    private string _text = "";
    private bool _done;
    public string Text { get => _text; set { _text = value ?? ""; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); } }
    public bool Done { get => _done; set { _done = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Done))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One to-do item on a focus session.</summary>
public class TaskItem : TaskBase
{
    /// <summary>The personal task this session task came from, if any (finishing it ticks that off in Personal).</summary>
    public string PersonalId { get; set; } = "";

    private bool _done;

    /// <summary>
    /// For a task dragged from your task list: the Id of that task. The list keeps it until it's
    /// finished, so the same task can sit in several sessions. Empty for tasks typed into a session.
    /// </summary>
    public string BinId { get; set; } = "";

    public bool Done { get => _done; set { _done = value; Changed(); } }

    public TaskItem Copy()
    {
        var copy = new TaskItem { Done = Done, BinId = BinId };
        copy.CopyDetailsFrom(this);
        return copy;
    }
}

/// <summary>A task waiting in your task list.</summary>
public class BinTask : TaskBase, IStamped
{
    /// <summary>When this task was last changed on any computer (UTC), so the newest version wins when syncing.</summary>
    public DateTime? Modified { get; set; }

    private string _from = "";

    /// <summary>Where it came from, like "From Focus session 3, Thu Oct 8".</summary>
    public string From { get => _from; set { _from = value ?? ""; Changed(); } }
}

/// <summary>A finished task, kept in the Resolved list.</summary>
public class ResolvedTask : IStamped
{
    public DateTime? Modified { get; set; }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public string From { get; set; } = "";
    public DateTime ResolvedAt { get; set; } = DateTime.Now;

    /// <summary>How many focus sessions it took (0 if not tracked).</summary>
    public int Spent { get; set; }

    /// <summary>The estimate it had, in sessions (0 if none), for comparing estimates with reality.</summary>
    public double Estimate { get; set; }

    public string Project { get; set; } = "";

    /// <summary>When the task was first created (for the daily record).</summary>
    public DateTime? CreatedAt { get; set; }

    public List<string> Tags { get; set; } = new();

    [JsonIgnore] public string Took => Spent switch { 0 => "", 1 => "took 1 session", _ => $"took {Spent} sessions" };
}

/// <summary>Every task you've finished, saved to resolved-tasks.json (synced with your task list).</summary>
public static class ResolvedTasks
{
    public static List<ResolvedTask> All { get => TaskStores.Current.Resolved; private set => TaskStores.Current.Resolved = value; }
    public static event Action? Changed;

    private static List<ResolvedTask> _base { get => TaskStores.Current.ResolvedBase; set => TaskStores.Current.ResolvedBase = value; }

    public static void Load()
    {
        All = Storage.Load<List<ResolvedTask>>(TaskStores.Current.DoneFile);
        _base = SyncMerge.Copy(All);
    }

    /// <summary>Re-reads after OneDrive brings a newer file, merging in anything done here meanwhile.</summary>
    public static void Reload() => Save();

    public static void Add(string text, string from, int spent = 0, double estimate = 0, string project = "",
                           DateTime? created = null, List<string>? tags = null)
    {
        All.Add(new ResolvedTask
        {
            Text = text, From = from, ResolvedAt = DateTime.Now, Spent = spent, Estimate = estimate, Project = project,
            CreatedAt = created, Tags = tags?.ToList() ?? new()
        });
        Save();
    }

    /// <summary>Saves by merging with OneDrive's current copy (see SyncMerge), writing only if something changed.</summary>
    public static void Save()
    {
        try
        {
            var merged = SyncMerge.SaveMerged(TaskStores.Current.DoneFile, _base, All, t => t.Id);
            if (merged != null) { All = merged; _base = SyncMerge.Copy(merged); }
        }
        catch (Exception ex) { Log.Error("ResolvedTasks.Save", ex); }
        Changed?.Invoke();
    }

    /// <summary>True if this task was finished (on any computer): same text and same creation time.</summary>
    public static bool WasFinished(BinTask t) =>
        t.CreatedAt != null && All.Any(r => r.CreatedAt == t.CreatedAt && r.Text == t.Text);

    public static int CountSince(DateTime from) => All.Count(t => t.ResolvedAt >= from);
}

/// <summary>Your task list: unfinished tasks, saved to task-bin.json.</summary>
/// <summary>A task that was finished or deleted (kept 90 days, so no old copy anywhere can bring it back).</summary>
public sealed class RemovedTask
{
    public string Id { get; set; } = "";
    public DateTime At { get; set; }
}

/// <summary>One set of tasks: the list, the finished list and the projects, with their files.</summary>
public sealed class TaskStore
{
    public TaskStore(string name, string binFile, string doneFile, string projectFile, string removedFile)
    {
        Name = name; BinFile = binFile; DoneFile = doneFile; ProjectFile = projectFile; RemovedFile = removedFile;
    }
    public string RemovedFile { get; }
    public List<RemovedTask> Removed { get; set; } = new();
    public List<RemovedTask> RemovedBase { get; set; } = new();
    public string Name { get; }
    public string BinFile { get; }
    public string DoneFile { get; }
    public string ProjectFile { get; }
    public System.Collections.ObjectModel.ObservableCollection<BinTask> Bin { get; } = new();
    public List<BinTask> BinBase { get; set; } = new();
    public List<ResolvedTask> Resolved { get; set; } = new();
    public List<ResolvedTask> ResolvedBase { get; set; } = new();
    public List<Project> Projects { get; set; } = new();
    public List<Project> ProjectsBase { get; set; } = new();
}

/// <summary>
/// Work and Personal: two completely separate sets of tasks. TaskBin, ResolvedTasks and Projects always mean the
/// active set, which is Work except while the Personal page is open (and Focus's background work, like reminders,
/// session endings and syncing, always runs on Work).
/// </summary>
public static class TaskStores
{
    public static readonly TaskStore Main = new("main", "task-bin.json", "resolved-tasks.json", "projects.json", "task-removed.json");
    public static readonly TaskStore Personal = new("personal", "personal-tasks.json", "personal-finished.json", "personal-projects.json", "personal-removed.json");

    public static TaskStore Current { get; private set; } = Main;
    public static bool IsPersonal => ReferenceEquals(Current, Personal);

    /// <summary>The Tasks page shows this set (Work, or Personal from its own button).</summary>
    public static void Show(TaskStore store) => Current = store;

    /// <summary>Works on a set for a moment, then goes back: using (TaskStores.Use(TaskStores.Main)) { ... }</summary>
    public static IDisposable Use(TaskStore store)
    {
        var before = Current;
        Current = store;
        return new Restore(() => Current = before);
    }

    private sealed class Restore : IDisposable
    {
        private Action? _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() { _undo?.Invoke(); _undo = null; }
    }
}

public static class TaskBin
{
    /// <summary>The active set's tasks (Work, unless the Personal page is open).</summary>
    public static System.Collections.ObjectModel.ObservableCollection<BinTask> All => TaskStores.Current.Bin;

    private static List<BinTask> _base { get => TaskStores.Current.BinBase; set => TaskStores.Current.BinBase = value; } // last loaded or saved

    /// <summary>
    /// Loads the list. Nothing is written just from opening Focus: writing at startup is what let an old
    /// computer (before OneDrive had caught up) put stale tasks back over newer ones.
    /// </summary>
    public static void Load()
    {
        var list = Storage.Load<List<BinTask>>(TaskStores.Current.BinFile);
        TaskStores.Current.Removed = Storage.Load<List<RemovedTask>>(TaskStores.Current.RemovedFile);
        TaskStores.Current.RemovedBase = SyncMerge.Copy(TaskStores.Current.Removed);
        _base = SyncMerge.Copy(list);
        TaskStores.Current.Bin.Clear();
        foreach (var t in list) TaskStores.Current.Bin.Add(t); // the same list object, so anything watching it stays connected
    }

    /// <summary>Re-reads after OneDrive brings a newer file, merging in anything done here meanwhile.</summary>
    public static void Reload() => Save(reloading: true);

    public static void Save() => Save(reloading: false);

    /// <summary>
    /// Saves by merging with OneDrive's current copy (see SyncMerge), so changes made on another computer
    /// or the phone are kept, and tasks finished elsewhere stay finished. Writes only if something changed.
    /// </summary>
    private static void Save(bool reloading)
    {
        // New tasks get their creation date when they're first saved.
        foreach (var t in All.Where(t => t.CreatedAt == null)) t.CreatedAt = DateTime.Now;
        try
        {
            // Remember what was finished or deleted here (and forget it again if you brought a task back, like with undo).
            var set = TaskStores.Current;
            var here = new HashSet<string>(All.Select(t => t.Id));
            var before = new HashSet<string>(_base.Select(t => t.Id));
            var now = DateTime.Now;
            foreach (var id in before.Where(id => !here.Contains(id)))
                if (!set.Removed.Any(r => r.Id == id)) set.Removed.Add(new RemovedTask { Id = id, At = now });
            set.Removed.RemoveAll(r => here.Contains(r.Id) && !before.Contains(r.Id)); // brought back here on purpose
            set.Removed.RemoveAll(r => now - r.At > TimeSpan.FromDays(90));
            if (SyncMerge.SaveMerged(set.RemovedFile, set.RemovedBase, set.Removed, r => r.Id) is List<RemovedTask> removed)
            {
                set.Removed = removed;
                set.RemovedBase = SyncMerge.Copy(removed);
            }
            var gone = new HashSet<string>(set.Removed.Select(r => r.Id));
            // Finished or deleted on any computer: never comes back.
            var merged = SyncMerge.SaveMerged(TaskStores.Current.BinFile, _base, All.ToList(), t => t.Id, t => !ResolvedTasks.WasFinished(t) && !gone.Contains(t.Id));
            if (merged == null) return; // OneDrive busy: kept here, saved again shortly
            _base = SyncMerge.Copy(merged);
            // Bring the list on screen up to date (same list object, so the screen stays connected).
            if (!merged.SequenceEqual(All))
            {
                All.Clear();
                foreach (var t in merged) All.Add(t);
            }
        }
        catch (Exception ex) { Log.Error(reloading ? "TaskBin.Reload" : "TaskBin.Save", ex); }
    }

    /// <summary>
    /// Takes what's genuinely new from a OneDrive conflict copy of the active list (OneDrive keeps both versions when
    /// two computers save at once): never a task finished or deleted anywhere; for a task in both, the most recently
    /// changed version; a task only in the copy only if it was made or changed in the last 7 days (older ones are
    /// leftovers from before, not new work). Returns how many tasks were added or updated.
    /// </summary>
    public static int MergeConflictCopy(List<BinTask> copy)
    {
        var set = TaskStores.Current;
        var gone = new HashSet<string>(set.Removed.Select(r => r.Id));
        var recent = DateTime.Now.AddDays(-7);
        int changed = 0;
        foreach (var t in copy)
        {
            if (gone.Contains(t.Id) || ResolvedTasks.WasFinished(t)) continue;
            var mine = All.FirstOrDefault(x => x.Id == t.Id);
            if (mine != null)
            {
                if ((t.Modified ?? DateTime.MinValue) > (mine.Modified ?? DateTime.MinValue))
                {
                    All[All.IndexOf(mine)] = t; // their newer version
                    changed++;
                }
                continue;
            }
            var when = t.Modified?.ToLocalTime() ?? t.CreatedAt;
            if (when != null && when >= recent) { All.Add(t); changed++; }
        }
        if (changed > 0) Save();
        return changed;
    }
}

/// <summary>A named group of apps to block together, like "Chat apps".</summary>
public class BlockList
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<BlockedApp> Apps { get; set; } = new();

    /// <summary>Websites this list blocks (like "facebook.com"), during sessions that use the list.</summary>
    public List<string> Sites { get; set; } = new();

    /// <summary>"block" blocks the sites above; "allow" blocks every site except them.</summary>
    public string SitesMode { get; set; } = "block";

    /// <summary>Freedom blocklists (Freedom's ids) to turn on during focus sessions that use this list.</summary>
    public List<string> FreedomBlocklistIds { get; set; } = new();

    [JsonIgnore]
    public string Summary
    {
        get
        {
            string apps = Apps.Count switch { 0 => "", 1 => "1 app", _ => $"{Apps.Count} apps" };
            string freedom = FreedomBlocklistIds.Count switch { 0 => "", 1 => "1 Freedom list", _ => $"{FreedomBlocklistIds.Count} Freedom lists" };
            string both = string.Join(" · ", new[] { apps, freedom }.Where(t => t.Length > 0));
            return both.Length > 0 ? both : "Empty";
        }
    }
}

/// <summary>All block lists, shared across the app and saved to blocklists.json.</summary>
public static class BlockLists
{
    public static List<BlockList> All { get; private set; } = new();

    public static event Action? Changed;

    public static void Load() => All = Storage.Load<List<BlockList>>("blocklists.json");

    public static void Save()
    {
        try { Storage.Save("blocklists.json", All); }
        catch (Exception ex) { Log.Error("BlockLists.Save", ex); }
        Changed?.Invoke();
    }

    public static BlockList? Find(string id) => All.FirstOrDefault(l => l.Id == id);
}

/// <summary>
/// An app to block. Regular apps are matched by program name (for example "chrome").
/// Microsoft Store apps (like WhatsApp) are also matched by package, which catches every
/// program inside the app and keeps working after the app updates itself.
/// </summary>
public class BlockedApp
{
    public string Name { get; set; } = "";
    public string Process { get; set; } = "";
    public string PackageName { get; set; } = "";

    /// <summary>Identifies the app, so the same app isn't listed twice.</summary>
    [JsonIgnore]
    public string Key => KeyFor(Process, PackageName);

    public static string KeyFor(string process, string packageName) =>
        string.IsNullOrEmpty(packageName) ? (process ?? "").ToLowerInvariant() : "pkg:" + packageName.ToLowerInvariant();

    public BlockedApp Copy() => new() { Name = Name, Process = Process, PackageName = PackageName };

    public static string Summary(IReadOnlyList<BlockedApp> apps)
    {
        if (apps.Count == 0) return "+ Block apps";
        string names = string.Join(", ", apps.Take(3).Select(a => a.Name));
        return apps.Count > 3 ? $"Blocking {names} and {apps.Count - 3} more" : $"Blocking {names}";
    }
}

/// <summary>One focus session or break in a day plan.</summary>
public class PlanBlock : INotifyPropertyChanged
{
    private BlockKind _kind;
    private string _name = "";
    private int _minutes = 1;
    private string _startLabel = "";
    private List<BlockedApp> _blockedApps = new();
    private List<string> _blockListIds = new();
    private bool _isSelected;
    private System.Collections.ObjectModel.ObservableCollection<TaskItem> _tasks = new();

    public PlanBlock() => Tasks = new System.Collections.ObjectModel.ObservableCollection<TaskItem>();

    /// <summary>To-do items for this session. Any change is reported as a change to Tasks.</summary>
    public System.Collections.ObjectModel.ObservableCollection<TaskItem> Tasks
    {
        get => _tasks;
        set
        {
            _tasks.CollectionChanged -= Tasks_CollectionChanged;
            foreach (var t in _tasks) t.PropertyChanged -= Task_PropertyChanged;
            _tasks = value ?? new();
            _tasks.CollectionChanged += Tasks_CollectionChanged;
            foreach (var t in _tasks) t.PropertyChanged += Task_PropertyChanged;
            OnChanged();
        }
    }

    private void Tasks_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (TaskItem t in e.OldItems) t.PropertyChanged -= Task_PropertyChanged;
        if (e.NewItems != null) foreach (TaskItem t in e.NewItems) t.PropertyChanged += Task_PropertyChanged;
        OnChanged(nameof(Tasks));
    }

    private void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e) => OnChanged(nameof(Tasks));

    public BlockKind Kind
    {
        get => _kind;
        set { _kind = value; OnChanged(); OnChanged(nameof(IsFocus)); OnChanged(nameof(KindLabel)); }
    }

    public string Name
    {
        get => _name;
        set { _name = value ?? ""; OnChanged(); }
    }

    public int Minutes
    {
        get => _minutes;
        set { _minutes = Math.Clamp(value, 1, 600); OnChanged(); }
    }

    public List<BlockedApp> BlockedApps
    {
        get => _blockedApps;
        set
        {
            _blockedApps = value ?? new();
            OnChanged();
            OnChanged(nameof(BlockedSummary));
            OnChanged(nameof(HasBlockedApps));
        }
    }

    /// <summary>Websites for this session only, and whether they're blocked ("block") or the only ones allowed ("allow").</summary>
    public List<string> Sites { get; set; } = new();
    public string SitesMode { get; set; } = "block";

    /// <summary>Block lists used by this session (their apps are blocked too).</summary>
    public List<string> BlockListIds
    {
        get => _blockListIds;
        set
        {
            _blockListIds = value ?? new();
            OnChanged();
            OnChanged(nameof(UsedLists));
            OnChanged(nameof(BlockedSummary));
            OnChanged(nameof(HasBlockedApps));
        }
    }

    /// <summary>For meetings from the calendar: the meeting's real start time, to spot drift.</summary>
    public DateTime? MeetingStart { get; set; }

    /// <summary>
    /// Set for things that must happen at a fixed time of day (meetings, lunch, the set break, the
    /// break after a meeting). The plan reshapes itself around them; they never move.
    /// </summary>
    public TimeSpan? AnchorStart { get; set; }

    [JsonIgnore] public bool IsAnchored => AnchorStart != null;

    [JsonIgnore] public bool IsFocus => Kind == BlockKind.Focus;
    [JsonIgnore] public bool IsMeeting => Kind == BlockKind.Meeting;
    [JsonIgnore] public string KindLabel => Kind switch { BlockKind.Focus => "Focus", BlockKind.Meeting => "Meeting", _ => "Break" };

    /// <summary>Every app this block blocks: its own apps plus the apps in its block lists.</summary>
    public List<BlockedApp> EffectiveApps()
    {
        var all = new List<BlockedApp>(BlockedApps);
        foreach (var id in BlockListIds)
            if (FocusApp.BlockLists.Find(id) is BlockList list) all.AddRange(list.Apps);
        return all.GroupBy(a => a.Key).Select(g => g.First()).ToList();
    }

    [JsonIgnore]
    public string BlockedSummary
    {
        get
        {
            var names = BlockListIds.Select(id => FocusApp.BlockLists.Find(id)?.Name).Where(n => n != null).Cast<string>()
                .Concat(BlockedApps.Select(a => a.Name)).ToList();
            if (names.Count == 0) return "Nothing blocked";
            string shown = string.Join(", ", names.Take(3));
            return names.Count > 3 ? $"Blocking {shown} and {names.Count - 3} more" : $"Blocking {shown}";
        }
    }

    [JsonIgnore] public bool HasBlockedApps => EffectiveApps().Count > 0;

    /// <summary>The block lists this block uses (for showing them on screen).</summary>
    [JsonIgnore]
    public List<BlockList> UsedLists =>
        BlockListIds.Select(id => FocusApp.BlockLists.Find(id)).Where(l => l != null).Cast<BlockList>().ToList();

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnChanged(); }
    }

    /// <summary>Tells the screen to redraw summaries after a block list changes.</summary>
    public void RefreshSummary()
    {
        OnChanged(nameof(UsedLists));
        OnChanged(nameof(BlockedSummary));
        OnChanged(nameof(HasBlockedApps));
    }

    [JsonIgnore]
    public string StartLabel
    {
        get => _startLabel;
        set { _startLabel = value; OnChanged(); }
    }

    public PlanBlock Clone() => new()
    {
        Kind = Kind,
        Name = Name,
        Minutes = Minutes,
        BlockedApps = BlockedApps.Select(a => a.Copy()).ToList(),
        BlockListIds = new List<string>(BlockListIds),
        Sites = new List<string>(Sites ?? new()),
        SitesMode = SitesMode,
        MeetingStart = MeetingStart,
        AnchorStart = AnchorStart,
        Tasks = new System.Collections.ObjectModel.ObservableCollection<TaskItem>(Tasks.Select(t => t.Copy()))
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A saved day, like "Tuesday".</summary>
public class DayLayout
{
    public string Name { get; set; } = "";
    /// <summary>Planned start time, stored as "HH:mm" (older files may use a local format).</summary>
    public string DayStart { get; set; } = "08:00";

    /// <summary>True when the plan starts whenever you press Start instead of at a set time.</summary>
    public bool StartsNow { get; set; }
    public List<PlanBlock> Blocks { get; set; } = new();

    [JsonIgnore]
    public string Summary
    {
        get
        {
            int focus = Blocks.Count(b => b.IsFocus);
            int minutes = Blocks.Sum(b => b.Minutes);
            string plural = focus == 1 ? "session" : "sessions";
            return $"{focus} focus {plural}, {Format.Duration(TimeSpan.FromMinutes(minutes))}";
        }
    }
}

public class AppSettings
{
    public int FocusMinutes { get; set; } = 35;
    public int BreakMinutes { get; set; } = 5;
    public bool SoundsOn { get; set; } = true;
    public int Volume { get; set; } = 70;
    public bool ShowFloatingTimer { get; set; } = true;
    public double OverlayLeft { get; set; } = double.NaN;
    public double OverlayTop { get; set; } = double.NaN;
    public List<BlockedApp> PomodoroBlocked { get; set; } = new();
    public List<BlockedApp> QuickFocusBlocked { get; set; } = new();
    public bool StartWithWindows { get; set; } = true;
    public bool MorningReminderOn { get; set; } = true;
    public string ReminderTime { get; set; } = "07:00";
    public string LastReminderDate { get; set; } = "";
    public bool ShownTrayTip { get; set; }
    public string Theme { get; set; } = "System";
    public bool NudgesOn { get; set; } = true;
    public string LastSessionDate { get; set; } = "";
    public string LastNudgeKey { get; set; } = "";
    public List<string> PomodoroLists { get; set; } = new();
    public List<string> QuickFocusLists { get; set; } = new();
    public bool AutoStartWorkdays { get; set; }
    public double DashRightWidth { get; set; } = 440;
    public string LastReviewDate { get; set; } = "";
    public bool WeekShowsPlan { get; set; } = true;
    public bool SidebarCollapsed { get; set; }

    // Greek practice
    public int VocabPerDay { get; set; } = 4;
    public int VocabWords { get; set; } = 4;
    public string VocabFrom { get; set; } = "08:30";
    public string VocabTo { get; set; } = "16:00";
    public bool VocabAutoAdd { get; set; } = true;
    /// <summary>Levels a word climbs before it retires (3 = quick, 12 = slow).</summary>
    public int VocabRetireAfter { get; set; } = 6;
    /// <summary>No word retires until My words holds more than this many.</summary>
    public int VocabRetireThreshold { get; set; } = 100;
    public int AlphaPerDay { get; set; } = 2;
    public int AlphaLetters { get; set; } = 5;
    public bool AlphaPairs { get; set; } = true;
    public string AlphaDone { get; set; } = "";
    /// <summary>Each practice's own hours (empty: the same as the Words hours).</summary>
    public string AlphaFrom { get; set; } = "";
    public string AlphaTo { get; set; } = "";
    public string ConceptFrom { get; set; } = "";
    public string ConceptTo { get; set; } = "";
    public bool PomoShowTasks { get; set; } = true;
    /// <summary>Show the floating timer all the time while Focus is open (not only when minimized). On unless you switch it off.</summary>
    public bool OverlayAlwaysOn { get; set; } = true;
    public bool OverlayPinned { get; set; }      // keep its tasks open (until unpinned or the day ends)
    /// <summary>Hidden with its ✕ (stays hidden, even after a restart, until the dashboard's 🌡 brings it back).</summary>
    public bool OverlayHidden { get; set; }
    public bool OverlayToday { get; set; }       // its tasks: today's (true) or this session's (false)
    /// <summary>How many tasks the floating timer shows before scrolling (each time Focus opens).</summary>
    public int OverlayRows { get; set; } = 6;

    // Concepts and extra unmatched answers (the *Away* settings are no longer used: practice follows the timelines)
    /// <summary>How tasks look: "default", "compact" or "paper" (also used when printing).</summary>
    public string TaskLook { get; set; } = "default";

    /// <summary>How the Personal page looks: its own theme and accent (in your light or dark mode).</summary>
    public string PersonalTheme { get; set; } = "Calm";
    public string PersonalAccent { get; set; } = "Green";
    /// <summary>The shortcut that opens the personal quick-add box from anywhere.</summary>
    public string PersonalHotkey { get; set; } = "Ctrl+Alt+Space";
    /// <summary>The floating timer's list: "session", "today" or "personal" (empty: from the older setting).</summary>
    public string OverlayView { get; set; } = "";
    /// <summary>What the floating timer's Personal tab shows: "today", "week", "all", or "project:ID".</summary>
    public string OverlayPersonalScope { get; set; } = "today";

    public int ConceptPerDay { get; set; } = 2;
    public int ConceptCount { get; set; } = 4;
    public string ConceptDone { get; set; } = "";
    public bool VocabAwayOn { get; set; }
    public int VocabAwayMinutes { get; set; } = 90;
    public bool AlphaAwayOn { get; set; }
    public int AlphaAwayMinutes { get; set; } = 120;
    public bool ConceptAwayOn { get; set; }
    public int ConceptAwayMinutes { get; set; } = 120;
    public int VocabExtras { get; set; }
    public int AlphaExtras { get; set; }
    public int ConceptExtras { get; set; }
    /// <summary>The task list's share of the Pomodoro tab's width (0.25 to 0.8).</summary>
    public double PomoTasksShare { get; set; } = 0.5;
    public string VocabDone { get; set; } = "";
    public bool HomeShowsPlan { get; set; } = true;
    public bool PlanTasksDayOnly { get; set; }

    // Update 4
    public string ColorTheme { get; set; } = "Classic";          // Classic, Mono, Calm
    public bool CountWeekends { get; set; } = true;               // false: weekends stay out of charts and the streak
    public int ExtendMinutes { get; set; } = 10;
    public string NewMeetings { get; set; } = "ask";              // ask, overlay, fit
    public string WebMode { get; set; } = "block";                // block, allow
    public string WebWhen { get; set; } = "focus";                // focus, day
    public List<string> WebCategories { get; set; } = new();
    public Dictionary<string, List<string>> WebCategorySites { get; set; } = new();
    public List<string> AllowedSites { get; set; } = new();
    public string VaultFolder { get; set; } = "";
    public List<string> RecordFallbackDays { get; set; } = new();
    public string LastBinView { get; set; } = "list";
    public string LastBinFilter { get; set; } = "all";
    public string LastDashTasksView { get; set; } = "session";
    public string PlanTasksView { get; set; } = "all";
    public bool TourShown { get; set; }

    // Update 3
    public double PlanEditorWidth { get; set; } = 320;
    public double PlanTasksWidth { get; set; } = 290;
    public int GoalTasks { get; set; } = 5;
    public double GoalFocusHours { get; set; } = 4;
    public string LastGoalCelebrated { get; set; } = "";
    public List<string> BlockedSites { get; set; } = new();
    public int WaitingReminderDays { get; set; } = 3;
    public string LastAutomationDay { get; set; } = "";

    /// <summary>Features you've switched off in Settings (missing means on).</summary>
    public Dictionary<string, bool> Features { get; set; } = new();

    public bool On(string feature) => Features.TryGetValue(feature, out bool on) ? on : FocusApp.Features.DefaultOn(feature);

    // Practice timelines (1.3.0): your own round times for "words", "alphabet" and "concepts", like "09:00,11:30".
    // A practice with no entry spreads its rounds evenly between its hours, as before.
    public Dictionary<string, string> PracticeTimes { get; set; } = new();
    /// <summary>Saturdays and Sundays: no practice, lesson or review pops up by itself (you can still start any of them). Synced.</summary>
    public bool PauseWeekends { get; set; }
    /// <summary>The practice day ends at this time: nothing pops up by itself after it, and anything left open while you're away closes. Synced.</summary>
    public bool PracticeEndOn { get; set; } = true;
    public string PracticeEnd { get; set; } = "16:30";
    /// <summary>1.7.1 moved every practice onto its timeline (the old evenly spread rounds became timeline times once).</summary>
    public bool PracticeOnTimelines { get; set; }
    /// <summary>When this computer's practice timelines last changed (here or adopted from practice-times.json).</summary>
    public DateTime PracticeTimesUpdatedUtc { get; set; }
    /// <summary>Times changed for today only (PracticeTodayDate is the day they're for).</summary>
    public Dictionary<string, string> PracticeTodayTimes { get; set; } = new();
    public string PracticeTodayDate { get; set; } = "";

    // Customization
    public double DisplayScale { get; set; } = 1.0;
    public double TaskFontSize { get; set; } = 15;
    public double CalendarSpacing { get; set; } = 1.0;
    public int AlertSeconds { get; set; } = 4;
    public int WarningSeconds { get; set; } = 10;
    public bool OverlaySeconds { get; set; } = true;
    public string WorkdayStart { get; set; } = "06:45";
    public string WorkdayEnd { get; set; } = "16:30";

    [JsonIgnore] public TimeSpan WorkdayStartTime => ParseTime(WorkdayStart, Defaults.AutoFillStart);
    [JsonIgnore] public TimeSpan WorkdayEndTime => ParseTime(WorkdayEnd, Defaults.AutoFillEnd);

    private static TimeSpan ParseTime(string? text, TimeSpan fallback) =>
        TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var t) && t > TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : fallback;
    public string LastAutoStartDate { get; set; } = "";
    public List<string> DefaultPlanLists { get; set; } = new();
    public List<BlockedApp> DefaultPlanApps { get; set; } = new();
    public bool LockedMode { get; set; }
    public bool ExtremeLock { get; set; }
    public bool FlashOnChange { get; set; } = true;

    [JsonIgnore]
    public TimeSpan ReminderTimeOfDay =>
        TimeSpan.TryParse(ReminderTime, CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)
            ? t
            : new TimeSpan(7, 0, 0);

    public void Sanitize()
    {
        if (FocusMinutes < 1 || FocusMinutes > 600) FocusMinutes = 35;
        if (BreakMinutes < 1 || BreakMinutes > 600) BreakMinutes = 5;
        Volume = Math.Clamp(Volume, 0, 100);
        PomodoroBlocked ??= new();
        QuickFocusBlocked ??= new();
        ReminderTime ??= "07:00";
        DisplayScale = Math.Clamp(DisplayScale <= 0 ? 1 : DisplayScale, 0.8, 1.6);
        TaskFontSize = Math.Clamp(TaskFontSize <= 0 ? 15 : TaskFontSize, 12, 24);
        CalendarSpacing = Math.Clamp(CalendarSpacing <= 0 ? 1 : CalendarSpacing, 0.6, 2);
        AlertSeconds = Math.Clamp(AlertSeconds, 1, 30);
        WarningSeconds = Math.Clamp(WarningSeconds, 0, 120);
        WorkdayStart ??= "06:45";
        Features ??= new();
        BlockedSites ??= new();
        WebCategories ??= new();
        WebCategorySites ??= new();
        AllowedSites ??= new();
        RecordFallbackDays ??= new();
        ColorTheme = ColorTheme is "Classic" or "Mono" or "Calm" ? ColorTheme : "Classic";
        NewMeetings = NewMeetings is "ask" or "overlay" or "fit" ? NewMeetings : "ask";
        WebMode = WebMode is "block" or "allow" ? WebMode : "block";
        WebWhen = WebWhen is "focus" or "day" or "always" ? WebWhen : "focus";
        ExtendMinutes = Math.Clamp(ExtendMinutes, 1, 60);
        VaultFolder ??= "";
        GoalTasks = Math.Clamp(GoalTasks, 1, 100);
        GoalFocusHours = Math.Clamp(GoalFocusHours, 0.5, 16);
        WaitingReminderDays = Math.Clamp(WaitingReminderDays, 1, 60);
        WorkdayEnd ??= "16:30";
        Theme ??= "System";
        LastSessionDate ??= "";
        LastNudgeKey ??= "";
        PomodoroLists ??= new();
        LastAutoStartDate ??= "";
        DefaultPlanLists ??= new();
        DefaultPlanApps ??= new();
        QuickFocusLists ??= new();
        LastReminderDate ??= "";
    }
}

/// <summary>The plan for one date, saved as plans\yyyy-MM-dd.json.</summary>
/// <summary>plan-defaults.json (synced): the block lists and apps every new focus session starts with, the same on every computer.</summary>
public class PlanDefaults
{
    public List<string> Lists { get; set; } = new();
    public List<BlockedApp> Apps { get; set; } = new();
    /// <summary>Pause all practice on weekends (shared so every computer agrees).</summary>
    public bool PauseWeekends { get; set; }
    public bool PracticeEndOn { get; set; } = true;
    public string PracticeEnd { get; set; } = "16:30";
    public DateTime UpdatedUtc { get; set; }
}

public class DayPlanFile
{
    public string Date { get; set; } = "";
    public string DayStart { get; set; } = "06:45";
    public bool StartsNow { get; set; }
    public List<PlanBlock> Blocks { get; set; } = new();

    /// <summary>Made automatically from the calendar and not changed by you since.</summary>
    public bool Automatic { get; set; }

    /// <summary>When this day's work ends ("HH:mm"), if you adjusted it; empty means the usual time.</summary>
    public string DayEnd { get; set; } = "";

    /// <summary>When this day's work starts ("HH:mm"), if you adjusted it.</summary>
    public string WorkStart { get; set; } = "";

    /// <summary>Start this day's plan by itself when its first session begins.</summary>
    public bool AutoStart { get; set; }

    /// <summary>A fingerprint of the meetings the plan was built around, to spot calendar changes.</summary>
    public string CalendarFingerprint { get; set; } = "";

    public static string FileFor(DateTime date) =>
        System.IO.Path.Combine("plans", date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".json");
}

/// <summary>
/// What's running right now, saved to disk so a session survives Focus being closed,
/// crashing, or the PC restarting.
/// </summary>
public class RunState
{
    public string Mode { get; set; } = "";
    public List<PlanBlock> Blocks { get; set; } = new();
    public List<DateTime>? ScheduledEndsUtc { get; set; }
    public bool Loop { get; set; }
    public int Index { get; set; }
    public int Round { get; set; } = 1;
    public DateTime EndsAtUtc { get; set; }
    public bool Paused { get; set; }
    public TimeSpan PausedRemaining { get; set; }
    public bool Locked { get; set; }
    public bool Extreme { get; set; }

    /// <summary>Set when Windows is shutting down, so Focus isn't relaunched mid-shutdown.</summary>
    public bool WindowsShuttingDown { get; set; }

    [JsonIgnore] public bool IsActive => Blocks.Count > 0;
}

public static class Format
{
    public static string Duration(TimeSpan t)
    {
        int h = (int)t.TotalHours;
        int m = t.Minutes;
        if (h == 0) return $"{m}m";
        return m == 0 ? $"{h}h" : $"{h}h {m}m";
    }

    /// <summary>Like 34:50, counting down in steps of ten seconds (for the floating timer).</summary>
    public static string ClockTens(TimeSpan t)
    {
        int total = (int)Math.Ceiling(Math.Max(0, t.TotalSeconds) / 10.0) * 10;
        int h = total / 3600, m = (total % 3600) / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }

    /// <summary>A total like "2 h 15 min" (rounded down to the minute).</summary>
    public static string HoursMinutes(double minutes)
    {
        int total = (int)Math.Floor(Math.Max(0, minutes));
        int h = total / 60, m = total % 60;
        return h == 0 ? $"{m} min" : m == 0 ? $"{h} h" : $"{h} h {m} min";
    }

    /// <summary>Minutes only, rounded up, like "34 min" or "1 h 5 min".</summary>
    public static string MinutesLeft(TimeSpan t)
    {
        int minutes = (int)Math.Ceiling(Math.Max(0, t.TotalMinutes));
        if (minutes < 60) return $"{minutes} min";
        int h = minutes / 60, m = minutes % 60;
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }

    public static string Clock(TimeSpan t)
    {
        int total = (int)Math.Ceiling(Math.Max(0, t.TotalSeconds));
        int h = total / 3600, m = (total % 3600) / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }
}

/// <summary>Screen state the window binds to.</summary>
public class UiState : INotifyPropertyChanged
{
    private bool _running;
    private bool _planRunning;

    /// <summary>Any session is running, so nothing can be unblocked.</summary>
    public bool Running
    {
        get => _running;
        set
        {
            if (_running == value) return;
            _running = value;
            OnChanged();
            OnChanged(nameof(RemoveVisibility));
        }
    }

    /// <summary>A day plan is running, so its times can't change.</summary>
    public bool PlanRunning
    {
        get => _planRunning;
        set
        {
            if (_planRunning == value) return;
            _planRunning = value;
            OnChanged();
        }
    }

    private System.Windows.Visibility _todayVisibility = System.Windows.Visibility.Visible;

    /// <summary>The ☀ buttons (hidden when Today's tasks is switched off).</summary>
    public System.Windows.Visibility TodayVisibility
    {
        get => _todayVisibility;
        set
        {
            if (_todayVisibility == value) return;
            _todayVisibility = value;
            OnChanged();
        }
    }

    public System.Windows.Visibility RemoveVisibility =>
        Running ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A block list shown with a check box in the block list picker.</summary>
public class ListChoice : INotifyPropertyChanged
{
    private bool _isChecked;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool CanChange { get; init; } = true;

    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Minutes of real, app-blocking focus time per day. Each computer saves its own file
/// (focus-stats-PCNAME.json) and the totals add up every computer's file, so two synced
/// computers never overwrite each other's numbers.
/// </summary>
public class FocusStats
{
    /// <summary>This computer's minutes, by day.</summary>
    public Dictionary<string, double> MinutesByDay { get; set; } = new();

    private Dictionary<string, double> _others = new();

    private static string Key(DateTime day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    private static string MyFile => $"focus-stats-{Sync.MachineName}.json";

    public static FocusStats Load()
    {
        if (!Storage.TryLoad(MyFile, out FocusStats mine))
            mine = Storage.Load<FocusStats>("focus-stats.json"); // the single file used before sync existed
        mine.MinutesByDay ??= new();
        mine.LoadOthers();
        return mine;
    }

    /// <summary>Reads the other computers' totals from the sync folder.</summary>
    private void LoadOthers()
    {
        _others = new();
        try
        {
            if (!System.IO.Directory.Exists(Storage.SharedFolder)) return;
            foreach (var path in System.IO.Directory.GetFiles(Storage.SharedFolder, "focus-stats-*.json"))
            {
                string name = System.IO.Path.GetFileName(path);
                if (string.Equals(name, MyFile, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Storage.TryLoad(name, out FocusStats other) || other.MinutesByDay == null) continue;
                foreach (var (day, minutes) in other.MinutesByDay)
                    _others[day] = _others.GetValueOrDefault(day) + minutes;
            }
        }
        catch (Exception ex)
        {
            Log.Error("FocusStats.LoadOthers", ex);
        }
    }

    public void Add(DateTime day, TimeSpan time)
    {
        string key = Key(day);
        MinutesByDay[key] = MinutesByDay.GetValueOrDefault(key) + time.TotalMinutes;
    }

    /// <summary>Every day with focus time, on any computer.</summary>
    public Dictionary<DateTime, double> AllDays()
    {
        var all = new Dictionary<DateTime, double>();
        foreach (var source in new[] { MinutesByDay, _others })
            foreach (var (key, minutes) in source)
                if (DateTime.TryParseExact(key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var day))
                    all[day] = all.GetValueOrDefault(day) + minutes;
        return all;
    }

    public double MinutesOn(DateTime day) =>
        MinutesByDay.GetValueOrDefault(Key(day)) + _others.GetValueOrDefault(Key(day));

    /// <summary>Monday through Sunday of the week containing <paramref name="day"/>.</summary>
    public double MinutesInWeekOf(DateTime day)
    {
        int sinceMonday = ((int)day.DayOfWeek + 6) % 7;
        var monday = day.Date.AddDays(-sinceMonday);
        return Enumerable.Range(0, 7).Sum(i => MinutesOn(monday.AddDays(i)));
    }

    public void Save()
    {
        // Keep about two years of history so the file stays small.
        var cutoff = Key(DateTime.Today.AddDays(-730));
        foreach (var old in MinutesByDay.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
            MinutesByDay.Remove(old);
        try { Storage.Save(MyFile, this); }
        catch (Exception ex) { Log.Error("FocusStats.Save", ex); }
    }
}

/// <summary>A section inside a project (like "Legal" in "Client A").</summary>
public class ProjectSection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
}

/// <summary>A project (like "Client A"), with a color, typing shortcuts and sections. Saved to projects.json (synced).</summary>
public class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#DC4C3E";

    /// <summary>Short names you can type after # instead of the full name (like "goj" or "1").</summary>
    public List<string> Aliases { get; set; } = new();

    public List<ProjectSection> Sections { get; set; } = new();

    [JsonIgnore] public string AliasText => Aliases.Count == 0 ? "" : string.Join(", ", Aliases.Select(a => "#" + a));
    [JsonIgnore] public string SectionText => Sections.Count == 0 ? "" : string.Join(", ", Sections.Select(x => x.Name));
}

public static class Projects
{
    /// <summary>Colors new projects take in turn.</summary>
    public static readonly string[] Palette = { "#DC4C3E", "#246FE0", "#058527", "#EB8909", "#884DFF", "#14AAF5", "#B8256F", "#6ACCBC" };

    public static List<Project> All { get => TaskStores.Current.Projects; private set => TaskStores.Current.Projects = value; }
    public static event Action? Changed;

    public static void Load()
    {
        All = Storage.Load<List<Project>>(TaskStores.Current.ProjectFile);
        TaskStores.Current.ProjectsBase = SyncMerge.Copy(All);
    }

    /// <summary>After OneDrive brings newer files: merges in projects added or changed elsewhere (the phone, the other computer).</summary>
    public static void Reload() => Save();

    public static void Save()
    {
        // Merged with OneDrive's copy (like tasks), so a project added on the phone or the other computer is kept,
        // never overwritten; written only if something changed.
        try
        {
            var set = TaskStores.Current;
            if (SyncMerge.SaveMerged(set.ProjectFile, set.ProjectsBase, All, p => p.Id) is List<Project> merged)
            {
                All = merged;
                set.ProjectsBase = SyncMerge.Copy(merged);
            }
        }
        catch (Exception ex) { Log.Error("Projects.Save", ex); }
        Changed?.Invoke();
    }

    public static Project? Find(string? id) => string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(p => p.Id == id);

    public static Project Add(string name)
    {
        var project = new Project { Name = name.Trim(), Color = Palette[All.Count % Palette.Length] };
        All.Add(project);
        Save();
        return project;
    }

    /// <summary>The project with this name or shortcut (capitals don't matter), or null.</summary>
    public static Project? Match(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        return All.FirstOrDefault(p => p.Aliases.Any(a => a.Equals(text, StringComparison.CurrentCultureIgnoreCase)))
            ?? All.FirstOrDefault(p => p.Name.Equals(text, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Who already uses this name or shortcut (other than <paramref name="except"/>), or null if it's free.</summary>
    public static Project? Clash(string text, Project? except) =>
        All.FirstOrDefault(p => !ReferenceEquals(p, except) &&
            (p.Name.Equals(text, StringComparison.CurrentCultureIgnoreCase) ||
             p.Aliases.Any(a => a.Equals(text, StringComparison.CurrentCultureIgnoreCase))));

    /// <summary>A starting shortcut for a new project: its initials ("Gospel of John" gives "goj").</summary>
    public static string SuggestAlias(string name)
    {
        var letters = new string(name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToLowerInvariant(w[0])).Where(char.IsLetterOrDigit).ToArray());
        return letters.Length >= 2 && Clash(letters, null) == null ? letters : "";
    }
}

/// <summary>A saved set of tasks to add in one go (one task per line). Saved to templates.json (synced).</summary>
public class TaskTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Lines { get; set; } = "";

    [JsonIgnore]
    public int Count => Lines.Split('\n').Count(l => l.Trim().Length > 0);

    [JsonIgnore]
    public string Summary => Count == 1 ? "1 task" : $"{Count} tasks";
}

public static class Templates
{
    public static List<TaskTemplate> All { get; private set; } = new();

    public static void Load() => All = Storage.Load<List<TaskTemplate>>("templates.json");

    public static void Save()
    {
        try { Storage.Save("templates.json", All); }
        catch (Exception ex) { Log.Error("Templates.Save", ex); }
    }
}
