using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace FocusApp;

/// <summary>
/// One Greek word in your review bank, with how well you know it. Words climb levels 0 to 6 as you
/// match them correctly; each level waits longer before coming back, and level 6 means retired
/// (it only returns now and then, to make sure). A miss drops the word back down so it comes up more.
/// </summary>
public class VocabCard : IStamped
{
    /// <summary>When this word's progress last changed (UTC), so the newest wins when syncing.</summary>
    public DateTime? Modified { get; set; }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Greek { get; set; } = "";
    public string English { get; set; } = "";
    /// <summary>The built-in word this came from (its Greek as listed), or empty for your own words.</summary>
    public string Source { get; set; } = "";
    public int Level { get; set; }
    public DateTime Due { get; set; } = DateTime.Now;
    public DateTime? LastSeen { get; set; }
    public int Right { get; set; }
    public int Wrong { get; set; }
    /// <summary>True if the last time you saw it, you missed it.</summary>
    public bool LastWrong { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
    /// <summary>In your review (My words). Removed words stay saved, with their progress, and can be added back.</summary>
    public bool InReview { get; set; } = true;

    [JsonIgnore] public bool Retired => Vocab.IsRetired(this);
    /// <summary>How often you miss it (0 = never, 1 = always), counting a fresh word as middling.</summary>
    [JsonIgnore] public double MissRate => (Wrong + 0.5) / (Right + Wrong + 1.0);
    [JsonIgnore] public string LevelText => Retired ? "Retired (known well)" : $"Level {Level} of {Vocab.RetiredLevel}";
}

public static class Vocab
{
    /// <summary>
    /// How many levels a word climbs before it retires (Settings, Greek practice: 3 is quick, 12 is slow).
    /// Each level is one right-first-time round, waiting longer before it comes back.
    /// </summary>
    public static int RetiredLevel { get; set; } = 6;

    /// <summary>
    /// No word retires until My words holds more than this many (Settings). Above it, only the most familiar
    /// words that have reached the retire level retire, and only enough to bring My words back to this size.
    /// </summary>
    public static int RetireThreshold { get; set; } = 100;

    private static HashSet<string> _retired = new();

    public static bool IsRetired(VocabCard c) => _retired.Contains(c.Id);

    /// <summary>Works out which words are retired right now (after loading, saving, or a change in Settings).</summary>
    public static void RecomputeRetired()
    {
        var before = _retired;
        var active = Bank.Where(c => c.InReview).ToList();
        int excess = active.Count - RetireThreshold;
        if (excess <= 0) { _retired = new HashSet<string>(); BringBack(before); return; }
        _retired = active.Where(c => c.Level >= RetiredLevel)
            .OrderByDescending(c => c.Level).ThenBy(c => c.MissRate).ThenByDescending(c => c.Right)
            .Take(excess).Select(c => c.Id).ToHashSet();
        // A word that has just retired gets an occasional check (about 90 days after it was last seen).
        foreach (var c in Bank.Where(c => _retired.Contains(c.Id) && !before.Contains(c.Id)))
        {
            var check = (c.LastSeen ?? DateTime.Now) + TimeSpan.FromDays(90);
            if (c.Due < check) c.Due = check;
        }
        BringBack(before);
    }

    /// <summary>Words that just left retirement come back into review within a day rather than months later.</summary>
    private static void BringBack(HashSet<string> wereRetired)
    {
        foreach (var c in Bank.Where(c => wereRetired.Contains(c.Id) && !_retired.Contains(c.Id) && c.Due > DateTime.Now.AddDays(1)))
            c.Due = DateTime.Now.AddDays(1);
    }

    /// <summary>The longest any word in review waits before coming back, so every word keeps being reviewed.</summary>
    private static readonly TimeSpan LongestGap = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a word waits after a correct match: longer at higher levels, shorter the more often you miss it
    /// (a word you miss half the time waits less than half as long), and never more than 30 days.
    /// </summary>
    private static TimeSpan WaitFor(VocabCard card)
    {
        var basic = Wait(card.Level);
        // Miss rate 0 waits the full time; 0.25 about three-quarters; 0.5 under half; 1 a fifth.
        double ease = 1.0 / (1.0 + 5.0 * card.MissRate * card.MissRate);
        var wait = TimeSpan.FromTicks((long)(basic.Ticks * Math.Clamp(ease, 0.2, 1.0)));
        return wait > LongestGap ? LongestGap : wait;
    }

    /// <summary>The basic wait for a level (before adjusting for how often the word is missed).</summary>
    private static TimeSpan Wait(int level)
    {
        TimeSpan[] steps = { TimeSpan.FromHours(2), TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(7),
                             TimeSpan.FromDays(14), TimeSpan.FromDays(21), TimeSpan.FromDays(30), TimeSpan.FromDays(40),
                             TimeSpan.FromDays(50), TimeSpan.FromDays(60), TimeSpan.FromDays(75) };
        return steps[Math.Clamp(level, 0, steps.Length - 1)];
    }

    public static List<VocabCard> Bank { get; private set; } = new();
    public static event Action? Changed;

    public static void Load()
    {
        Bank = Storage.Load<List<VocabCard>>("vocab.json");
        _base = SyncMerge.Copy(Bank);
        UpdateMeanings();
        RecomputeRetired();
    }

    /// <summary>
    /// Words added from an earlier list take the lexicon's meaning, unless you edited the meaning yourself
    /// (an edited meaning no longer matches the earlier list's wording, so it's left alone).
    /// </summary>
    private static void UpdateMeanings()
    {
        foreach (var c in Bank.Where(c => c.Source.Length > 0))
        {
            if (BuiltInFor(c.Source) is not BuiltInWord w || c.English == w.English) continue;
            if (w.OldGloss.Length > 0 && c.English == w.OldGloss) c.English = w.English;
        }
        // Not saved now (nothing is written just from opening Focus); kept with your next practice or change.
    }

    private static List<VocabCard> _base = new();

    /// <summary>Saves by merging with OneDrive's current copy (see SyncMerge), writing only if something changed.</summary>
    public static void Save()
    {
        try
        {
            var merged = SyncMerge.SaveMerged("vocab.json", _base, Bank, c => c.Id);
            if (merged != null) { Bank = merged; _base = SyncMerge.Copy(merged); }
        }
        catch (Exception ex) { Log.Error("Vocab.Save", ex); }
        RecomputeRetired();
        Changed?.Invoke();
    }

    // ---------- The built-in list: every New Testament word, most frequent first ----------
    // Meanings are Dodson's public-domain Greek-English lexicon, word for word (only "a"/"an" and the final
    // period removed); counts are from the SBL Greek New Testament via MorphGNT.

    public record BuiltInWord(string Greek, string English, int Count, string Full = "", string OldGloss = "")
    {
        public string Key { get; } = LemmaKey(Greek);
        public string PlainGreek { get; } = Plain(Greek);
    }

    private static List<BuiltInWord>? _builtIn;

    public static IReadOnlyList<BuiltInWord> BuiltIn
    {
        get
        {
            if (_builtIn != null) return _builtIn;
            var list = new List<BuiltInWord>();
            try
            {
                using var stream = typeof(Vocab).Assembly.GetManifestResourceStream("FocusApp.GreekWords.tsv");
                if (stream != null)
                {
                    using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var parts = line.Split('\t');
                        if (parts.Length < 2 || parts[0].Trim().Length == 0) continue;
                        int count = parts.Length > 2 && int.TryParse(parts[2], out int n) ? n : 0;
                        list.Add(new BuiltInWord(parts[0].Trim(), parts[1].Trim(), count,
                                                 parts.Length > 3 ? parts[3].Trim() : "", parts.Length > 4 ? parts[4].Trim() : ""));
                    }
                }
            }
            catch (Exception ex) { Log.Error("Vocab.BuiltIn", ex); }
            return _builtIn = list;
        }
    }

    /// <summary>The dictionary word on its own, without endings, articles or accents: how words are matched up.</summary>
    public static string LemmaKey(string greek)
    {
        var head = System.Text.RegularExpressions.Regex.Replace(greek, @"\(.*?\)", "").Split(',')[0];
        return Plain(head).Trim();
    }

    /// <summary>The built-in entry for a word (by its dictionary form), if there is one.</summary>
    public static BuiltInWord? BuiltInFor(string greek)
    {
        string key = LemmaKey(greek);
        return BuiltIn.FirstOrDefault(b => b.Key == key);
    }

    /// <summary>True if a word with this dictionary form is in your bank (in review or saved).</summary>
    private static bool Has(string greek) { string k = LemmaKey(greek); return Bank.Any(c => LemmaKey(c.Greek) == k || (c.Source.Length > 0 && LemmaKey(c.Source) == k)); }

    /// <summary>Greek without accents and breathings, lowercased, final sigma as σ: for searching.</summary>
    public static string Plain(string text)
    {
        var d = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('ς', 'σ');
    }

    /// <summary>InBank: the word if it's in your review; Saved: your saved copy if you removed it from review.</summary>
    public record SearchResult(string Greek, string English, string Source, VocabCard? InBank, VocabCard? Saved = null);

    /// <summary>The words in your review (My words).</summary>
    public static IEnumerable<VocabCard> Active => Bank.Where(c => c.InReview);

    public static VocabCard? Find(string id) => Bank.FirstOrDefault(c => c.Id == id);

    /// <summary>Takes a word out of your review without deleting it: it goes back to Find words.</summary>
    public static void RemoveFromReview(string id)
    {
        if (Find(id) is not VocabCard c) return;
        c.InReview = false;
        Save();
    }

    /// <summary>Puts a saved word back into your review (its progress is kept).</summary>
    public static void AddBack(string id)
    {
        if (Find(id) is not VocabCard c) return;
        c.InReview = true;
        if (c.Due > DateTime.Now.AddDays(1) && !c.Retired) c.Due = DateTime.Now;
        Save();
    }

    /// <summary>Finds words by Greek (accents optional) or English, in your bank and the built-in list.</summary>
    public static List<SearchResult> Search(string query, int max = 60)
    {
        query = query.Trim();
        if (query.Length == 0) return new();
        string q = Plain(query);
        bool Match(string greek, string english) => Plain(greek).Contains(q) || english.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        int Rank(string greek, string english)
        {
            // Best first: exact Greek, Greek starting with it, English word match, anything else.
            var pg = Plain(greek).Split(',')[0].Trim();
            if (pg == q) return 0;
            if (pg.StartsWith(q)) return 1;
            if (english.Split(new[] { ',', ';', ' ', '(', ')' }, StringSplitOptions.RemoveEmptyEntries).Any(w => w.Equals(query, StringComparison.CurrentCultureIgnoreCase))) return 2;
            return 3;
        }
        var results = new List<(SearchResult R, int Rank, int Order)>();
        int order = 0;
        foreach (var c in Bank.Where(c => Match(c.Greek, c.English)))
            results.Add((new SearchResult(c.Greek, c.English, c.Source, c.InReview ? c : null, c.InReview ? null : c), Rank(c.Greek, c.English), order++));
        var mine = new HashSet<string>(Bank.SelectMany(c => new[] { LemmaKey(c.Greek), c.Source.Length > 0 ? LemmaKey(c.Source) : "" }));
        foreach (var w in BuiltIn)
        {
            order++;
            if (mine.Contains(w.Key)) continue;
            if (!w.PlainGreek.Contains(q) && !w.English.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
            results.Add((new SearchResult(w.Greek, w.English, w.Greek, null), Rank(w.Greek, w.English), order));
        }
        return results.OrderBy(r => r.Rank).ThenBy(r => r.Order).Take(max).Select(r => r.R).ToList();
    }

    public static VocabCard Add(string greek, string english, string source)
    {
        var card = new VocabCard { Greek = greek.Trim(), English = english.Trim(), Source = source, Due = DateTime.Now };
        Bank.Add(card);
        Save();
        return card;
    }

    /// <summary>Keeps a small supply of words to learn: adds the most frequent built-in words you don't have yet.</summary>
    public static int TopUp(int minActive)
    {
        int active = Bank.Count(c => c.InReview && !c.Retired);
        int added = 0;
        foreach (var w in BuiltIn)
        {
            if (active + added >= minActive) break;
            if (Has(w.Greek)) continue;
            Bank.Add(new VocabCard { Greek = w.Greek, English = w.English, Source = w.Greek, Due = DateTime.Now });
            added++;
        }
        if (added > 0) Save();
        return added;
    }

    /// <summary>
    /// Chooses words for a round: those due (most overdue and lowest level first, missed ones favored),
    /// then the least known; now and then one retired word, to check it's still known. Meanings are kept
    /// distinct so every match has one right answer.
    /// </summary>
    public static List<VocabCard> PickRound(int count, bool anyTime = false, Random? rnd = null)
    {
        rnd ??= new Random();
        var now = DateTime.Now;
        double Difficulty(VocabCard c) => c.MissRate;
        // Order: words you just missed, then words due for review (least known first), then new words,
        // then anything else by how soon it's due.
        int Group(VocabCard c) => c.LastWrong ? 0 : c.Due > now ? 3 : c.LastSeen != null ? 1 : 2;
        var candidates = Bank.Where(c => c.InReview && !c.Retired)
            .OrderBy(Group)
            .ThenBy(c => c.Level)
            .ThenByDescending(Difficulty)
            .ThenBy(c => c.Due)
            .ToList();
        var retiredDue = Bank.Where(c => c.InReview && c.Retired && c.Due <= now).OrderBy(_ => rnd.Next()).ToList();
        if (retiredDue.Count > 0 && rnd.NextDouble() < 0.5) candidates.Insert(Math.Min(count - 1, candidates.Count), retiredDue[0]);
        if (candidates.Count < count) candidates.AddRange(Bank.Where(c => c.InReview && c.Retired && !candidates.Contains(c)).OrderBy(c => c.Due));

        // Reviewing any time: keep the struggling words first, but shuffle the rest a little so every
        // round feels fresh and the whole bank gets a turn.
        if (anyTime)
        {
            // Words you just missed first; then a random draw where every word has a chance, but words you
            // miss more often (and words that are due) are several times more likely to come up.
            var struggling = candidates.Where(c => c.LastWrong).ToList();
            double Weight(VocabCard c) => (1.0 + 6.0 * c.MissRate) * (c.Due <= now ? 2.0 : 1.0);
            var rest = candidates.Where(c => !c.LastWrong)
                .Select(c => (c, key: -Math.Log(1.0 - rnd.NextDouble()) / Weight(c)))   // weighted random order
                .OrderBy(x => x.key).Select(x => x.c);
            candidates = struggling.Concat(rest).ToList();
        }
        var picked = new List<VocabCard>();
        foreach (var c in candidates)
        {
            if (picked.Count >= count) break;
            if (picked.Any(p => Plain(p.English) == Plain(c.English) || Plain(p.Greek) == Plain(c.Greek))) continue;
            picked.Add(c);
        }
        return picked.OrderBy(_ => rnd.Next()).ToList();
    }

    /// <summary>Records a match: right on the first try moves the word up a level; a miss drops it back.</summary>
    public static void Record(VocabCard card, bool rightFirstTry)
    {
        var now = DateTime.Now;
        card.LastSeen = now;
        card.LastWrong = !rightFirstTry;
        if (rightFirstTry)
        {
            card.Right++;
            card.Level = Math.Min(RetiredLevel, card.Level + 1);
            card.Due = IsRetired(card) ? now + TimeSpan.FromDays(90) : now + WaitFor(card); // retired: an occasional check only
        }
        else
        {
            card.Wrong++;
            card.Level = Math.Max(0, card.Level - 2);
            card.Due = now + TimeSpan.FromMinutes(30); // see it again soon
        }
    }

    /// <summary>Combines two copies of the bank (from two computers): each word keeps its most recent state.</summary>
    public static List<VocabCard> Merge(List<VocabCard> a, List<VocabCard> b) =>
        a.Concat(b).GroupBy(c => c.Id)
         .Select(g => g.OrderByDescending(c => c.LastSeen ?? DateTime.MinValue).ThenByDescending(c => c.Right + c.Wrong).First())
         .ToList();
}
