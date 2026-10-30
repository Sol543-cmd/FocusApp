using System;
using System.Collections.Generic;
using System.Linq;

namespace FocusApp;

/// <summary>
/// What a practice round works with: the Greek words, or the alphabet's sounds.
/// </summary>
public interface IPracticeDeck
{
    string Title { get; }
    string Hint { get; }
    bool LargeLeft { get; }              // letters show bigger than words
    IEnumerable<VocabCard> Cards { get; }
    void Record(VocabCard card, bool rightFirstTry);
    void Save();
    /// <summary>How many extra meanings (with no match) to mix in, set in Settings (0 to 3).</summary>
    int ExtraCount { get; }
    /// <summary>Extra meanings from this practice's own bank that can't be confused with any in the round.</summary>
    List<VocabCard> Extras(IReadOnlyList<VocabCard> round, Random rnd);
}

/// <summary>Picks extra (unmatched) meanings: different from every meaning in the round, and from each other.</summary>
public static class PracticeExtras
{
    public static List<VocabCard> Pick(IEnumerable<VocabCard> pool, IReadOnlyList<VocabCard> round, int count, Random rnd, Func<VocabCard, VocabCard, bool> same)
    {
        var chosen = new List<VocabCard>();
        if (count <= 0) return chosen;
        foreach (var c in pool.Where(c => !round.Contains(c) && c.English.Trim().Length > 0).OrderBy(_ => rnd.Next()))
        {
            if (chosen.Count >= count) break;
            if (round.Any(r => same(r, c)) || chosen.Any(x => same(x, c))) continue;
            chosen.Add(c);
        }
        return chosen;
    }

    /// <summary>Two meanings count as the same if they read the same once accents, case and spacing are ignored.</summary>
    public static bool SameMeaning(VocabCard a, VocabCard b) => Vocab.Plain(a.English).Trim() == Vocab.Plain(b.English).Trim();
}

public sealed class WordsDeck : IPracticeDeck
{
    public static readonly WordsDeck Instance = new();
    public string Title => "Greek practice";
    public string Hint => "Click a Greek word, then its meaning. Click a matched pair again to undo it.";
    public bool LargeLeft => false;
    public IEnumerable<VocabCard> Cards => Vocab.Bank;
    public void Record(VocabCard card, bool right) => Vocab.Record(card, right);
    public void Save() => Vocab.Save();
    public static int ExtraSetting { get; set; }
    public int ExtraCount => ExtraSetting;
    public List<VocabCard> Extras(IReadOnlyList<VocabCard> round, Random rnd) =>
        PracticeExtras.Pick(Vocab.Active, round, ExtraSetting, rnd, PracticeExtras.SameMeaning);
}

/// <summary>
/// Modern Greek sounds for every letter and the common letter pairs. All of them are always in review:
/// letters you know well just come back less often (never retired), and ones you miss come back soon.
/// </summary>
public sealed class Alphabet : IPracticeDeck
{
    public static readonly Alphabet Instance = new();
    public string Title => "Greek alphabet";
    public string Hint => "Click a letter, then the sound it makes in modern Greek. Click a matched pair again to undo it.";
    public bool LargeLeft => true;
    public static int ExtraSetting { get; set; }
    public int ExtraCount => ExtraSetting;
    /// <summary>Extra sounds: never one that sounds like a letter in the round (η, ι and υ, for example).</summary>
    public List<VocabCard> Extras(IReadOnlyList<VocabCard> round, Random rnd) =>
        PracticeExtras.Pick(Cards.Where(c => IncludePairs || !IsPair(c)), round, ExtraSetting, rnd, SameSound);

    /// <summary>The letters (and pairs) and their modern Greek sounds. No letter names: sounds only.</summary>
    public static readonly (string Letter, string Sound, bool Pair)[] Items =
    {
        ("Α α", "a as in \"father\"", false),
        ("Β β", "v as in \"vase\"", false),
        ("Γ γ", "a soft, throaty g (like a gargled \"gh\") before a, o, ou and consonants; y as in \"yes\" before e and ee sounds", false),
        ("Δ δ", "th as in \"this\"", false),
        ("Ε ε", "e as in \"pet\"", false),
        ("Ζ ζ", "z as in \"zoo\"", false),
        ("Η η", "ee as in \"see\"", false),
        ("Θ θ", "th as in \"think\"", false),
        ("Ι ι", "ee as in \"see\"; often a quick y (as in \"yes\") right before another vowel", false),
        ("Κ κ", "k as in \"skin\" (no puff of air); a little softer, almost \"ky\", before e and ee sounds", false),
        ("Λ λ", "l as in \"lamp\"", false),
        ("Μ μ", "m as in \"man\"", false),
        ("Ν ν", "n as in \"net\"", false),
        ("Ξ ξ", "ks as in \"box\"", false),
        ("Ο ο", "o as in \"or\" (short and clear)", false),
        ("Π π", "p as in \"spin\" (no puff of air)", false),
        ("Ρ ρ", "a tapped or lightly rolled r", false),
        ("Σ σ ς", "s as in \"sun\"; z as in \"zoo\" before β, γ, δ, λ, μ, ν, ρ (ς is the form at the end of a word)", false),
        ("Τ τ", "t as in \"stop\" (no puff of air)", false),
        ("Υ υ", "ee as in \"see\" (same sound as η and ι)", false),
        ("Φ φ", "f as in \"fun\"", false),
        ("Χ χ", "ch as in Scottish \"loch\" before a, o, ou and consonants; a soft h, like German \"ich\", before e and ee sounds", false),
        ("Ψ ψ", "ps as in \"lapse\"", false),
        ("Ω ω", "o as in \"or\" (same sound as ο)", false),
        ("αι", "e as in \"pet\" (same sound as ε)", true),
        ("ει", "ee as in \"see\" (two letters, one sound)", true),
        ("οι", "ee as in \"see\" (two letters, one sound: like ει)", true),
        ("ου", "oo as in \"food\"", true),
        ("αυ", "av before vowels and β, γ, δ, ζ, λ, μ, ν, ρ; af before θ, κ, ξ, π, σ, τ, φ, χ, ψ", true),
        ("ευ", "ev before vowels and β, γ, δ, ζ, λ, μ, ν, ρ; ef before θ, κ, ξ, π, σ, τ, φ, χ, ψ", true),
        ("μπ", "b at the start of a word; mb (or b) in the middle", true),
        ("ντ", "d at the start of a word; nd (or d) in the middle", true),
        ("γκ", "g as in \"go\" at the start of a word; ng (as in \"finger\") or g in the middle", true),
        ("γγ", "ng as in \"finger\"", true),
        ("τσ", "ts as in \"cats\"", true),
        ("τζ", "dz as in \"adze\"", true),
    };

    public static List<VocabCard> Cards { get; private set; } = new();
    IEnumerable<VocabCard> IPracticeDeck.Cards => Cards;
    public static bool IncludePairs { get; set; } = true;

    /// <summary>Loads your progress and makes sure every letter is there, with its current sound.</summary>
    public static void Load()
    {
        // One card per letter (the same ID on every computer, so syncing never duplicates them).
        var loaded = Storage.Load<List<VocabCard>>("alphabet.json");
        _base = SyncMerge.Copy(loaded);
        Cards = loaded.GroupBy(c => c.Greek).Select(g => g.OrderByDescending(c => c.Right + c.Wrong).First()).ToList();
        bool changed = false;
        foreach (var (letter, sound, _) in Items)
        {
            var card = Cards.FirstOrDefault(c => c.Greek == letter);
            if (card == null) { Cards.Add(new VocabCard { Id = "alpha:" + letter, Greek = letter, English = sound, Source = letter, Due = DateTime.Now }); changed = true; }
            else if (card.English != sound) { card.English = sound; changed = true; }
        }
        _ = changed; // not saved now: nothing is written just from opening Focus
    }

    public void Save() => SaveNow();

    private static List<VocabCard> _base = new();

    private static void SaveNow()
    {
        try
        {
            var merged = SyncMerge.SaveMerged("alphabet.json", _base, Cards, c => c.Id);
            if (merged != null) { Cards = merged; _base = SyncMerge.Copy(merged); }
        }
        catch (Exception ex) { Log.Error("Alphabet.Save", ex); }
    }

    private const int MaxLevel = 8;
    private static readonly TimeSpan[] Waits =
    {
        TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromDays(1), TimeSpan.FromDays(3),
        TimeSpan.FromDays(7), TimeSpan.FromDays(14), TimeSpan.FromDays(21), TimeSpan.FromDays(30), TimeSpan.FromDays(30)
    };

    /// <summary>Right first time: back later than before. A miss: down two levels and back soon. Never retired.</summary>
    public void Record(VocabCard card, bool right)
    {
        var now = DateTime.Now;
        card.LastSeen = now;
        card.LastWrong = !right;
        if (right) { card.Right++; card.Level = Math.Min(MaxLevel, card.Level + 1); card.Due = now + Waits[card.Level]; }
        else { card.Wrong++; card.Level = Math.Max(0, card.Level - 2); card.Due = now + TimeSpan.FromMinutes(20); }
    }

    /// <summary>
    /// A round from all the letters: missed ones first, then those due (least known first), then the
    /// ones due soonest. Sounds in a round are all different, so every match has one right answer.
    /// </summary>
    public static List<VocabCard> PickRound(int count, Random? rnd = null)
    {
        rnd ??= new Random();
        var now = DateTime.Now;
        var pool = Cards.Where(c => IncludePairs || !IsPair(c)).ToList();
        var ordered = pool
            .OrderBy(c => c.LastWrong ? 0 : c.Due <= now ? 1 : 2)
            .ThenBy(c => c.Due <= now ? c.Level : 0)
            .ThenBy(c => c.Due)
            .ThenBy(_ => rnd.Next())
            .ToList();
        var picked = new List<VocabCard>();
        foreach (var c in ordered)
        {
            if (picked.Count >= count) break;
            if (picked.Any(p => SameSound(p, c))) continue;
            picked.Add(c);
        }
        return picked.OrderBy(_ => rnd.Next()).ToList();
    }

    private static bool IsPair(VocabCard c) => Items.Any(i => i.Letter == c.Greek && i.Pair);

    /// <summary>Letters that sound alike (η, ι, υ, ει, οι; ο and ω; ε and αι) never share a round.</summary>
    private static bool SameSound(VocabCard a, VocabCard b)
    {
        static string Core(string s) => s.Split(new[] { ';', '(' }, 2)[0].Trim();
        return Core(a.English) == Core(b.English);
    }
}
