using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace FocusApp;

/// <summary>
/// Scripture references in text (like "John 3:16", "Rom 8:28-30", "1 Cor 13", "Gen 1:1–2:3") become links that open
/// the passage in Logos Bible Software (logosref: links; if Logos isn't installed, the passage opens at ref.ly).
/// Use <c>local:ScriptureLinks.Text="{Binding Text}"</c> on a TextBlock instead of Text.
/// </summary>
public static class ScriptureLinks
{
    // Logos book code, then the names and abbreviations people type (first letter capitalized; any case after).
    // Two-letter words that are also English ("Is", "Am", "So") are left out, so "Am 9:30" isn't Amos.
    private static readonly (string Code, string[] Names)[] Books =
    {
        ("Ge", new[] { "Genesis", "Gen", "Ge", "Gn" }), ("Ex", new[] { "Exodus", "Exod", "Exo", "Ex" }), ("Le", new[] { "Leviticus", "Lev", "Lv" }),
        ("Nu", new[] { "Numbers", "Num", "Nm" }), ("Dt", new[] { "Deuteronomy", "Deut", "Dt" }), ("Jos", new[] { "Joshua", "Josh", "Jos" }),
        ("Jdg", new[] { "Judges", "Judg", "Jdg" }), ("Ru", new[] { "Ruth", "Ru" }), ("1Sa", new[] { "1 Samuel", "1 Sam", "1 Sa" }),
        ("2Sa", new[] { "2 Samuel", "2 Sam", "2 Sa" }), ("1Ki", new[] { "1 Kings", "1 Kgs", "1 Ki" }), ("2Ki", new[] { "2 Kings", "2 Kgs", "2 Ki" }),
        ("1Ch", new[] { "1 Chronicles", "1 Chron", "1 Chr", "1 Ch" }), ("2Ch", new[] { "2 Chronicles", "2 Chron", "2 Chr", "2 Ch" }),
        ("Ezr", new[] { "Ezra", "Ezr" }), ("Ne", new[] { "Nehemiah", "Neh" }), ("Es", new[] { "Esther", "Esth" }), ("Job", new[] { "Job" }),
        ("Ps", new[] { "Psalms", "Psalm", "Pss", "Psa", "Ps" }), ("Pr", new[] { "Proverbs", "Prov", "Prv", "Pr" }),
        ("Ec", new[] { "Ecclesiastes", "Eccles", "Eccl", "Ecc", "Qoh" }), ("So", new[] { "Song of Songs", "Song of Solomon", "Song", "Cant" }),
        ("Is", new[] { "Isaiah", "Isa" }), ("Je", new[] { "Jeremiah", "Jer" }), ("La", new[] { "Lamentations", "Lam" }),
        ("Eze", new[] { "Ezekiel", "Ezek", "Eze" }), ("Da", new[] { "Daniel", "Dan", "Dn" }), ("Ho", new[] { "Hosea", "Hos" }),
        ("Joe", new[] { "Joel" }), ("Am", new[] { "Amos" }), ("Ob", new[] { "Obadiah", "Obad", "Ob" }), ("Jon", new[] { "Jonah", "Jon" }),
        ("Mic", new[] { "Micah", "Mic" }), ("Na", new[] { "Nahum", "Nah" }), ("Hab", new[] { "Habakkuk", "Hab" }),
        ("Zep", new[] { "Zephaniah", "Zeph", "Zep" }), ("Hag", new[] { "Haggai", "Hag" }), ("Zec", new[] { "Zechariah", "Zech", "Zec" }),
        ("Mal", new[] { "Malachi", "Mal" }), ("Mt", new[] { "Matthew", "Matt", "Mt" }), ("Mk", new[] { "Mark", "Mk", "Mrk" }),
        ("Lk", new[] { "Luke", "Lk" }), ("Jn", new[] { "John", "Jn" }), ("Ac", new[] { "Acts", "Ac" }), ("Ro", new[] { "Romans", "Rom", "Ro" }),
        ("1Co", new[] { "1 Corinthians", "1 Cor", "1 Co" }), ("2Co", new[] { "2 Corinthians", "2 Cor", "2 Co" }),
        ("Ga", new[] { "Galatians", "Gal" }), ("Eph", new[] { "Ephesians", "Eph" }), ("Php", new[] { "Philippians", "Phil", "Php" }),
        ("Col", new[] { "Colossians", "Col" }), ("1Th", new[] { "1 Thessalonians", "1 Thess", "1 Th" }), ("2Th", new[] { "2 Thessalonians", "2 Thess", "2 Th" }),
        ("1Ti", new[] { "1 Timothy", "1 Tim", "1 Ti" }), ("2Ti", new[] { "2 Timothy", "2 Tim", "2 Ti" }), ("Tt", new[] { "Titus", "Tit" }),
        ("Phm", new[] { "Philemon", "Phlm", "Phm" }), ("Heb", new[] { "Hebrews", "Heb" }), ("Jas", new[] { "James", "Jas" }),
        ("1Pe", new[] { "1 Peter", "1 Pet", "1 Pe" }), ("2Pe", new[] { "2 Peter", "2 Pet", "2 Pe" }),
        ("1Jn", new[] { "1 John", "1 Jn" }), ("2Jn", new[] { "2 John", "2 Jn" }), ("3Jn", new[] { "3 John", "3 Jn" }),
        ("Jud", new[] { "Jude" }), ("Re", new[] { "Revelation", "Rev", "Rv" })
    };

    private static readonly Dictionary<string, string> CodeFor = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Pattern;

    static ScriptureLinks()
    {
        var names = new List<string>();
        foreach (var (code, list) in Books)
            foreach (var name in list)
            {
                string key = Key(name);
                if (!CodeFor.ContainsKey(key)) CodeFor[key] = code;
                // "1 Cor" also matches "1Cor" and "I Cor"; a period may follow an abbreviation.
                string first = Regex.Escape(name[..1]);
                string rest = Regex.Escape(name[1..]).Replace("\\ ", "\\s*");
                names.Add(char.IsDigit(name[0]) ? $"(?:{name[0]}|{"I".PadLeft(name[0] - '0', 'I')})\\s*{Regex.Escape(name[2..]).Replace("\\ ", "\\s*")}" : first + rest);
            }
        string books = string.Join("|", names.OrderByDescending(n => n.Length));
        // Book, then chapter, then optional :verse, ranges (-, –) and a further chapter:verse.
        Pattern = new Regex($@"(?<![\w])(?<book>{books})\.?\s+(?<ref>\d{{1,3}}(?::\d{{1,3}}[a-c]?)?(?:\s*[-–]\s*\d{{1,3}}(?::\d{{1,3}})?)?(?:,\s*\d{{1,3}}(?:[-–]\d{{1,3}})?)*)(?![\w:])",
                            RegexOptions.CultureInvariant);
    }

    private static readonly HashSet<string> PersonNames = new(StringComparer.OrdinalIgnoreCase)
        { "John", "Mark", "Luke", "Job", "James", "Jude", "Ruth", "Amos", "Daniel", "Dan", "Joel", "Titus", "Jonah", "Ezra", "Micah", "Matt", "Matthew", "Joshua", "Josh" };

    private static string Key(string name) => Regex.Replace(name, @"\s+", "").TrimEnd('.');

    /// <summary>The Logos book code for a typed book name, or null.</summary>
    private static string? Code(string typed)
    {
        string key = Key(typed);
        var roman = Regex.Match(key, "^(I{1,3})(.+)$");
        if (roman.Success && !CodeFor.ContainsKey(key)) key = roman.Groups[1].Length + roman.Groups[2].Value;
        return CodeFor.TryGetValue(key, out var code) ? code : null;
    }

    /// <summary>The references in a piece of text: where each starts, its length, and its Logos form (like "Jn3.16-18").</summary>
    public static IEnumerable<(int Start, int Length, string Logos)> Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in Pattern.Matches(text))
        {
            if (Code(m.Groups["book"].Value) is not string code) continue;
            // A chapter alone ("John 3") could be a person and a number, so books that are also first names need a verse.
            if (!m.Groups["ref"].Value.Contains(':') && PersonNames.Contains(Key(m.Groups["book"].Value))) continue;
            string r = Regex.Replace(m.Groups["ref"].Value, @"\s+", "").Replace(':', '.').Replace('–', '-');
            yield return (m.Index, m.Length, code + r);
        }
    }

    /// <summary>Opens a passage in Logos, or at ref.ly if Logos can't be opened.</summary>
    public static void Open(string logos)
    {
        try { Process.Start(new ProcessStartInfo("logosref:Bible." + logos) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Error("ScriptureLinks.Open", ex);
            try { Process.Start(new ProcessStartInfo("https://ref.ly/" + Uri.EscapeDataString(logos)) { UseShellExecute = true }); } catch { }
        }
    }

    // ---------- On a TextBlock ----------

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(ScriptureLinks), new PropertyMetadata(null, (d, e) => { if (d is TextBlock tb) Fill(tb, e.NewValue as string); }));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    /// <summary>Puts text in a TextBlock with its Scripture references as links.</summary>
    public static void Fill(TextBlock block, string? text)
    {
        block.Inlines.Clear();
        text ??= "";
        int at = 0;
        foreach (var (start, length, logos) in Find(text))
        {
            if (start > at) block.Inlines.Add(new Run(text[at..start]));
            var link = new Hyperlink(new Run(text.Substring(start, length))) { ToolTip = "Open in Logos", Cursor = Cursors.Hand };
            link.SetResourceReference(TextElement.ForegroundProperty, "FocusBrush");
            // Handled here, so clicking the link doesn't also open the task for editing.
            link.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
            link.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; Open(logos); };
            block.Inlines.Add(link);
            at = start + length;
        }
        if (at < text.Length) block.Inlines.Add(new Run(text[at..]));
    }
}
