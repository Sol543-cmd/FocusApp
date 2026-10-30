using System;
using System.Collections.Generic;
using System.Linq;

namespace FocusApp;

/// <summary>
/// Keeps a day plan in shape while you edit it. Fixed-time blocks (meetings, lunch, the set break,
/// the break after a meeting) never move; the focus sessions and breaks between them stretch,
/// shrink, or get filled in so everything still lines up. Plain arithmetic, no AI.
/// </summary>
public static class PlanReflow
{
    /// <summary>Blocks squeezed shorter than this are removed rather than left as slivers.</summary>
    public const int MinBlock = 3;

    public static bool HasAnchors(IEnumerable<PlanBlock> plan) => plan.Any(b => b.IsAnchored);

    /// <summary>
    /// A block got longer or shorter by <paramref name="delta"/> minutes: the blocks right after it
    /// give or take the difference, stopping at the next fixed-time block.
    /// </summary>
    public static void AbsorbChange(List<PlanBlock> plan, PlanBlock changed, int delta)
    {
        int i = plan.IndexOf(changed);
        if (i < 0 || delta == 0) return;
        int j = i + 1;

        if (delta < 0)
        {
            // Shorter: the next block grows into the freed time (a fixed block instead gets a break before it).
            if (j < plan.Count && !plan[j].IsAnchored) plan[j].Minutes += -delta;
            return;
        }

        // Longer: the next blocks shrink, and any squeezed too small are removed.
        int remaining = delta;
        while (remaining > 0 && j < plan.Count && !plan[j].IsAnchored)
        {
            var next = plan[j];
            if (next.Minutes - remaining >= MinBlock)
            {
                next.Minutes -= remaining;
                remaining = 0;
            }
            else
            {
                remaining -= next.Minutes;
                plan.RemoveAt(j);
            }
        }
        // Anything still left over is sorted out by Reflow.
    }

    /// <summary>
    /// Plans saved before fixed times existed: marks meetings, lunch, the set break and the break
    /// after a meeting as fixed at the times they currently have.
    /// </summary>
    public static void InferAnchors(IList<PlanBlock> plan, TimeSpan dayStart)
    {
        var t = dayStart;
        foreach (var b in plan)
        {
            if (!b.IsAnchored)
            {
                bool fixedBreak =
                    (b.Kind == BlockKind.Break && b.Name == Defaults.LunchName && t == Defaults.LunchStart) ||
                    (b.Kind == BlockKind.Break && b.Name == Defaults.MorningBreakName && t == Defaults.MorningBreakStart) ||
                    (b.Kind == BlockKind.Break && b.Name == Defaults.DevotionsName && t == Defaults.DevotionsStart) ||
                    (b.Kind == BlockKind.Break && b.Name == "Break after meeting");
                if (b.IsMeeting) b.AnchorStart = b.MeetingStart?.TimeOfDay ?? t;
                else if (fixedBreak) b.AnchorStart = t;
            }
            t += TimeSpan.FromMinutes(b.Minutes);
        }
    }

    /// <summary>Removes a block and fills its time with focus sessions and breaks, when the plan has fixed times.</summary>
    public static void RemoveAndFill(List<PlanBlock> plan, PlanBlock block, int preferredFocus, int preferredBreak)
    {
        int i = plan.IndexOf(block);
        if (i < 0) return;
        plan.RemoveAt(i);
        if (HasAnchors(plan)) AddFill(plan, i, block.Minutes, preferredFocus, preferredBreak);
    }

    /// <summary>
    /// Makes every fixed-time block start exactly at its time again. Between fixed blocks, extra time
    /// is filled and too much time is trimmed (from the end of that stretch, never from <paramref name="keep"/>
    /// unless there's no other way). After the last fixed block, the day ends at <paramref name="softEnd"/>.
    /// </summary>
    public static List<PlanBlock> Reflow(List<PlanBlock> plan, TimeSpan dayStart, PlanBlock? keep,
        int preferredFocus, int preferredBreak, TimeSpan softEnd)
    {
        var list = new List<PlanBlock>(plan);
        if (list.Count == 0) return list;

        var t = dayStart;
        int segmentStart = 0;
        for (int a = 0; a <= list.Count; a++)
        {
            bool atEnd = a == list.Count;
            if (!atEnd && !list[a].IsAnchored) continue;

            int sum = 0;
            for (int k = segmentStart; k < a; k++) sum += list[k].Minutes;

            if (atEnd)
            {
                // After the last fixed block, the day still ends at the usual time.
                int wanted = Math.Max(0, (int)Math.Round((softEnd - t).TotalMinutes));
                if (wanted > sum) AddFill(list, a, wanted - sum, preferredFocus, preferredBreak);
                else if (wanted < sum) Shrink(list, segmentStart, a, sum - wanted, keep, preferredFocus, preferredBreak);
                break;
            }

            var anchorStart = list[a].AnchorStart!.Value;
            int needed = Math.Max(0, (int)Math.Round((anchorStart - t).TotalMinutes));
            if (sum < needed) a += AddFill(list, a, needed - sum, preferredFocus, preferredBreak);
            else if (sum > needed) a += Shrink(list, segmentStart, a, sum - needed, keep, preferredFocus, preferredBreak);

            t = anchorStart + TimeSpan.FromMinutes(list[a].Minutes);
            segmentStart = a + 1;
        }
        return list;
    }

    /// <summary>Trims <paramref name="amount"/> minutes from list[from..to), latest blocks first. Returns the change in count.</summary>
    private static int Shrink(List<PlanBlock> list, int from, int to, int amount, PlanBlock? keep, int preferredFocus, int preferredBreak)
    {
        int shift = 0;
        int remaining = amount;
        for (int k = to - 1; k >= from && remaining > 0; k--)
        {
            var b = list[k];
            if (ReferenceEquals(b, keep)) continue;
            if (b.Minutes - remaining >= MinBlock)
            {
                b.Minutes -= remaining;
                remaining = 0;
            }
            else
            {
                remaining -= b.Minutes;
                list.RemoveAt(k);
                shift--;
            }
        }

        if (remaining > 0 && keep != null && list.IndexOf(keep) is int ki && ki >= from && ki < to + shift)
        {
            // Nothing else could give way: the block you're changing is limited instead,
            // and if there's no room for it at all (say, between two back-to-back meetings), it can't go there.
            if (remaining >= keep.Minutes)
            {
                remaining -= keep.Minutes;
                list.RemoveAt(ki);
                shift--;
            }
            else
            {
                keep.Minutes -= remaining;
                remaining = 0;
            }
        }

        // Removing a whole block can free a little more than needed; fill that back in.
        if (remaining < 0) shift += AddFill(list, to + shift, -remaining, preferredFocus, preferredBreak);
        return shift;
    }

    /// <summary>Puts <paramref name="minutes"/> of focus sessions and breaks before list[index]. Returns how many blocks were inserted.</summary>
    private static int AddFill(List<PlanBlock> list, int index, int minutes, int preferredFocus, int preferredBreak)
    {
        if (minutes <= 0) return 0;
        var before = index > 0 ? list[index - 1] : null;
        bool beforeIsFlexibleBreak = before != null && !before.IsAnchored && before.Kind == BlockKind.Break;
        bool beforeIsFlexibleFocus = before != null && !before.IsAnchored && before.IsFocus;

        if (minutes < Defaults.FocusMin)
        {
            // Too short for a focus session: lengthen the break that's there, or add one.
            if (beforeIsFlexibleBreak)
            {
                before!.Minutes += minutes;
                return 0;
            }
            if (beforeIsFlexibleFocus && minutes < Defaults.BreakMin)
            {
                before!.Minutes += minutes;
                return 0;
            }
            list.Insert(index, new PlanBlock { Kind = BlockKind.Break, Name = "Break", Minutes = minutes });
            return 1;
        }

        // Number new sessions after the highest "Focus session N" already in the plan.
        int focusSoFar = list.Select(b => b.Name.StartsWith("Focus session ", StringComparison.Ordinal) &&
                                          int.TryParse(b.Name.AsSpan(14), out int n) ? n : 0)
                             .DefaultIfEmpty(0).Max();
        int breakLength = Math.Clamp(preferredBreak, Defaults.BreakMin, Defaults.BreakMax);
        var fill = new List<PlanBlock>();
        if (before != null && before.IsFocus && minutes - breakLength >= Defaults.FocusMin)
        {
            // Don't put two focus sessions back to back.
            fill.Add(new PlanBlock { Kind = BlockKind.Break, Name = "Break", Minutes = breakLength });
            fill.AddRange(AutoPlanner.Fill(minutes - breakLength, preferredFocus, preferredBreak, focusSoFar));
        }
        else
        {
            fill.AddRange(AutoPlanner.Fill(minutes, preferredFocus, preferredBreak, focusSoFar));
        }
        list.InsertRange(index, fill);
        return fill.Count;
    }
    // ---------- Keeping sessions and breaks within their allowed lengths ----------

    /// <summary>
    /// After an edit, looks at each stretch between fixed blocks that the edit touched. If any session
    /// or break there is outside the allowed lengths (25 to 40 and 4 to 7 minutes by default), or two
    /// sessions or two breaks ended up side by side, the stretch is evened out: sessions and breaks
    /// alternate and share the time within the limits. The block you edited keeps its length, and
    /// existing sessions keep their names, tasks and blocked apps where possible.
    /// </summary>
    /// <param name="before">The plan before the edit, with each block's length then.</param>
    /// <param name="lockedCount">Blocks at the start that can't change (already happened, or happening now).</param>
    public static List<PlanBlock> Rebalance(List<PlanBlock> plan, List<(PlanBlock Block, int Minutes)> before,
        PlanBlock? keep, int preferredFocus, int preferredBreak, int lockedCount = 0)
    {
        var list = new List<PlanBlock>(plan);
        var beforeMinutes = before.ToDictionary(b => b.Block, b => b.Minutes);
        var beforeKey = SegmentKeys(before.Select(b => b.Block).ToList());
        var afterKey = SegmentKeys(list);

        // Which stretches did this edit touch?
        var touched = new HashSet<object>();
        foreach (var b in list)
        {
            bool changed = !beforeMinutes.TryGetValue(b, out int m) || m != b.Minutes || ReferenceEquals(b, keep);
            if (changed && !b.IsAnchored) touched.Add(afterKey[b]);
        }
        foreach (var (b, _) in before)
        {
            if (b.IsAnchored) continue;
            var oldKey = beforeKey[b];
            if (!afterKey.TryGetValue(b, out var newKey)) touched.Add(oldKey); // removed
            else if (!Equals(oldKey, newKey)) { touched.Add(oldKey); touched.Add(newKey); } // moved
        }

        // Rebuild each touched stretch that breaks the rules.
        int start = lockedCount;
        while (start < list.Count)
        {
            if (list[start].IsAnchored) { start++; continue; }
            int end = start;
            while (end < list.Count && !list[end].IsAnchored) end++;
            var stretch = list.GetRange(start, end - start);
            object key = end < list.Count ? list[end] : EndKey;
            if (touched.Contains(key) && BreaksRules(stretch, keep, start > 0 ? list[start - 1] : null, end < list.Count ? list[end] : null))
            {
                var rebuilt = RebuildStretch(stretch, keep, start > 0 ? list[start - 1] : null, end < list.Count ? list[end] : null,
                    preferredFocus, preferredBreak, list);
                list.RemoveRange(start, end - start);
                list.InsertRange(start, rebuilt);
                end = start + rebuilt.Count;
            }
            start = end;
        }
        return list;
    }

    private static readonly object EndKey = new();

    /// <summary>For each block, the fixed block that ends its stretch (or the end of the day).</summary>
    private static Dictionary<PlanBlock, object> SegmentKeys(List<PlanBlock> list)
    {
        var keys = new Dictionary<PlanBlock, object>();
        object current = EndKey;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            keys[list[i]] = current;
            if (list[i].IsAnchored) current = list[i];
        }
        return keys;
    }

    private static bool InFocusRange(int m) => m >= Defaults.FocusMin && m <= Defaults.FocusMax;
    private static bool InBreakRange(int m) => m >= Defaults.BreakMin && m <= Defaults.BreakMax;

    private static bool BreaksRules(List<PlanBlock> stretch, PlanBlock? keep, PlanBlock? before, PlanBlock? after)
    {
        for (int i = 0; i < stretch.Count; i++)
        {
            var b = stretch[i];
            if (!ReferenceEquals(b, keep))
            {
                if (b.IsFocus && !InFocusRange(b.Minutes)) return true;
                if (b.Kind == BlockKind.Break && !InBreakRange(b.Minutes)) return true;
            }
            var prev = i > 0 ? stretch[i - 1] : before;
            if (prev != null && !prev.IsAnchored && prev.Kind == b.Kind && b.Kind != BlockKind.Meeting) return true;
        }
        return false;
    }

    /// <summary>Rebuilds a stretch, keeping <paramref name="keep"/> where it is and evening out the parts around it.</summary>
    private static List<PlanBlock> RebuildStretch(List<PlanBlock> stretch, PlanBlock? keep, PlanBlock? before, PlanBlock? after,
        int preferredFocus, int preferredBreak, List<PlanBlock> whole)
    {
        int k = keep != null ? stretch.IndexOf(keep) : -1;
        if (k < 0) return RebuildPart(stretch, before, after, preferredFocus, preferredBreak, whole);

        var beforePart = stretch.GetRange(0, k);
        var afterPart = stretch.GetRange(k + 1, stretch.Count - k - 1);
        int beforeTotal = beforePart.Sum(b => b.Minutes), afterTotal = afterPart.Sum(b => b.Minutes);
        bool leadBefore = before != null && (before.IsFocus || before.IsMeeting), trailBefore = keep!.IsFocus;
        bool leadAfter = keep.IsFocus || keep.IsMeeting, trailAfter = after != null && after.IsFocus;
        int prefB = Math.Clamp(preferredBreak, Defaults.BreakMin, Defaults.BreakMax);

        // If one side can't be filled within the limits, slide the edited block so the other side takes the extra.
        if (!Fits(afterTotal, leadAfter, trailAfter, preferredFocus, preferredBreak))
        {
            int smallest = leadAfter || trailAfter ? Math.Min(prefB, afterTotal) : 0;
            int moved = afterTotal - smallest;
            if (moved > 0 && Fits(beforeTotal + moved, leadBefore, trailBefore, preferredFocus, preferredBreak))
            {
                beforeTotal += moved;
                afterTotal = smallest;
            }
        }
        else if (!Fits(beforeTotal, leadBefore, trailBefore, preferredFocus, preferredBreak))
        {
            int smallest = leadBefore || trailBefore ? Math.Min(prefB, beforeTotal) : 0;
            int moved = beforeTotal - smallest;
            if (moved > 0 && Fits(afterTotal + moved, leadAfter, trailAfter, preferredFocus, preferredBreak))
            {
                afterTotal += moved;
                beforeTotal = smallest;
            }
        }

        var result = new List<PlanBlock>();
        result.AddRange(RebuildPart(beforePart, before, keep, preferredFocus, preferredBreak, whole, beforeTotal));
        result.Add(keep);
        result.AddRange(RebuildPart(afterPart, keep, after, preferredFocus, preferredBreak, whole, afterTotal));
        return result;
    }

    /// <summary>Replaces a run of blocks with evenly sized, alternating sessions and breaks of the same total length.</summary>
    private static List<PlanBlock> RebuildPart(List<PlanBlock> part, PlanBlock? before, PlanBlock? after,
        int preferredFocus, int preferredBreak, List<PlanBlock> whole, int? totalOverride = null)
    {
        int total = totalOverride ?? part.Sum(b => b.Minutes);
        if (total <= 0) return new List<PlanBlock>();

        // Start with a break after a focus session (or a meeting); end with one before a focus session.
        bool leadBreak = before != null && (before.IsFocus || before.IsMeeting);
        bool trailBreak = after != null && after.IsFocus;
        var lengths = PlanLengths(total, leadBreak, trailBreak, preferredFocus, preferredBreak);

        // Reuse existing blocks of the right kind, in order, so names, tasks and blocking carry over.
        var focuses = new Queue<PlanBlock>(part.Where(b => b.IsFocus));
        var breaks = new Queue<PlanBlock>(part.Where(b => b.Kind == BlockKind.Break));
        var template = part.FirstOrDefault(b => b.IsFocus) ?? (before?.IsFocus == true ? before : null) ??
                       (after?.IsFocus == true ? after : null) ?? whole.FirstOrDefault(b => b.IsFocus);
        int nextNumber = whole.Select(b => b.Name.StartsWith("Focus session ", StringComparison.Ordinal) &&
                                           int.TryParse(b.Name.AsSpan(14), out int n) ? n : 0).DefaultIfEmpty(0).Max() + 1;

        var result = new List<PlanBlock>();
        foreach (var (kind, minutes) in lengths)
        {
            PlanBlock block;
            if (kind == BlockKind.Focus)
            {
                block = focuses.Count > 0 ? focuses.Dequeue() : new PlanBlock
                {
                    Kind = BlockKind.Focus,
                    Name = $"Focus session {nextNumber++}",
                    BlockedApps = template?.BlockedApps.Select(a => a.Copy()).ToList() ?? new(),
                    BlockListIds = template != null ? new List<string>(template.BlockListIds) : new()
                };
            }
            else
            {
                block = breaks.Count > 0 ? breaks.Dequeue() : new PlanBlock { Kind = BlockKind.Break, Name = "Break" };
            }
            block.Minutes = minutes;
            result.Add(block);
        }

        // Sessions that no longer fit hand their tasks to the last session that's left.
        var heir = result.LastOrDefault(b => b.IsFocus) ?? (before?.IsFocus == true ? before : null) ?? (after?.IsFocus == true ? after : null);
        foreach (var dropped in focuses)
            if (heir != null)
                foreach (var task in dropped.Tasks.ToList())
                    heir.Tasks.Add(task.Copy());
        return result;
    }

    /// <summary>True if <paramref name="total"/> minutes can be filled with everything inside the limits.</summary>
    private static bool Fits(int total, bool leadBreak, bool trailBreak, int preferredFocus, int preferredBreak)
    {
        if (total == 0) return true;
        var lengths = PlanLengths(total, leadBreak, trailBreak, preferredFocus, preferredBreak);
        return lengths.All(l => l.Kind == BlockKind.Focus ? InFocusRange(l.Minutes) : InBreakRange(l.Minutes));
    }

    /// <summary>
    /// Chooses how many sessions and how long each session and break should be, to fill exactly
    /// <paramref name="total"/> minutes within the allowed lengths, as close to your usual lengths as possible.
    /// </summary>
    internal static List<(BlockKind Kind, int Minutes)> PlanLengths(int total, bool leadBreak, bool trailBreak,
        int preferredFocus, int preferredBreak)
    {
        int prefF = Math.Clamp(preferredFocus, Defaults.FocusMin, Defaults.FocusMax);
        int prefB = Math.Clamp(preferredBreak, Defaults.BreakMin, Defaults.BreakMax);
        var result = new List<(BlockKind, int)>();
        if (total <= 0) return result;

        // Try every combination; the best one stays inside the limits if that's possible at all,
        // and otherwise goes over them by as few minutes as possible. Among equals, it's the one
        // closest to your usual lengths.
        int bestF = 0, bestB = prefB;
        bool bestExtra = false;
        double bestScore = double.MaxValue;

        // Option: no focus session, just one break.
        {
            double over = Math.Max(0, total - Defaults.BreakMax) + Math.Max(0, Defaults.BreakMin - total);
            bestScore = over * 100 + 50; // a lone break is a last resort
        }

        foreach (bool extra in trailBreak ? new[] { false } : new[] { false, true })
        {
            for (int f = 1; f <= total / 15 + 1; f++)
            {
                int breakCount = f - 1 + (leadBreak ? 1 : 0) + (trailBreak || extra ? 1 : 0);
                for (int b = Defaults.BreakMin; b <= 20; b++)
                {
                    if (breakCount == 0 && b != prefB) continue;
                    int focusTotal = total - breakCount * b;
                    if (focusTotal < f * 10) continue;
                    double focus = focusTotal / (double)f;
                    double over = f * (Math.Max(0, Defaults.FocusMin - focus) + Math.Max(0, focus - Defaults.FocusMax))
                                  + breakCount * Math.Max(0, b - Defaults.BreakMax);
                    double score = over * 100 + Math.Abs(focus - prefF) + Math.Abs(b - prefB) + (extra ? 3 : 0);
                    if (score < bestScore) { bestScore = score; bestF = f; bestB = b; bestExtra = extra; }
                }
            }
        }

        if (bestF == 0)
        {
            result.Add((BlockKind.Break, total));
            return result;
        }

        bool trail = trailBreak || bestExtra;
        int breaks = bestF - 1 + (leadBreak ? 1 : 0) + (trail ? 1 : 0);
        int sessionsTotal = total - breaks * bestB;
        int baseLength = sessionsTotal / bestF, remainder = sessionsTotal % bestF;
        if (leadBreak) result.Add((BlockKind.Break, bestB));
        for (int i = 0; i < bestF; i++)
        {
            if (i > 0) result.Add((BlockKind.Break, bestB));
            result.Add((BlockKind.Focus, baseLength + (i < remainder ? 1 : 0)));
        }
        if (trail) result.Add((BlockKind.Break, bestB));
        return result;
    }
}
