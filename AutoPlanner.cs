using System;
using System.Collections.Generic;
using System.Linq;

namespace FocusApp;

/// <summary>Something at a fixed time that a plan must work around (a meeting, lunch, a set break).</summary>
public record FixedItem(TimeSpan Start, TimeSpan End, string Name, BlockKind Kind);

/// <summary>
/// Builds a day plan by plain arithmetic (no AI, nothing online): it places the fixed items,
/// adds a short break after each meeting, then fills every gap with evenly sized focus
/// sessions and breaks, staying within the allowed ranges in <see cref="Defaults"/>.
/// </summary>
public static class AutoPlanner
{
    public static List<PlanBlock> Build(TimeSpan dayStart, TimeSpan dayEnd, IEnumerable<FixedItem> items,
        int preferredFocus, int preferredBreak, DateTime? date = null)
    {
        var day = (date ?? DateTime.Today).Date;
        var fixedItems = Normalize(dayStart, dayEnd, items);
        var blocks = new List<PlanBlock>();
        var cursor = dayStart;
        int focusNumber = 0;

        foreach (var item in fixedItems)
        {
            if (item.Start > cursor) FillGap((int)(item.Start - cursor).TotalMinutes, blocks, preferredFocus, preferredBreak, ref focusNumber);
            int minutes = (int)(item.End - item.Start).TotalMinutes;
            if (minutes > 0)
            {
                blocks.Add(new PlanBlock
                {
                    Kind = item.Kind,
                    Name = item.Name,
                    Minutes = minutes,
                    MeetingStart = item.Kind == BlockKind.Meeting ? day + item.Start : null,
                    AnchorStart = item.Start
                });
            }
            cursor = item.End;
        }
        if (dayEnd > cursor) FillGap((int)(dayEnd - cursor).TotalMinutes, blocks, preferredFocus, preferredBreak, ref focusNumber);
        return blocks;
    }

    /// <summary>
    /// Sorts items, clips them to the day, merges overlapping meetings, adds the after-meeting
    /// break (shortened if something else starts sooner), and drops anything fully overlapped.
    /// </summary>
    private static List<FixedItem> Normalize(TimeSpan dayStart, TimeSpan dayEnd, IEnumerable<FixedItem> items)
    {
        var all = items.ToList();

        // A meeting during the morning break replaces the break entirely.
        all.RemoveAll(i => i.Kind == BlockKind.Break && i.Name == Defaults.MorningBreakName &&
                           i.Start == Defaults.MorningBreakStart &&
                           all.Any(m => m.Kind == BlockKind.Meeting && m.Start < i.End && m.End > i.Start));

        var clipped = all
            .Select(i => i with { Start = Max(i.Start, dayStart), End = Min(i.End, dayEnd) })
            .Where(i => i.End > i.Start)
            .OrderBy(i => i.Start).ThenBy(i => i.End)
            .ToList();

        // Meetings win over breaks and lunch when they overlap; overlapping meetings merge.
        var result = new List<FixedItem>();
        foreach (var item in clipped)
        {
            if (result.Count == 0 || item.Start >= result[^1].End)
            {
                result.Add(item);
                continue;
            }
            var last = result[^1];
            if (last.Kind == BlockKind.Meeting && item.Kind == BlockKind.Meeting)
            {
                result[^1] = last with { End = Max(last.End, item.End), Name = last.Name + " + " + item.Name };
            }
            else if (item.Kind == BlockKind.Meeting)
            {
                // A meeting overlaps a set break: shorten the break, the meeting keeps its time.
                result[^1] = last with { End = item.Start };
                if (result[^1].End <= result[^1].Start) result.RemoveAt(result.Count - 1);
                result.Add(item);
            }
            else if (item.End > last.End)
            {
                // A set break overlaps the end of a meeting: keep the part after the meeting.
                result.Add(item with { Start = last.End });
            }
        }

        // A break after each meeting, unless something else is already right there.
        var withBreaks = new List<FixedItem>();
        for (int i = 0; i < result.Count; i++)
        {
            var item = result[i];
            withBreaks.Add(item);
            if (item.Kind != BlockKind.Meeting) continue;
            var nextStart = i + 1 < result.Count ? result[i + 1].Start : dayEnd;
            if (i + 1 < result.Count && result[i + 1].Kind != BlockKind.Meeting && result[i + 1].Start == item.End) continue;
            var breakEnd = Min(item.End + TimeSpan.FromMinutes(Defaults.BreakAfterMeeting), nextStart);
            if (breakEnd > item.End) withBreaks.Add(new FixedItem(item.End, breakEnd, "Break after meeting", BlockKind.Break));
        }
        return withBreaks;
    }

    /// <summary>Focus sessions and breaks that exactly fill <paramref name="minutes"/>, for filling gaps while editing.</summary>
    public static List<PlanBlock> Fill(int minutes, int preferredFocus, int preferredBreak, int focusNumbersSoFar)
    {
        var blocks = new List<PlanBlock>();
        int number = focusNumbersSoFar;
        FillGap(minutes, blocks, preferredFocus, preferredBreak, ref number);
        return blocks;
    }

    /// <summary>Fills a gap of free minutes with focus sessions separated by breaks.</summary>
    private static void FillGap(int gap, List<PlanBlock> blocks, int preferredFocus, int preferredBreak, ref int focusNumber)
    {
        if (gap <= 0) return;
        int prefFocus = Math.Clamp(preferredFocus, Defaults.FocusMin, Defaults.FocusMax);
        int prefBreak = Math.Clamp(preferredBreak, Defaults.BreakMin, Defaults.BreakMax);

        if (gap < Defaults.FocusMin)
        {
            // Too short for a real focus session. Short ones become a break; a longer awkward gap first
            // tries to stretch the focus session just before it (within the limits).
            var last = blocks.Count > 0 ? blocks[^1] : null;
            if (gap > Defaults.BreakMax && last != null && last.IsFocus && !last.IsAnchored && last.Minutes + gap <= Defaults.FocusMax)
            {
                last.Minutes += gap;
                return;
            }
            AddOrExtendBreak(blocks, gap <= Defaults.BreakMax ? "Break" : "Free time", gap);
            return;
        }

        // Try every possible number of focus sessions; keep the one closest to your preferred lengths.
        int bestCount = 0, bestBreak = prefBreak;
        double bestScore = double.MaxValue;
        for (int n = 1; n <= gap / Defaults.FocusMin; n++)
        {
            for (int b = Defaults.BreakMin; b <= Defaults.BreakMax; b++)
            {
                if (n == 1 && b != prefBreak) continue; // one session has no break inside the gap
                double focus = (gap - (n - 1) * b) / (double)n;
                if (focus < Defaults.FocusMin || focus > Defaults.FocusMax) continue;
                double score = Math.Abs(focus - prefFocus) + Math.Abs(b - prefBreak);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestCount = n;
                    bestBreak = b;
                }
            }
        }

        if (bestCount == 0)
        {
            // No exact fit (for example 41 to 53 minutes): as many full sessions as fit, then a break.
            int n = Math.Max(1, (gap + prefBreak) / (Defaults.FocusMax + prefBreak));
            int used = 0;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) { blocks.Add(MakeBreak(prefBreak)); used += prefBreak; }
                blocks.Add(MakeFocus(Defaults.FocusMax, ref focusNumber));
                used += Defaults.FocusMax;
            }
            int left = gap - used;
            if (left > 0 && left < Defaults.BreakMin) blocks[^1].Minutes += left; // a 1 to 3 minute break isn't worth it
            else if (left > 0) AddOrExtendBreak(blocks, "Break", left);
            return;
        }

        int focusTotal = gap - (bestCount - 1) * bestBreak;
        int baseLength = focusTotal / bestCount, extra = focusTotal % bestCount;
        for (int i = 0; i < bestCount; i++)
        {
            if (i > 0) blocks.Add(MakeBreak(bestBreak));
            blocks.Add(MakeFocus(baseLength + (i < extra ? 1 : 0), ref focusNumber));
        }
    }

    private static void AddOrExtendBreak(List<PlanBlock> blocks, string name, int minutes)
    {
        if (blocks.Count > 0 && blocks[^1].Kind == BlockKind.Break && blocks[^1].Name == name)
            blocks[^1].Minutes += minutes;
        else
            blocks.Add(new PlanBlock { Kind = BlockKind.Break, Name = name, Minutes = minutes });
    }

    private static PlanBlock MakeFocus(int minutes, ref int number) =>
        new() { Kind = BlockKind.Focus, Name = $"Focus session {++number}", Minutes = minutes };

    private static PlanBlock MakeBreak(int minutes) =>
        new() { Kind = BlockKind.Break, Name = "Break", Minutes = minutes };

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>
    /// Plans a whole date around its meetings: fills 6:45 to 4:30 (stretching to fit a meeting
    /// that starts before or runs past those times), with the set break and lunch, a break after
    /// each meeting, and your default blocking on every focus session.
    /// </summary>
    public static (List<PlanBlock> Blocks, TimeSpan Start) BuildDay(DateTime date, IEnumerable<CalendarEvent> busyEvents,
        int preferredFocus, int preferredBreak, IEnumerable<string> defaultLists, IEnumerable<BlockedApp> defaultApps,
        TimeSpan? workStart = null, TimeSpan? workEnd = null)
    {
        var start = workStart ?? Defaults.AutoFillStart;
        var end = workEnd ?? Defaults.AutoFillEnd;
        var meetings = new List<FixedItem>();
        foreach (var e in busyEvents)
        {
            var s = e.Start < date.Date ? TimeSpan.Zero : e.Start - date.Date;
            var f = e.End > date.Date.AddDays(1) ? TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1) : e.End - date.Date;
            if (f <= s) continue;
            meetings.Add(new FixedItem(s, f, e.Title, BlockKind.Meeting));
        }

        // A meeting that crosses the edges of the day stretches the day to fit it.
        foreach (var m in meetings)
        {
            if (m.Start < end && m.End > end) end = m.End;
            if (m.End > start && m.Start < start) start = m.Start;
        }

        // Set breaks outside the working day (say, Devotions when the day starts at 9) are left out.
        var standard = StandardBreaks().Where(b => b.Start >= start && b.End <= end);
        var blocks = Build(start, end, standard.Concat(meetings), preferredFocus, preferredBreak, date);
        var lists = defaultLists.ToList();
        var apps = defaultApps.ToList();
        foreach (var b in blocks.Where(b => b.IsFocus))
        {
            b.BlockListIds = new List<string>(lists);
            b.BlockedApps = apps.Select(a => a.Copy()).ToList();
        }
        return (blocks, start);
    }

    /// <summary>A short fingerprint of a day's meetings, to notice when the calendar changes.</summary>
    public static string Fingerprint(IEnumerable<CalendarEvent> busyEvents)
    {
        string text = string.Join("|", busyEvents.Select(e => $"{e.Start:yyyyMMddHHmm}-{e.End:yyyyMMddHHmm}-{e.Title}"));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>The fixed breaks every automatic plan includes (from <see cref="Defaults"/>).</summary>
    public static IEnumerable<FixedItem> StandardBreaks() => new[]
    {
        new FixedItem(Defaults.DevotionsStart, Defaults.DevotionsEnd, Defaults.DevotionsName, BlockKind.Break),
        new FixedItem(Defaults.MorningBreakStart, Defaults.MorningBreakEnd, Defaults.MorningBreakName, BlockKind.Break),
        new FixedItem(Defaults.LunchStart, Defaults.LunchEnd, Defaults.LunchName, BlockKind.Break)
    };
}
