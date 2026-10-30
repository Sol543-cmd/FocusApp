using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FocusApp;

/// <summary>
/// A repeating schedule, like "every Friday", "weekdays", "every 2 weeks on Monday",
/// "monthly on the 15th", "every last Friday" or "yearly". Stored as the plain words you typed
/// (so it reads the same forever), and worked out when needed.
/// </summary>
public sealed class RepeatRule
{
    public enum Kind { Days, Weeks, MonthDay, MonthWeekday, MonthLastWeekday, Years }

    public Kind Type { get; private set; }
    public int Interval { get; private set; } = 1;
    public List<DayOfWeek> Days { get; } = new();
    public int MonthDay { get; private set; }          // 1-31, or -1 for the last day
    public int Ordinal { get; private set; }           // 1-4, or -1 for "last"
    public DayOfWeek Weekday { get; private set; }
    public bool WeekdaysOnly { get; private set; }

    private static readonly string[] OrdinalWords = { "first", "second", "third", "fourth" };

    /// <summary>Reads a schedule. Returns null if the words aren't understood.</summary>
    public static RepeatRule? Parse(string? text, DateTime? anchor = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var words = Words(text);
        return TryParse(words, 0, out var rule, out int used, anchor ?? DateTime.Today) && used == words.Count ? rule : null;
    }

    internal static List<string> Words(string text) =>
        text.ToLowerInvariant().Replace(",", " , ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Tries to read a schedule starting at words[start]. "every"/"each" or a word like "daily" must come first.</summary>
    internal static bool TryParse(List<string> words, int start, out RepeatRule rule, out int used, DateTime anchor)
    {
        rule = new RepeatRule();
        used = 0;
        int i = start;
        string W(int k) => k < words.Count ? words[k] : "";

        switch (W(i))
        {
            case "daily": rule.Type = Kind.Days; used = 1; return true;
            case "weekdays": case "workdays": rule.Type = Kind.Days; rule.WeekdaysOnly = true; used = 1; return true;
            case "weekly": rule.Type = Kind.Weeks; rule.Days.Add(anchor.DayOfWeek); i++; break;
            case "biweekly": rule.Type = Kind.Weeks; rule.Interval = 2; rule.Days.Add(anchor.DayOfWeek); i++; break;
            case "monthly": rule.Type = Kind.MonthDay; rule.MonthDay = anchor.Day; i++; break;
            case "yearly": case "annually": rule.Type = Kind.Years; used = 1; return true;
            case "every": case "each": i++; break;
            default: return false;
        }

        if (W(start) is "every" or "each")
        {
            // A number or "other": every 2 weeks, every other day.
            if (W(i) == "other") { rule.Interval = 2; i++; }
            else if (int.TryParse(W(i), out int n) && n is > 0 and < 100) { rule.Interval = n; i++; }

            string unit = W(i);
            if (unit is "day" or "days") { rule.Type = Kind.Days; i++; }
            else if (unit is "weekday" or "weekdays" or "workday" or "workdays") { rule.Type = Kind.Days; rule.WeekdaysOnly = true; i++; }
            else if (unit is "week" or "weeks") { rule.Type = Kind.Weeks; i++; rule.Days.Add(anchor.DayOfWeek); }
            else if (unit is "month" or "months") { rule.Type = Kind.MonthDay; rule.MonthDay = anchor.Day; i++; }
            else if (unit is "year" or "years") { rule.Type = Kind.Years; i++; }
            else if (OrdinalIndex(unit) is int ord && W(i + 1) is var after)
            {
                // every first Monday / every last Friday / every last weekday / every last day
                if (DateWords.Weekday(after) is DayOfWeek dow) { rule.Type = Kind.MonthWeekday; rule.Ordinal = ord; rule.Weekday = dow; i += 2; }
                else if (ord == -1 && after is "weekday" or "workday") { rule.Type = Kind.MonthLastWeekday; i += 2; }
                else if (ord == -1 && after == "day") { rule.Type = Kind.MonthDay; rule.MonthDay = -1; i += 2; }
                else return false;
                used = i - start;
                return true;
            }
            else if (DayNumber(unit) is int dayNumber) { rule.Type = Kind.MonthDay; rule.MonthDay = dayNumber; i++; used = i - start; return true; }
            else if (DateWords.Weekday(unit) is not null) { rule.Type = Kind.Weeks; }
            else return false;
        }

        // Optional "on ..." details: on Monday and Friday / on the 15th / on the last Friday
        if (rule.Type == Kind.Weeks)
        {
            int j = W(i) == "on" ? i + 1 : i;
            var days = new List<DayOfWeek>();
            while (DateWords.Weekday(W(j)) is DayOfWeek d)
            {
                days.Add(d);
                j++;
                if (W(j) is "," or "and" && DateWords.Weekday(W(j + 1)) is not null) j++;
            }
            if (days.Count > 0) { rule.Days.Clear(); rule.Days.AddRange(days.Distinct()); i = j; }
        }
        else if (rule.Type == Kind.MonthDay && W(i) == "on")
        {
            int j = i + 1;
            if (W(j) == "the") j++;
            if (DayNumber(W(j)) is int dn) { rule.MonthDay = dn; i = j + 1; }
            else if (OrdinalIndex(W(j)) is int ord)
            {
                if (DateWords.Weekday(W(j + 1)) is DayOfWeek dow) { rule.Type = Kind.MonthWeekday; rule.Ordinal = ord; rule.Weekday = dow; i = j + 2; }
                else if (ord == -1 && W(j + 1) is "weekday" or "workday") { rule.Type = Kind.MonthLastWeekday; i = j + 2; }
                else if (ord == -1 && W(j + 1) == "day") { rule.MonthDay = -1; i = j + 2; }
            }
        }

        used = i - start;
        return true;
    }

    private static int? OrdinalIndex(string word)
    {
        if (word == "last") return -1;
        int i = Array.IndexOf(OrdinalWords, word);
        if (i >= 0) return i + 1;
        return word switch { "1st" => 1, "2nd" => 2, "3rd" => 3, "4th" => 4, _ => null };
    }

    /// <summary>"15th", "1st", "22nd", "15" as a day of the month.</summary>
    private static int? DayNumber(string word)
    {
        string digits = new string(word.TakeWhile(char.IsDigit).ToArray());
        string rest = word[digits.Length..];
        if (digits.Length == 0 || (rest.Length > 0 && rest is not ("st" or "nd" or "rd" or "th"))) return null;
        int n = int.Parse(digits, CultureInfo.InvariantCulture);
        return n is >= 1 and <= 31 ? n : null;
    }

    /// <summary>The first date strictly after <paramref name="after"/> that this schedule lands on.</summary>
    public DateTime Next(DateTime after)
    {
        var d = after.Date;
        switch (Type)
        {
            case Kind.Days when WeekdaysOnly:
                do d = d.AddDays(1); while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
                return d;
            case Kind.Days:
                return d.AddDays(Interval);
            case Kind.Weeks:
            {
                // Next listed weekday; when the week's list runs out, skip ahead by the interval.
                var days = Days.Count > 0 ? Days : new List<DayOfWeek> { after.DayOfWeek };
                for (int k = 1; k <= 7; k++)
                {
                    var c = d.AddDays(k);
                    bool sameWeek = StartOfWeek(c) == StartOfWeek(d);
                    if (days.Contains(c.DayOfWeek) && (sameWeek || Interval == 1)) return c;
                    if (!sameWeek) break;
                }
                var weekStart = StartOfWeek(d).AddDays(7 * Interval);
                return Enumerable.Range(0, 7).Select(k => weekStart.AddDays(k)).First(c => days.Contains(c.DayOfWeek));
            }
            case Kind.MonthDay:
            {
                for (int m = 0; m <= 24; m++)
                {
                    var month = new DateTime(d.Year, d.Month, 1).AddMonths(m == 0 ? 0 : m);
                    if (m > 0 && (m % Interval) != 0) continue;
                    int dim = DateTime.DaysInMonth(month.Year, month.Month);
                    int day = MonthDay == -1 ? dim : Math.Min(MonthDay, dim);
                    var c = month.AddDays(day - 1);
                    if (c > d) return c;
                }
                return d.AddMonths(Interval);
            }
            case Kind.MonthWeekday:
            case Kind.MonthLastWeekday:
            {
                for (int m = 0; m <= 24; m++)
                {
                    if (m > 0 && (m % Interval) != 0) continue;
                    var month = new DateTime(d.Year, d.Month, 1).AddMonths(m);
                    var c = Type == Kind.MonthLastWeekday ? LastWeekday(month) : NthWeekday(month, Weekday, Ordinal);
                    if (c != null && c > d) return c.Value;
                }
                return d.AddMonths(Interval);
            }
            case Kind.Years:
            {
                var c = d.AddYears(Interval);
                return c;
            }
        }
        return d.AddDays(1);
    }

    private static DateTime StartOfWeek(DateTime d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    private static DateTime? NthWeekday(DateTime month, DayOfWeek day, int ordinal)
    {
        int dim = DateTime.DaysInMonth(month.Year, month.Month);
        var matches = Enumerable.Range(0, dim).Select(i => month.AddDays(i)).Where(x => x.DayOfWeek == day).ToList();
        if (ordinal == -1) return matches[^1];
        return ordinal <= matches.Count ? matches[ordinal - 1] : null;
    }

    private static DateTime LastWeekday(DateTime month)
    {
        var d = month.AddMonths(1).AddDays(-1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }

    private static string JoinDays(IEnumerable<DayOfWeek> days)
    {
        var names = days.OrderBy(x => ((int)x + 6) % 7).Select(x => x.ToString()).ToList();
        return names.Count <= 1 ? string.Join("", names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }

    /// <summary>A plain description, like "Every Monday and Friday" or "Every 2 weeks on Monday".</summary>
    public string Describe()
    {
        static string Ord(int o) => o == -1 ? "last" : OrdinalWords[o - 1];
        static string Suffix(int n) => n is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        string every = Interval == 1 ? "Every" : Interval == 2 ? "Every other" : $"Every {Interval}";
        string Plural(string unit) => Interval > 2 ? unit + "s" : unit;
        return Type switch
        {
            Kind.Days when WeekdaysOnly => "Every weekday",
            Kind.Days => Interval == 1 ? "Every day" : $"{every} {Plural("day")}",
            Kind.Weeks => (Interval == 1 ? "Every " : $"{every} {Plural("week")} on ") + JoinDays(Days),
            Kind.MonthDay => (Interval == 1 ? "Every month" : $"{every} {Plural("month")}") +
                             (MonthDay == -1 ? " on the last day" : $" on the {MonthDay}{Suffix(MonthDay)}"),
            Kind.MonthWeekday => (Interval == 1 ? "Every month" : $"{every} {Plural("month")}") + $" on the {Ord(Ordinal)} {Weekday}",
            Kind.MonthLastWeekday => "Every month on the last weekday",
            Kind.Years => Interval == 1 ? "Every year" : $"{every} {Plural("year")}",
            _ => "Repeats"
        };
    }
}

/// <summary>Reading dates and times from everyday words.</summary>
public static class DateWords
{
    private static readonly string[] Months =
        { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };

    public static DayOfWeek? Weekday(string word)
    {
        word = word.Trim(',', '.').ToLowerInvariant();
        if (word.Length < 3) return null;
        foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek)))
        {
            string name = d.ToString().ToLowerInvariant();
            // "fri", "friday", "fridays", "tues", "thurs"
            if (word == name || word == name + "s" || name.StartsWith(word)) return d;
        }
        return null;
    }

    private static int? Month(string word)
    {
        word = word.Trim(',', '.').ToLowerInvariant();
        if (word.Length < 3) return null;
        for (int i = 0; i < 12; i++)
            if (Months[i] == word || (Months[i].StartsWith(word) && word.Length >= 3)) return i + 1;
        return null;
    }

    /// <summary>
    /// Tries to read a date at words[start]: today, tomorrow, Friday, next Friday, next week,
    /// in 3 days, Oct 14, 14 October, 10/14, end of week, end of month. Returns how many words it used.
    /// </summary>
    /// <param name="inText">True when reading inside a task's text: "sat" and "sun" aren't read as days there.</param>
    public static DateTime? TryDate(List<string> words, int start, out int used, DateTime today, bool inText = false)
    {
        used = 0;
        string W(int k) => k < words.Count ? words[k].Trim(',', '.') : "";
        string w = W(start);

        switch (w)
        {
            case "today": case "tod": used = 1; return today;
            case "tonight": used = 1; return today;
            case "tomorrow": case "tmr": case "tom": used = 1; return today.AddDays(1);
            case "eow": used = 1; return EndOfWeek(today);
            case "eom": used = 1; return new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        }
        if (w == "end" && W(start + 1) == "of")
        {
            if (W(start + 2) == "week") { used = 3; return EndOfWeek(today); }
            if (W(start + 2) == "month") { used = 3; return new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)); }
        }
        if (w == "next")
        {
            if (W(start + 1) == "week") { used = 2; return today.AddDays(7 - ((int)today.DayOfWeek + 6) % 7); }
            if (W(start + 1) == "month") { used = 2; return new DateTime(today.Year, today.Month, 1).AddMonths(1); }
            if (Weekday(W(start + 1)) is DayOfWeek nd) { used = 2; return NextWeekday(today, nd, strictlyNextWeek: true); }
        }
        if (w == "in" && int.TryParse(W(start + 1), out int n) && n is > 0 and < 400)
        {
            string unit = W(start + 2);
            if (unit is "day" or "days") { used = 3; return today.AddDays(n); }
            if (unit is "week" or "weeks") { used = 3; return today.AddDays(7 * n); }
            if (unit is "month" or "months") { used = 3; return today.AddMonths(n); }
        }
        if (Weekday(w) is DayOfWeek d && !w.EndsWith("s") && !(inText && w is "sat" or "sun"))
        {
            used = 1;
            return NextWeekday(today, d, strictlyNextWeek: false);
        }

        // Oct 14 / October 14th / 14 Oct
        if (Month(w) is int m1 && DayOf(W(start + 1)) is int d1) { used = 2; return Upcoming(today, m1, d1); }
        if (DayOf(w) is int d2 && Month(W(start + 1)) is int m2) { used = 2; return Upcoming(today, m2, d2); }

        // 10/14 or 10/14/2027 (in this PC's date order)
        if (w.Count(c => c == '/') is 1 or 2 && w.Any(char.IsDigit) &&
            DateTime.TryParse(w, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed))
        {
            used = 1;
            return w.Count(c => c == '/') == 2 ? parsed.Date : Upcoming(today, parsed.Month, parsed.Day);
        }
        return null;
    }

    /// <summary>Tries to read a time at words[start]: 3pm, 3:30pm, 15:30, "at 3", "at noon".</summary>
    public static TimeSpan? TryTime(List<string> words, int start, out int used)
    {
        used = 0;
        string W(int k) => k < words.Count ? words[k].Trim(',', '.') : "";
        int i = start;
        bool at = W(i) == "at";
        if (at) i++;
        string w = W(i);
        if (w == "noon") { used = i - start + 1; return new TimeSpan(12, 0, 0); }

        // Join "3 pm" into "3pm".
        string next = W(i + 1);
        bool spacedSuffix = next is "am" or "pm";
        string text = spacedSuffix ? w + next : w;
        bool pm = text.EndsWith("pm"), am = text.EndsWith("am");
        string core = pm || am ? text[..^2] : text;
        var parts = core.Split(':');
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out int h)) return null;
        int min = 0;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out min) || min is < 0 or > 59)) return null;
        // A bare number only counts as a time after "at" (so "call 3 people" stays as text).
        if (!pm && !am && parts.Length == 1 && !at) return null;
        if (pm && h < 12) h += 12;
        if (am && h == 12) h = 0;
        if (!pm && !am && parts.Length == 1 && h is >= 1 and <= 7) h += 12; // "at 3" means 3 PM
        if (h is < 0 or > 23) return null;
        used = i - start + (spacedSuffix ? 2 : 1);
        return new TimeSpan(h, min, 0);
    }

    private static int? DayOf(string word)
    {
        string digits = new string(word.TakeWhile(char.IsDigit).ToArray());
        string rest = word[digits.Length..];
        if (digits.Length == 0 || (rest.Length > 0 && rest is not ("st" or "nd" or "rd" or "th"))) return null;
        int n = int.Parse(digits, CultureInfo.InvariantCulture);
        return n is >= 1 and <= 31 ? n : null;
    }

    private static DateTime Upcoming(DateTime today, int month, int day)
    {
        int year = today.Year;
        day = Math.Min(day, DateTime.DaysInMonth(year, month));
        var date = new DateTime(year, month, day);
        // A date that has already passed (by more than a month) means next year.
        return date < today.AddDays(-30) ? new DateTime(year + 1, month, Math.Min(day, DateTime.DaysInMonth(year + 1, month))) : date;
    }

    private static DateTime NextWeekday(DateTime today, DayOfWeek day, bool strictlyNextWeek)
    {
        int ahead = ((int)day - (int)today.DayOfWeek + 7) % 7;
        if (strictlyNextWeek && ahead == 0) ahead = 7;
        return today.AddDays(ahead);
    }

    private static DateTime EndOfWeek(DateTime today) => today.AddDays(((int)DayOfWeek.Friday - (int)today.DayOfWeek + 7) % 7);
}
