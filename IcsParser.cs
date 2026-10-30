using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FocusApp;

/// <summary>One calendar event occurrence, in local time.</summary>
public class CalendarEvent
{
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool AllDay { get; set; }

    /// <summary>False for events marked "free" (they're shown, but plans don't work around them).</summary>
    public bool Busy { get; set; } = true;
    public string Source { get; set; } = "";
}

/// <summary>
/// Reads calendar files in the standard .ics format (used by Outlook and Google Calendar),
/// including repeating events, exceptions and time zones. Plain text parsing only.
/// </summary>
public static class IcsParser
{
    private sealed class Prop
    {
        public string Name = "";
        public Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
        public string Value = "";
    }

    private sealed class RawEvent
    {
        public string Uid = "";
        public string Title = "";
        public Prop? Start, End, RecurrenceId;
        public string? Duration, RRule, Status, Transp, BusyStatus;
        public List<Prop> ExDates = new();
    }

    /// <summary>All event occurrences that overlap [from, to), in local time.</summary>
    public static List<CalendarEvent> Parse(string ics, DateTime from, DateTime to, string sourceName = "")
    {
        var events = ReadEvents(ics);
        var result = new List<CalendarEvent>();

        // Moved or changed single occurrences of a repeating event ("exceptions").
        var overrides = events.Where(e => e.RecurrenceId != null)
            .GroupBy(e => e.Uid)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var ev in events.Where(e => e.RecurrenceId == null))
        {
            try
            {
                if (ev.Start == null) continue;
                bool allDay = IsDateOnly(ev.Start);
                var startUtc = ToUtc(ev.Start);
                var length = Length(ev, startUtc);

                IEnumerable<DateTime> starts = ev.RRule == null
                    ? new[] { startUtc }
                    : Recurrence.Expand(ev.RRule, ev.Start, startUtc, to.ToUniversalTime());

                var excluded = new HashSet<DateTime>(ev.ExDates.SelectMany(ExpandDateList).Select(ToUtc));
                var moved = overrides.TryGetValue(ev.Uid, out var list) ? list : new List<RawEvent>();
                var replaced = new HashSet<DateTime>(moved.Select(m => ToUtc(m.RecurrenceId!)));

                foreach (var s in starts)
                {
                    if (excluded.Contains(s) || replaced.Contains(s)) continue;
                    Add(result, ev, s, s + length, allDay, from, to, sourceName);
                }
            }
            catch
            {
                // Skip one broken event rather than losing the whole calendar.
            }
        }

        // The exceptions themselves (each is a full event with its own time).
        foreach (var ev in overrides.Values.SelectMany(v => v))
        {
            try
            {
                if (ev.Start == null) continue;
                var s = ToUtc(ev.Start);
                Add(result, ev, s, s + Length(ev, s), IsDateOnly(ev.Start), from, to, sourceName);
            }
            catch { }
        }

        return result.OrderBy(e => e.Start).ThenBy(e => e.End).ToList();
    }

    private static void Add(List<CalendarEvent> result, RawEvent ev, DateTime startUtc, DateTime endUtc,
        bool allDay, DateTime from, DateTime to, string source)
    {
        if (string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) return;
        if (ev.Title.StartsWith("Canceled:", StringComparison.OrdinalIgnoreCase) ||
            ev.Title.StartsWith("Cancelled:", StringComparison.OrdinalIgnoreCase)) return;

        DateTime start, end;
        if (allDay)
        {
            // All-day events are dates, not instants: keep them on the same calendar days.
            start = DateTime.SpecifyKind(startUtc, DateTimeKind.Local);
            end = DateTime.SpecifyKind(endUtc, DateTimeKind.Local);
        }
        else
        {
            start = startUtc.ToLocalTime();
            end = endUtc.ToLocalTime();
        }
        if (end <= start) end = start.AddMinutes(allDay ? 1440 : 1);
        if (end <= from || start >= to) return;

        bool free = string.Equals(ev.Transp, "TRANSPARENT", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ev.BusyStatus, "FREE", StringComparison.OrdinalIgnoreCase);
        result.Add(new CalendarEvent
        {
            Title = string.IsNullOrWhiteSpace(ev.Title) ? "(No title)" : ev.Title,
            Start = start,
            End = end,
            AllDay = allDay,
            Busy = !free,
            Source = source
        });
    }

    private static TimeSpan Length(RawEvent ev, DateTime startUtc)
    {
        if (ev.End != null)
        {
            var len = ToUtc(ev.End) - startUtc;
            if (len > TimeSpan.Zero) return len;
        }
        if (ev.Duration != null && TryParseDuration(ev.Duration, out var d)) return d;
        return ev.Start != null && IsDateOnly(ev.Start) ? TimeSpan.FromDays(1) : TimeSpan.Zero;
    }

    // ---------- Reading the file ----------

    private static List<RawEvent> ReadEvents(string ics)
    {
        var events = new List<RawEvent>();
        RawEvent? current = null;
        int depth = 0; // ignore things nested inside an event, like reminders (VALARM)

        foreach (var line in Unfold(ics))
        {
            if (line.Length == 0) continue;
            var prop = ParseLine(line);
            if (prop == null) continue;

            if (prop.Name == "BEGIN")
            {
                if (current != null) depth++;
                else if (prop.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) current = new RawEvent();
                continue;
            }
            if (prop.Name == "END")
            {
                if (current == null) continue;
                if (depth > 0) { depth--; continue; }
                if (prop.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(current);
                    current = null;
                }
                continue;
            }
            if (current == null || depth > 0) continue;

            switch (prop.Name)
            {
                case "UID": current.Uid = prop.Value; break;
                case "SUMMARY": current.Title = Unescape(prop.Value); break;
                case "DTSTART": current.Start = prop; break;
                case "DTEND": current.End = prop; break;
                case "DURATION": current.Duration = prop.Value; break;
                case "RRULE": current.RRule = prop.Value; break;
                case "EXDATE": current.ExDates.Add(prop); break;
                case "RECURRENCE-ID": current.RecurrenceId = prop; break;
                case "STATUS": current.Status = prop.Value; break;
                case "TRANSP": current.Transp = prop.Value; break;
                case "X-MICROSOFT-CDO-BUSYSTATUS": current.BusyStatus = prop.Value; break;
            }
        }
        return events;
    }

    /// <summary>Joins long lines that the format splits with a leading space.</summary>
    private static IEnumerable<string> Unfold(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                sb.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear();
            sb.Append(raw);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static Prop? ParseLine(string line)
    {
        // NAME;PARAM=value;PARAM="quoted:value":VALUE
        int i = 0;
        bool inQuotes = false;
        int colon = -1;
        for (; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ':' && !inQuotes) { colon = i; break; }
        }
        if (colon < 0) return null;

        string head = line[..colon];
        var prop = new Prop { Value = line[(colon + 1)..] };
        var parts = SplitOutsideQuotes(head, ';');
        prop.Name = parts[0].Trim().ToUpperInvariant();
        foreach (var p in parts.Skip(1))
        {
            int eq = p.IndexOf('=');
            if (eq > 0) prop.Params[p[..eq].Trim()] = p[(eq + 1)..].Trim().Trim('"');
        }
        return prop;
    }

    private static List<string> SplitOutsideQuotes(string s, char separator)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in s)
        {
            if (c == '"') inQuotes = !inQuotes;
            if (c == separator && !inQuotes)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    // ---------- Dates and times ----------

    private static bool IsDateOnly(Prop p) =>
        (p.Params.TryGetValue("VALUE", out var v) && v.Equals("DATE", StringComparison.OrdinalIgnoreCase)) ||
        p.Value.Trim().Length == 8;

    private static IEnumerable<Prop> ExpandDateList(Prop p) =>
        p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => new Prop { Name = p.Name, Params = p.Params, Value = v.Trim() });

    /// <summary>Converts a date/time property to UTC (all-day dates stay as plain dates).</summary>
    private static DateTime ToUtc(Prop p) => ToUtc(p.Value, p.Params.GetValueOrDefault("TZID"), IsDateOnly(p));

    internal static DateTime ToUtc(string value, string? tzid, bool dateOnly)
    {
        value = value.Trim();
        if (dateOnly || value.Length == 8)
        {
            var date = DateTime.ParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture);
            return DateTime.SpecifyKind(date, DateTimeKind.Utc);
        }

        bool utc = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        var local = DateTime.ParseExact(value.TrimEnd('Z', 'z')[..15], "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        if (utc) return DateTime.SpecifyKind(local, DateTimeKind.Utc);

        var zone = FindZone(tzid);
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1); // skipped by a clock change
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    private static readonly Dictionary<string, TimeZoneInfo> ZoneCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds a time zone by Windows name ("Eastern Standard Time"), IANA name ("America/New_York"),
    /// or Outlook's display name ("(UTC-05:00) Eastern Time (US &amp; Canada)"). Falls back to this PC's zone.
    /// </summary>
    internal static TimeZoneInfo FindZone(string? tzid)
    {
        if (string.IsNullOrWhiteSpace(tzid)) return TimeZoneInfo.Local;
        lock (ZoneCache)
        {
            if (ZoneCache.TryGetValue(tzid, out var cached)) return cached;
            var zone = LookUpZone(tzid.Trim().Trim('"', '/'));
            ZoneCache[tzid] = zone;
            return zone;
        }
    }

    private static TimeZoneInfo LookUpZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win)) return TimeZoneInfo.FindSystemTimeZoneById(win);
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) return TimeZoneInfo.FindSystemTimeZoneById(iana);
        }
        catch { }
        foreach (var z in TimeZoneInfo.GetSystemTimeZones())
            if (string.Equals(z.DisplayName, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(z.StandardName, id, StringComparison.OrdinalIgnoreCase))
                return z;
        return TimeZoneInfo.Local;
    }

    internal static bool TryParseDuration(string text, out TimeSpan duration)
    {
        // Like P1D, PT1H30M, -PT15M, P2W
        duration = TimeSpan.Zero;
        text = text.Trim().ToUpperInvariant();
        bool negative = text.StartsWith("-");
        text = text.TrimStart('+', '-');
        if (!text.StartsWith("P")) return false;
        int number = 0;
        bool any = false;
        foreach (char c in text[1..])
        {
            if (char.IsDigit(c)) { number = number * 10 + (c - '0'); continue; }
            switch (c)
            {
                case 'W': duration += TimeSpan.FromDays(7 * number); break;
                case 'D': duration += TimeSpan.FromDays(number); break;
                case 'H': duration += TimeSpan.FromHours(number); break;
                case 'M': duration += TimeSpan.FromMinutes(number); break;
                case 'S': duration += TimeSpan.FromSeconds(number); break;
                case 'T': continue;
                default: return false;
            }
            number = 0;
            any = true;
        }
        if (negative) duration = -duration;
        return any;
    }

    // ---------- Repeating events ----------

    /// <summary>Expands a repeat rule (RRULE). Works in the event's own time zone so clock changes are handled.</summary>
    private static class Recurrence
    {
        private static readonly string[] DayCodes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

        public static IEnumerable<DateTime> Expand(string rule, Prop dtStart, DateTime startUtc, DateTime untilUtcWindow)
        {
            var r = rule.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

            string freq = r.GetValueOrDefault("FREQ", "").ToUpperInvariant();
            int interval = int.TryParse(r.GetValueOrDefault("INTERVAL"), out var iv) && iv > 0 ? iv : 1;
            int? count = int.TryParse(r.GetValueOrDefault("COUNT"), out var c) && c > 0 ? c : null;
            DateTime? until = null;
            if (r.TryGetValue("UNTIL", out var u) && u.Length >= 8)
            {
                until = u.Length == 8
                    ? DateTime.SpecifyKind(DateTime.ParseExact(u, "yyyyMMdd", CultureInfo.InvariantCulture).AddDays(1).AddTicks(-1), DateTimeKind.Utc)
                    : ToUtc(u, dtStart.Params.GetValueOrDefault("TZID"), false);
            }

            var byDay = ParseByDay(r.GetValueOrDefault("BYDAY"));
            var byMonthDay = ParseInts(r.GetValueOrDefault("BYMONTHDAY"));
            var byMonth = ParseInts(r.GetValueOrDefault("BYMONTH"));
            var bySetPos = ParseInts(r.GetValueOrDefault("BYSETPOS"));

            bool allDay = IsDateOnly(dtStart);
            var zone = allDay ? TimeZoneInfo.Utc : FindZone(dtStart.Params.GetValueOrDefault("TZID"));
            bool utcStart = dtStart.Value.Trim().EndsWith("Z", StringComparison.OrdinalIgnoreCase);
            if (utcStart) zone = TimeZoneInfo.Utc;

            // The first occurrence's wall-clock time in its own zone.
            var first = allDay ? startUtc : TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone);
            var timeOfDay = first.TimeOfDay;

            int produced = 0;
            const int maxPeriods = 20000; // safety limit (more than 50 years of daily events)
            for (int period = 0; period < maxPeriods; period++)
            {
                List<DateTime> days = freq switch
                {
                    "DAILY" => Daily(first, period * interval, byDay, byMonth),
                    "WEEKLY" => Weekly(first, period * interval, byDay),
                    "MONTHLY" => Monthly(first, period * interval, byDay, byMonthDay, bySetPos),
                    "YEARLY" => Yearly(first, period * interval, byDay, byMonthDay, byMonth),
                    _ => period == 0 ? new List<DateTime> { first.Date } : new List<DateTime>()
                };
                if (freq is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY") && period > 0) yield break;

                bool pastWindow = false;
                foreach (var day in days.OrderBy(d => d))
                {
                    var wall = day.Date + timeOfDay;
                    if (wall < first) continue;
                    DateTime occUtc;
                    if (allDay) occUtc = DateTime.SpecifyKind(wall, DateTimeKind.Utc);
                    else
                    {
                        var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
                        if (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
                        occUtc = TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
                    }

                    if (until != null && occUtc > until) yield break;
                    if (count != null && produced >= count) yield break;
                    if (occUtc > untilUtcWindow) { pastWindow = true; break; }
                    produced++;
                    yield return occUtc;
                }
                if (pastWindow) yield break;
            }
        }

        private static List<(int Ordinal, DayOfWeek Day)> ParseByDay(string? text)
        {
            var result = new List<(int, DayOfWeek)>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim().ToUpperInvariant();
                if (p.Length < 2) continue;
                string code = p[^2..];
                int index = Array.IndexOf(DayCodes, code);
                if (index < 0) continue;
                int ordinal = p.Length > 2 && int.TryParse(p[..^2], out var o) ? o : 0;
                result.Add((ordinal, (DayOfWeek)index));
            }
            return result;
        }

        private static List<int> ParseInts(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? new List<int>()
                : text.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => int.TryParse(t.Trim(), out var n) ? n : 0).Where(n => n != 0).ToList();

        private static List<DateTime> Daily(DateTime first, int offset, List<(int Ordinal, DayOfWeek Day)> byDay, List<int> byMonth)
        {
            var day = first.Date.AddDays(offset);
            if (byDay.Count > 0 && !byDay.Any(b => b.Day == day.DayOfWeek)) return new();
            if (byMonth.Count > 0 && !byMonth.Contains(day.Month)) return new();
            return new() { day };
        }

        private static List<DateTime> Weekly(DateTime first, int offset, List<(int Ordinal, DayOfWeek Day)> byDay)
        {
            // Weeks start on Monday (the standard default).
            int sinceMonday = ((int)first.DayOfWeek + 6) % 7;
            var weekStart = first.Date.AddDays(-sinceMonday).AddDays(7 * offset);
            var days = byDay.Count == 0 ? new List<DayOfWeek> { first.DayOfWeek } : byDay.Select(b => b.Day).ToList();
            return days.Select(d => weekStart.AddDays(((int)d + 6) % 7)).ToList();
        }

        private static List<DateTime> Monthly(DateTime first, int offset, List<(int Ordinal, DayOfWeek Day)> byDay,
            List<int> byMonthDay, List<int> bySetPos)
        {
            var month = new DateTime(first.Year, first.Month, 1).AddMonths(offset);
            var days = DaysInMonth(month, byDay, byMonthDay, first.Day);
            return ApplySetPos(days, bySetPos);
        }

        private static List<DateTime> Yearly(DateTime first, int offset, List<(int Ordinal, DayOfWeek Day)> byDay,
            List<int> byMonthDay, List<int> byMonth)
        {
            int year = first.Year + offset;
            var months = byMonth.Count > 0 ? byMonth : new List<int> { first.Month };
            var result = new List<DateTime>();
            foreach (var m in months.Where(m => m >= 1 && m <= 12))
                result.AddRange(DaysInMonth(new DateTime(year, m, 1), byDay, byMonthDay, first.Day));
            return result;
        }

        private static List<DateTime> DaysInMonth(DateTime month, List<(int Ordinal, DayOfWeek Day)> byDay,
            List<int> byMonthDay, int defaultDay)
        {
            int length = DateTime.DaysInMonth(month.Year, month.Month);
            var result = new List<DateTime>();

            if (byMonthDay.Count > 0)
            {
                foreach (var d in byMonthDay)
                {
                    int day = d > 0 ? d : length + d + 1;
                    if (day >= 1 && day <= length) result.Add(month.AddDays(day - 1));
                }
            }
            else if (byDay.Count > 0)
            {
                foreach (var (ordinal, dow) in byDay)
                {
                    var matches = Enumerable.Range(0, length).Select(i => month.AddDays(i)).Where(d => d.DayOfWeek == dow).ToList();
                    if (ordinal == 0) result.AddRange(matches);
                    else
                    {
                        int index = ordinal > 0 ? ordinal - 1 : matches.Count + ordinal;
                        if (index >= 0 && index < matches.Count) result.Add(matches[index]);
                    }
                }
            }
            else if (defaultDay <= length)
            {
                result.Add(month.AddDays(defaultDay - 1));
            }
            return result.Distinct().OrderBy(d => d).ToList();
        }

        private static List<DateTime> ApplySetPos(List<DateTime> days, List<int> bySetPos)
        {
            if (bySetPos.Count == 0) return days;
            var result = new List<DateTime>();
            foreach (var p in bySetPos)
            {
                int index = p > 0 ? p - 1 : days.Count + p;
                if (index >= 0 && index < days.Count) result.Add(days[index]);
            }
            return result;
        }
    }
}
