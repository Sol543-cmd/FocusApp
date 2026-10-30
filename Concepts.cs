using System;
using System.Collections.Generic;
using System.Linq;

namespace FocusApp;

/// <summary>
/// Concepts: your own questions and answers (from your textbook reading), practised like the Greek words:
/// match each question to its answer. Every card stays in review; ones you know come back less often (up to
/// 30 days), and ones you miss come back soon and more often. Synced between computers like the others.
/// </summary>
public sealed class Concepts : IPracticeDeck
{
    public static readonly Concepts Instance = new();
    public string Title => "Concepts";
    public string Hint => "Click a question, then its answer. Click a matched pair again to undo it.";
    public bool LargeLeft => false;
    public static int ExtraSetting { get; set; }
    public int ExtraCount => ExtraSetting;

    public static List<VocabCard> Cards { get; private set; } = new();
    IEnumerable<VocabCard> IPracticeDeck.Cards => Cards;
    private static List<VocabCard> _base = new();
    public static event Action? Changed;

    public static void Load()
    {
        Cards = Storage.Load<List<VocabCard>>("concepts.json");
        _base = SyncMerge.Copy(Cards);
    }

    public void Save() => SaveNow();

    public static void SaveNow()
    {
        try
        {
            var merged = SyncMerge.SaveMerged("concepts.json", _base, Cards, c => c.Id);
            if (merged != null) { Cards = merged; _base = SyncMerge.Copy(merged); }
        }
        catch (Exception ex) { Log.Error("Concepts.Save", ex); }
        Changed?.Invoke();
    }

    /// <summary>Adds a question and its answer (Source holds an optional chapter or reading, for your own sorting).</summary>
    public static VocabCard Add(string question, string answer, string chapter = "")
    {
        var card = new VocabCard { Greek = question.Trim(), English = answer.Trim(), Source = chapter.Trim(), Due = DateTime.Now };
        Cards.Add(card);
        SaveNow();
        return card;
    }

    public static void Remove(string id)
    {
        Cards.RemoveAll(c => c.Id == id);
        SaveNow();
    }

    private const int MaxLevel = 8;
    private static readonly TimeSpan[] Waits =
    {
        TimeSpan.FromHours(2), TimeSpan.FromHours(8), TimeSpan.FromDays(1), TimeSpan.FromDays(3),
        TimeSpan.FromDays(7), TimeSpan.FromDays(14), TimeSpan.FromDays(21), TimeSpan.FromDays(30), TimeSpan.FromDays(30)
    };

    /// <summary>Right first time: back later (sooner if you often miss it). A miss: down two levels, back within the hour.</summary>
    public void Record(VocabCard card, bool right)
    {
        var now = DateTime.Now;
        card.LastSeen = now;
        card.LastWrong = !right;
        if (right)
        {
            card.Right++;
            card.Level = Math.Min(MaxLevel, card.Level + 1);
            double ease = 1.0 / (1.0 + 5.0 * card.MissRate * card.MissRate);
            card.Due = now + TimeSpan.FromTicks((long)(Waits[card.Level].Ticks * Math.Clamp(ease, 0.2, 1.0)));
        }
        else
        {
            card.Wrong++;
            card.Level = Math.Max(0, card.Level - 2);
            card.Due = now + TimeSpan.FromMinutes(30);
        }
    }

    /// <summary>
    /// A round: ones you just missed first, then those due (least known first), then a draw weighted toward
    /// the ones you miss most. Answers in a round are all different, so every match has one right answer.
    /// </summary>
    public static List<VocabCard> PickRound(int count, Random? rnd = null)
    {
        rnd ??= new Random();
        var now = DateTime.Now;
        var missed = Cards.Where(c => c.LastWrong).OrderBy(c => c.Due);
        var due = Cards.Where(c => !c.LastWrong && c.Due <= now).OrderBy(c => c.Level).ThenByDescending(c => c.MissRate);
        var rest = Cards.Where(c => !c.LastWrong && c.Due > now)
            .Select(c => (c, key: -Math.Log(1.0 - rnd.NextDouble()) / (1.0 + 6.0 * c.MissRate))).OrderBy(x => x.key).Select(x => x.c);
        var picked = new List<VocabCard>();
        foreach (var c in missed.Concat(due).Concat(rest))
        {
            if (picked.Count >= count) break;
            if (picked.Any(p => PracticeExtras.SameMeaning(p, c) || p.Greek.Trim() == c.Greek.Trim())) continue;
            picked.Add(c);
        }
        return picked.OrderBy(_ => rnd.Next()).ToList();
    }

    public List<VocabCard> Extras(IReadOnlyList<VocabCard> round, Random rnd) =>
        PracticeExtras.Pick(Cards, round, ExtraSetting, rnd, PracticeExtras.SameMeaning);

    /// <summary>Reads a pasted list: each line is the question, then " = " (or "=", or a tab), then the answer.</summary>
    public static (List<(string Q, string A)> Pairs, List<string> Left) ParseList(string text)
    {
        var pairs = new List<(string, string)>();
        var left = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim('\r', ' ');
            if (line.Length == 0) continue;
            int tab = line.IndexOf('\t');
            int eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0) eq = line.IndexOf('=');
            int at = tab >= 0 ? tab : eq;
            int len = tab >= 0 ? 1 : line.IndexOf(" = ", StringComparison.Ordinal) == eq ? 3 : 1;
            if (at <= 0 || at + len >= line.Length) { left.Add(line); continue; }
            string q = line[..at].Trim(), a = line[(at + len)..].Trim();
            if (q.Length == 0 || a.Length == 0) { left.Add(line); continue; }
            pairs.Add((q, a));
        }
        return (pairs, left);
    }
}
