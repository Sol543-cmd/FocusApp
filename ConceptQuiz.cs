using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FocusApp;

/// <summary>One Greek lesson's question pack: the full lesson text (for links) and its 10 to 12 quiz questions.</summary>
public sealed class QuizPack
{
    /// <summary>The lesson's number, as the course writes it (for example "2.14").</summary>
    public string Lesson { get; set; } = "";
    public string Title { get; set; } = "";
    public List<QuizSection> Sections { get; set; } = new();
    public List<QuizQuestion> Questions { get; set; } = new();
}

public sealed class QuizSection
{
    public string Id { get; set; } = "";
    public string Heading { get; set; } = "";
    public List<string> Paragraphs { get; set; } = new();
}

public sealed class QuizQuestion
{
    public string Id { get; set; } = "";
    public string Prompt { get; set; } = "";
    public List<string> Options { get; set; } = new();
    /// <summary>The right options (one or several), by position in Options.</summary>
    public List<int> Correct { get; set; } = new();
    public string Explanation { get; set; } = "";
    /// <summary>The section of the lesson text that discusses it (a QuizSection Id).</summary>
    public string Section { get; set; } = "";
}

/// <summary>
/// The Greek concept quiz's packs, one per lesson, kept in the synced Focus folder so every computer has them.
/// Learn mode asks one lesson's questions; review mode asks 12 mixed from every lesson.
/// </summary>
public static class ConceptQuiz
{
    public const string Extension = ".quizpack";
    private static string Dir => Path.Combine(Storage.SharedFolder, "greek-concepts");
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    /// <summary>Lesson numbers in course order: 1.2 before 1.10, 1.10 before 2.1.</summary>
    public static double[] Key(string lesson) =>
        lesson.Split('.', '-', ' ').Select(p => double.TryParse(p, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();

    private static int Compare(string a, string b)
    {
        var x = Key(a); var y = Key(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            double p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p.CompareTo(q);
        }
        return string.CompareOrdinal(a, b);
    }

    public static List<QuizPack> All()
    {
        var list = new List<QuizPack>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            foreach (var f in Directory.GetFiles(Dir, "*" + Extension))
            {
                try { if (JsonSerializer.Deserialize<QuizPack>(File.ReadAllText(f), Json) is QuizPack p && p.Questions.Count > 0) list.Add(p); }
                catch (Exception ex) { Log.Error("Quiz pack " + Path.GetFileName(f), ex); }
            }
        }
        catch (Exception ex) { Log.Error("Quiz packs", ex); }
        list.Sort((a, b) => Compare(a.Lesson, b.Lesson));
        return list;
    }

    /// <summary>The lesson learn mode uses: the one chosen in Settings, or the most recent.</summary>
    public static QuizPack? Chosen(string setting)
    {
        var all = All();
        return all.FirstOrDefault(p => p.Lesson == setting && setting.Length > 0) ?? all.LastOrDefault();
    }

    /// <summary>Imports a pack file (replacing that lesson's earlier pack). Returns it, or throws with a plain reason.</summary>
    public static QuizPack Import(string path)
    {
        var pack = JsonSerializer.Deserialize<QuizPack>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("This file isn't a question pack.");
        if (pack.Lesson.Trim().Length == 0) throw new InvalidDataException("The pack doesn't say which lesson it is.");
        if (pack.Questions.Count == 0) throw new InvalidDataException("The pack has no questions.");
        foreach (var q in pack.Questions)
            if (q.Options.Count < 2 || q.Correct.Count == 0 || q.Correct.Any(c => c < 0 || c >= q.Options.Count))
                throw new InvalidDataException($"Question \"{q.Prompt}\" has its answers set up wrong.");
        Directory.CreateDirectory(Dir);
        string safe = string.Concat(pack.Lesson.Trim().Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '-'));
        File.WriteAllText(Path.Combine(Dir, "lesson-" + safe + Extension), JsonSerializer.Serialize(pack, Json));
        return pack;
    }

    public static void Remove(string lesson)
    {
        foreach (var f in Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*" + Extension) : Array.Empty<string>())
        {
            try
            {
                var p = JsonSerializer.Deserialize<QuizPack>(File.ReadAllText(f), Json);
                if (p?.Lesson == lesson) File.Delete(f);
            }
            catch (Exception ex) { Log.Error("Quiz pack remove", ex); }
        }
    }

    /// <summary>Learn mode: the lesson's questions, in a fresh order.</summary>
    public static List<(QuizPack Pack, QuizQuestion Q)> LearnRound(QuizPack pack) =>
        pack.Questions.OrderBy(_ => Guid.NewGuid()).Select(q => (pack, q)).ToList();

    /// <summary>Review mode: 12 questions mixed at random from every lesson so far.</summary>
    public static List<(QuizPack Pack, QuizQuestion Q)> ReviewRound(int count = 12) =>
        All().SelectMany(p => p.Questions.Select(q => (p, q))).OrderBy(_ => Guid.NewGuid()).Take(count).ToList();
}
