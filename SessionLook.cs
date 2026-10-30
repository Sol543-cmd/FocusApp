using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// The "numeral badge" look for focus sessions (the floating timer and the dashboard): the session number as a
/// Roman numeral in a small serif ring, and the label in small spaced capitals. A session you named shows its name
/// in place of FOCUS SESSION; the numeral still counts sessions.
/// </summary>
public static class SessionLook
{
    /// <summary>Cormorant Garamond SemiBold (Fonts folder, SIL Open Font License), with Georgia if it can't load.</summary>
    public static readonly FontFamily NumeralFont = new(new Uri("pack://application:,,,/"), "./Fonts/#Focus Cormorant SemiBold, Georgia");

    public static string Roman(int n)
    {
        if (n <= 0) return "";
        if (n >= 4000) return n.ToString();
        var parts = new (int Value, string Text)[]
        {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        };
        var sb = new StringBuilder();
        foreach (var (value, text) in parts)
            while (n >= value) { sb.Append(text); n -= value; }
        return sb.ToString();
    }

    /// <summary>Capitals with a thin space between letters (WPF has no letter spacing) and a wider gap between words.</summary>
    public static string Spaced(string text)
    {
        var words = text.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\u2002", words.Select(w => string.Join("\u2009", w.Select(c => c.ToString()))));
    }

    private static readonly Regex DefaultName = new(@"^\s*focus(\s+session)?(\s+\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the session has a name of its own (not just "Focus session 3").</summary>
    public static bool IsNamed(string? name) => !string.IsNullOrWhiteSpace(name) && !DefaultName.IsMatch(name);

    /// <summary>The spaced label: the session's own name, or FOCUS SESSION.</summary>
    public static string Label(string? name) => Spaced(IsNamed(name) ? name!.Trim() : "Focus session");
}
