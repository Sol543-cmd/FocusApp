using System;
using System.Windows;

namespace FocusApp;

/// <summary>
/// "Tomorrow" for tasks: the next day, except on Friday, when it's Monday (the weekend isn't a work day). The Tomorrow
/// tab, the Tomorrow tag on each task and the other Tomorrow buttons all use it, and say "Monday" on Fridays.
/// </summary>
public static class NextDay
{
    public static DateTime Date => DateTime.Today.AddDays(DateTime.Today.DayOfWeek == DayOfWeek.Friday ? 3 : 1);
    public static string Name => DateTime.Today.DayOfWeek == DayOfWeek.Friday ? "Monday" : "Tomorrow";
    public static string Key => Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string _shown = "";
    /// <summary>Puts the name in the app's resources (for the task tags); true when it changed (a new day).</summary>
    public static bool Refresh()
    {
        if (_shown == Name || Application.Current == null) return false;
        _shown = Name;
        Application.Current.Resources["NextDayName"] = Name;
        Application.Current.Resources["NextDayTip"] = $"Add to {(Name == "Monday" ? "Monday" : "tomorrow")}";
        return true;
    }
}
