using System.Collections.Generic;
using System.Linq;

namespace FocusApp;

/// <summary>
/// Features you can switch on or off in Settings, grouped by what they do. The key is what's saved
/// (never change a key, or saved choices are lost); the rest is shown on screen.
/// </summary>
public static class Features
{
    public record Feature(string Key, string Name, string Description, string Area, bool OnByDefault = true);

    /// <summary>Whether a feature is on when you haven't chosen yet.</summary>
    public static bool DefaultOn(string key) => All.FirstOrDefault(f => f.Key == key)?.OnByDefault ?? true;

    public static readonly string[] Areas = { "Tasks", "Planning", "Focus", "Stats and records", "App" };

    public static readonly IReadOnlyList<Feature> All = new[]
    {
        // Tasks
        new Feature("taskDetails", "Task details", "Click a task to set its priority, due date, estimate, tags and more.", "Tasks"),
        new Feature("taskShortcuts", "Typing shortcuts", "@label, #Project (or its shortcut), /Section, ! for high priority, !low and due: when adding tasks.", "Tasks"),
        new Feature("naturalDates", "Everyday dates, times and reminders", "Type \"tomorrow at 3pm\" or \"every friday\"; reminders at a set time.", "Tasks"),
        new Feature("repeat", "Repeating tasks", "Tasks that come back on a schedule, like every Friday.", "Tasks"),
        new Feature("subtasks", "Steps and notes", "Checklists and notes inside a task.", "Tasks"),
        new Feature("projects", "Projects", "Colored projects with shortcuts and sections, shown in the Tasks sidebar.", "Tasks"),
        new Feature("templates", "Task templates", "Saved sets of tasks you can add in one go.", "Tasks"),
        new Feature("taskFilters", "Filters", "The Show list: Today, Overdue, High priority, tags and projects.", "Tasks"),
        new Feature("ageFilters", "Age filters", "\"+ one week old\" and \"+ ten days old\" in the filters.", "Tasks"),
        new Feature("smartOrder", "Most important first", "Sorts tasks by due date and priority.", "Tasks"),
        new Feature("upcoming", "Upcoming and snooze", "The Upcoming view, and hiding a task until a later day.", "Tasks"),
        new Feature("board", "Board", "To do, Doing, Waiting and Done columns, with drag and drop.", "Tasks"),
        new Feature("search", "Search", "Search boxes for your tasks.", "Tasks"),
        new Feature("undo", "Undo and redo", "Undo buttons, Ctrl+Z and Ctrl+Y for task and plan changes.", "Tasks"),
        new Feature("quickAdd", "Quick-add key", "Ctrl+Shift+Space anywhere in Windows to add a task.", "Tasks"),
        new Feature("commandBar", "Command bar (Ctrl+K)", "Type to start sessions, add tasks, jump to tabs, or find tasks.", "Tasks"),
        new Feature("autoWaiting", "Waiting reminder", "Reminds you when a task has been Waiting for several days.", "Tasks"),
        new Feature("autoOverdueHigh", "Overdue becomes high priority", "Overdue tasks are marked high priority automatically.", "Tasks"),
        new Feature("autoDoingReset", "Doing resets overnight", "Tasks left in Doing move back to To do the next morning.", "Tasks"),

        // Planning
        new Feature("todayPlanning", "Today's tasks", "☀ to pick tasks for today, the capacity line, and Fill today's sessions.", "Planning"),
        new Feature("timedTasks", "Tasks with a time go into sessions", "A task with a time goes into the focus session at that time (or the next one).", "Planning"),
        new Feature("carryOver", "Multi-session tasks", "Unfinished tasks estimated at several sessions carry on into the next one.", "Planning"),
        new Feature("oneClickRename", "One-click renaming", "Clicking a block puts its title ready to type over.", "Planning"),
        new Feature("review", "End-of-day review", "Decide where unfinished tasks go when your day ends.", "Planning"),
        new Feature("workload", "Workload", "The coming week's planned work against your focus sessions (in Stats).", "Planning"),

        // Focus
        new Feature("concepts", "Concepts practice", "Rounds matching your own questions to their answers.", "Focus"),
        new Feature("alphabet", "Greek alphabet practice", "Rounds matching Greek letters to their modern Greek sounds.", "Focus"),
        new Feature("greek", "Greek practice", "Rounds of Greek vocabulary matching pop up a few times a day (never in meetings or lunch).", "Focus"),
        new Feature("focusKey", "Start-focus key", "Ctrl+Shift+F anywhere starts or stops a quick focus session.", "Focus"),
        new Feature("dashTaskEditing", "Task editing on the dashboard", "Edit tasks, add from your list, and print from the dashboard.", "Focus"),
        new Feature("timerTasks", "Tasks in the floating timer", "Hover over the floating timer to see and tick off this session's tasks.", "Focus"),

        // Stats and records
        new Feature("goals", "Daily goals and streaks", "A daily goal for tasks and focus hours, with a streak.", "Stats and records"),
        new Feature("statsGame", "Points, levels and badges", "The game side of Stats.", "Stats and records"),
        new Feature("stats", "Charts", "Charts of focus hours and tasks finished, and estimates versus reality.", "Stats and records"),
        new Feature("dailyRecords", "Daily records", "A Markdown file for every day, in your Focus folder or Obsidian vault.", "Stats and records"),

        // App
        new Feature("home", "Home page", "The Home page with a greeting and today's calendar (the dashboard opens otherwise).", "App", OnByDefault: false),
        new Feature("tour", "Welcome tour", "A short tour the first time Focus runs on a computer.", "App"),
        new Feature("updates", "Check for updates", "Twice a day, Focus checks your GitHub Releases page and offers new versions (it never updates without asking). Check for updates in the tray menu works either way.", "App"),
    };
}
