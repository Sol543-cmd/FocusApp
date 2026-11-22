using System;
using System.Windows;

namespace FocusApp;

/// <summary>
/// "Tomorrow" for tasks: the next day, except for work tasks on a Friday, when it's Monday (the weekend isn't a work
/// day). Personal tasks always use the real tomorrow. The Tomorrow tab, each task's Tomorrow tag and the other
/// Tomorrow buttons all use it.
/// </summary>
public static class NextDay
{
    /// <summary>For the set in use right now (Personal: tomorrow; Work: Monday on Fridays).</summary>
    public static DateTime Date => DateFor(TaskStores.IsPersonal);
    public static string Name => NameFor(TaskStores.IsPersonal);
    public static string Key => Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static bool Skips(bool personal) => !personal && DateTime.Today.DayOfWeek == DayOfWeek.Friday;
    public static DateTime DateFor(bool personal) => DateTime.Today.AddDays(Skips(personal) ? 3 : 1);
    public static string NameFor(bool personal) => Skips(personal) ? "Monday" : "Tomorrow";

    private static string _shown = "";
    /// <summary>Puts the name in the app's resources (for the task tags on the page showing); true when it changed.</summary>
    public static bool Refresh(bool personal = false)
    {
        string name = NameFor(personal);
        if (_shown == name || Application.Current == null) return false;
        _shown = name;
        Application.Current.Resources["NextDayName"] = name;
        Application.Current.Resources["NextDayTip"] = name == "Monday" ? "Add to Monday" : "Add to tomorrow";
        return true;
    }
}
