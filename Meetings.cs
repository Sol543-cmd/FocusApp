using System;
using System.Collections.Generic;
using System.Linq;

namespace FocusApp;

/// <summary>
/// Meetings: a built-in "project" like No Project. It holds your calendar's meetings (from the calendars your day is
/// planned around, such as Outlook) and any task you type with #meeting. In By project views (Tasks, the task window
/// and the thermometer) the day's meetings show first under Meetings, with their time and length; a meeting drops off
/// once it's over, and a cancelled one once the calendar updates. Not in Personal.
/// </summary>
public static class Meetings
{
    public const string Id = "meetings";
    public const string Name = "Meetings";
    public const string Color = "#5B6BD8";

    /// <summary>Calendar meetings not over yet that overlap from..to (timed, busy, from calendars your day is planned around).</summary>
    public static List<CalendarEvent> Between(DateTime from, DateTime to)
    {
        var now = DateTime.Now;
        return CalendarService.Events
            .Where(e => !e.AllDay && e.Busy && e.End > now && e.Start < to && e.End > from && CalendarService.SourceOf(e)?.PlanAround != false)
            .GroupBy(e => (e.Title, e.Start)).Select(g => g.OrderByDescending(e => e.JoinUrl.Length).First()) // the same meeting in two calendars: once
            .OrderBy(e => e.Start).ToList();
    }

    public static List<CalendarEvent> On(DateTime day) => Between(day.Date, day.Date.AddDays(1));

    /// <summary>"30 min", "1 hr", "1 hr 15 min".</summary>
    public static string Length(CalendarEvent e)
    {
        int m = (int)Math.Round((e.End - e.Start).TotalMinutes);
        return m < 60 ? $"{m} min" : m % 60 == 0 ? $"{m / 60} hr" : $"{m / 60} hr {m % 60} min";
    }

    /// <summary>"9:00 AM · Weekly sync · 30 min".</summary>
    public static string Label(CalendarEvent e) => $"{e.Start:t} · {e.Title} · {Length(e)}";

    /// <summary>A #meeting task whose time is over: an hour after its reminder time, or its day has passed.</summary>
    public static bool IsOver(BinTask t) =>
        t.IsMeetingTask && (t.RemindAt is DateTime at ? at.AddHours(1) < DateTime.Now : t.Due is DateTime d && d.Date < DateTime.Today);
}
