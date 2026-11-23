using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>The Calendar's "Go to date" box: type a date in any form and press Enter to jump there.</summary>
public partial class MainWindow
{
    private DateTime? _calGoneTo;

    private void CalGoBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        CalGoHint.Visibility = CalGoBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CalGoBox.ClearValue(ForegroundProperty);
    }

    private void CalGoBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (CalGoBox.Text.Trim().Length == 0) CalGoBox.Text = "";
    }

    private void CalGoBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CalGoBox.Text = ""; Keyboard.ClearFocus(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (!DateGuess.TryParse(CalGoBox.Text, DateTime.Today, out var date))
        {
            CalGoBox.Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0x45, 0x3A));
            CalGoBox.ToolTip = "Couldn't read that as a date. Try 12/25, Dec 25 2027, 2027-01-04, next Friday or in 3 weeks.";
            return;
        }
        CalGoBox.ToolTip = $"Went to {date:dddd, MMMM d, yyyy}. Type another date and press Enter.";
        _calDate = date;
        _calGoneTo = date;
        CalGoBox.Text = "";
        RefreshCalendarTab();
        if (CalDayView.IsChecked == true) CalDateText.Text = date.ToString("dddd, MMMM d, yyyy");
        else CalDateText.Text += date.Year != DateTime.Today.Year ? $", {date.Year}" : "";
    }
}

/// <summary>Reads a date typed in almost any form: 12/25, 25 Dec, December 25 2027, 2027-01-04, the 5th,
/// today, tomorrow, Friday, next Friday, last Monday, in 3 weeks, 2 days ago, +10, Christmas, Easter, Thanksgiving.</summary>
public static class DateGuess
{
    private static readonly string[] Months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
    private static readonly string[] Days = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

    public static bool TryParse(string? input, DateTime today, out DateTime date)
    {
        date = today;
        string t = (input ?? "").Trim().ToLowerInvariant();
        t = Regex.Replace(t, @"^(on|go to|goto|the)\s+", "");
        t = Regex.Replace(t, @"\b(\d{1,2})(st|nd|rd|th)\b", "$1");   // 25th → 25
        t = Regex.Replace(t, @"\bof\b", " ").Replace(",", " ").Replace("sept", "sep");
        t = Regex.Replace(t, @"\s+", " ").Trim().TrimEnd('.', '!', '?');
        if (t.Length == 0) return false;

        switch (t)
        {
            case "today": case "now": date = today; return true;
            case "tomorrow": case "tmrw": case "tmr": date = today.AddDays(1); return true;
            case "yesterday": date = today.AddDays(-1); return true;
            case "next week": date = today.AddDays(7); return true;
            case "last week": date = today.AddDays(-7); return true;
            case "next month": date = today.AddMonths(1); return true;
            case "last month": date = today.AddMonths(-1); return true;
            case "next year": date = today.AddYears(1); return true;
        }

        // In 3 days / 2 weeks / 1 month, 3 weeks from now, 2 days ago, +10, -3
        var rel = Regex.Match(t, @"^(?:in\s+)?([+-]?\d{1,4})\s*(d|day|days|w|wk|wks|week|weeks|m|mo|month|months|y|yr|year|years)?(\s+(from now|later|ago))?$");
        if (rel.Success && (t.StartsWith("in ") || t.StartsWith("+") || t.StartsWith("-") || rel.Groups[3].Success || (rel.Groups[2].Success && !t.All(char.IsDigit))))
        {
            int n = int.Parse(rel.Groups[1].Value, CultureInfo.InvariantCulture);
            if (rel.Groups[4].Value == "ago") n = -n;
            string unit = rel.Groups[2].Value;
            date = unit.StartsWith("w") ? today.AddDays(7 * n)
                 : unit.StartsWith("m") ? today.AddMonths(n)
                 : unit.StartsWith("y") ? today.AddYears(n)
                 : today.AddDays(n);
            return true;
        }

        // Friday, this Friday, next Friday, last Friday
        var wd = Regex.Match(t, @"^(this |next |last |coming )?([a-z]{3,9})$");
        if (wd.Success && DayIndex(wd.Groups[2].Value) is int dow)
        {
            string which = wd.Groups[1].Value.Trim();
            int ahead = (dow - (int)today.DayOfWeek + 7) % 7;
            if (which == "last") { int back = ((int)today.DayOfWeek - dow + 7) % 7; date = today.AddDays(-(back == 0 ? 7 : back)); return true; }
            if (which == "this") { int sinceMonday = ((int)today.DayOfWeek + 6) % 7; date = today.AddDays(-sinceMonday + (dow + 6) % 7); return true; }
            if (which == "next" && ahead == 0) ahead = 7;
            date = today.AddDays(ahead);
            return true;
        }

        // Holidays
        int hy = today.Year;
        var hol = Regex.Match(t, @"^(christmas|xmas|christmas eve|new year'?s?( day| eve)?|easter|thanksgiving|independence day|july 4|4th of july|valentine'?s?( day)?|halloween)(\s+(\d{4}))?$");
        if (hol.Success)
        {
            if (hol.Groups[4].Success) hy = int.Parse(hol.Groups[4].Value, CultureInfo.InvariantCulture);
            DateTime For(int y) => hol.Groups[1].Value switch
            {
                "christmas eve" => new DateTime(y, 12, 24),
                var h when h.StartsWith("christmas") || h == "xmas" => new DateTime(y, 12, 25),
                var h when h.StartsWith("new year") && h.EndsWith("eve") => new DateTime(y, 12, 31),
                var h when h.StartsWith("new year") => new DateTime(y, 1, 1),
                "easter" => Easter(y),
                "thanksgiving" => NthWeekday(y, 11, DayOfWeek.Thursday, 4),
                var h when h.StartsWith("valentine") => new DateTime(y, 2, 14),
                "halloween" => new DateTime(y, 10, 31),
                _ => new DateTime(y, 7, 4)
            };
            date = For(hy);
            if (!hol.Groups[4].Success && date < today.AddDays(-14)) date = For(hy + 1); // the next one
            return true;
        }

        // The 5th (this month, or next month if it's passed)
        var dayOnly = Regex.Match(t, @"^(\d{1,2})$");
        if (dayOnly.Success)
        {
            int d = int.Parse(dayOnly.Value, CultureInfo.InvariantCulture);
            if (d < 1 || d > 31) return false;
            var m = new DateTime(today.Year, today.Month, 1);
            if (d < today.Day) m = m.AddMonths(1);
            date = new DateTime(m.Year, m.Month, Math.Min(d, DateTime.DaysInMonth(m.Year, m.Month)));
            return true;
        }

        // Month names: Dec 25, 25 Dec, December 25 2027, Dec 2027, December
        var words = t.Split(' ');
        int month = -1, day = -1, year = -1;
        foreach (var w in words)
        {
            if (MonthIndex(w) is int mi && month < 0) month = mi;
            else if (int.TryParse(w.TrimStart('\''), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                if (w.Length == 4 || n > 31) year = n < 100 ? 2000 + n : n;
                else if (day < 0) day = n;
                else if (year < 0) year = 2000 + n;
            }
            else if (DayIndex(w) == null) { month = -2; break; } // a word that isn't a date: not this form
        }
        if (month >= 0)
        {
            bool hasYear = year > 0;
            int y = hasYear ? year : today.Year;
            if (day < 0) day = 1;
            if (y < 1 || y > 9999 || day > DateTime.DaysInMonth(y, month + 1)) return false;
            date = new DateTime(y, month + 1, day);
            if (!hasYear) date = Nearest(date, today);
            return true;
        }

        // Numbers: 12/25, 12/25/27, 2027-01-04, 25.12.2027, and anything else Windows can read in your date format
        bool yearGiven = Regex.IsMatch(t, @"\d{4}") || Regex.IsMatch(t, @"^\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2}$");
        foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
        {
            if (DateTime.TryParse(t, culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out var parsed) && parsed.Year > 1)
            {
                if (yearGiven) { date = parsed.Date; return true; }
                if (!TryMake(today.Year, parsed.Month, parsed.Day, out var thisYear) && !TryMake(today.Year + 1, parsed.Month, parsed.Day, out thisYear)) return false;
                date = Nearest(thisYear, today);
                return true;
            }
        }
        var iso = Regex.Match(t, @"^(\d{4})[/.\-](\d{1,2})[/.\-](\d{1,2})$");
        if (iso.Success && TryMake(int.Parse(iso.Groups[1].Value), int.Parse(iso.Groups[2].Value), int.Parse(iso.Groups[3].Value), out date)) return true;
        return false;
    }

    /// <summary>A date typed without a year: this year's, unless that's more than half a year away (then the nearer one).</summary>
    private static DateTime Nearest(DateTime d, DateTime today)
    {
        if ((d - today).TotalDays > 183 && TryMake(d.Year - 1, d.Month, d.Day, out var prev)) return prev;
        if ((today - d).TotalDays > 183 && TryMake(d.Year + 1, d.Month, d.Day, out var next)) return next;
        return d;
    }

    private static bool TryMake(int y, int m, int d, out DateTime date)
    {
        date = default;
        if (y < 1 || y > 9999 || m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateTime(y, m, d);
        return true;
    }

    private static int? MonthIndex(string w) => w.Length >= 3 && Array.IndexOf(Months, w[..3]) is int i && i >= 0 &&
        ("january february march april may june july august september october november december".Split(' ')[i].StartsWith(w) || w.Length == 3) ? i : null;

    private static int? DayIndex(string w) => w.Length >= 3 && Array.IndexOf(Days, w[..3]) is int i && i >= 0 &&
        ("sunday monday tuesday wednesday thursday friday saturday".Split(' ')[i].StartsWith(w) || w is "tues" or "thur" or "thurs" or "weds") ? i : null;

    private static DateTime NthWeekday(int y, int m, DayOfWeek dow, int n)
    {
        var d = new DateTime(y, m, 1);
        while (d.DayOfWeek != dow) d = d.AddDays(1);
        return d.AddDays(7 * (n - 1));
    }

    /// <summary>Western Easter Sunday (the anonymous Gregorian method).</summary>
    private static DateTime Easter(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3,
            h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 114;
        return new DateTime(y, (h + l - 7 * m + 114) / 31, (h + l - 7 * m + 114) % 31 + 1);
    }
}
