using System;

namespace FocusApp;

/// <summary>
/// Fixed numbers and words used around the app, collected in one place so they're easy to change.
/// Edit a value here, then rebuild (see MAINTENANCE.md).
/// </summary>
public static class Defaults
{
    /// <summary>Seconds of warning before already-open blocked apps are closed.</summary>
    public const int WarningSeconds = 10;

    /// <summary>The two short-break lengths, in minutes.</summary>
    public const int ShortBreakA = 3;
    public const int ShortBreakB = 8;

    /// <summary>What you type to take a short break in locked or extreme mode.</summary>
    public const string BreakPhrase = "I want to waste time";

    /// <summary>The hours the day calendar shows, and the hours an automatic plan fills.</summary>
    public static readonly TimeSpan CalendarStart = new(6, 0, 0);
    public static readonly TimeSpan CalendarEnd = new(22, 0, 0);

    /// <summary>The hours an automatic plan fills (it stretches to fit a meeting that runs past them).</summary>
    public static readonly TimeSpan AutoFillStart = new(6, 45, 0);
    public static readonly TimeSpan AutoFillEnd = new(16, 30, 0);

    /// <summary>How often linked calendars are checked for changes, in minutes.</summary>
    public const int CalendarRefreshMinutes = 15;

    /// <summary>Fixed breaks that every automatic plan includes.</summary>
    public static readonly TimeSpan MorningBreakStart = new(10, 5, 0);
    public static readonly TimeSpan MorningBreakEnd = new(10, 25, 0);
    public const string MorningBreakName = "Break";
    public static readonly TimeSpan LunchStart = new(12, 20, 0);
    public static readonly TimeSpan LunchEnd = new(12, 50, 0);
    public const string LunchName = "Lunch";

    /// <summary>Morning devotions: a fixed block like lunch (no break after it). Delete it on any day you don't want it.</summary>
    public static readonly TimeSpan DevotionsStart = new(8, 0, 0);
    public static readonly TimeSpan DevotionsEnd = new(8, 15, 0);
    public const string DevotionsName = "Devotions";

    /// <summary>Break placed after every meeting from your calendar, in minutes.</summary>
    public const int BreakAfterMeeting = 8;

    /// <summary>How much automatic plans may stretch or shrink focus sessions and breaks, in minutes.</summary>
    public const int FocusMin = 25;
    public const int FocusMax = 40;
    public const int BreakMin = 4;
    public const int BreakMax = 7;

    /// <summary>Times on workdays to nudge you if you haven't started a session yet.</summary>
    public static readonly TimeSpan[] NudgeTimes =
    {
        new(9, 30, 0), new(11, 30, 0), new(14, 0, 0), new(15, 30, 0)
    };

    public static readonly string[] NudgeMessages =
    {
        "Your day is wide open. Plan a few focus sessions and protect your best hours.",
        "Half the morning is gone. One focus session now still counts.",
        "Afternoons slip away fast. Start a focus session and get one real thing done.",
        "There's still time for a solid focus block before the day ends."
    };
}
