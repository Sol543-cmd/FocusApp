using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FocusApp;

/// <summary>practice-times.json (synced): the Greek practice timelines, the same on every computer.</summary>
public class SharedPracticeTimes
{
    public Dictionary<string, string> Times { get; set; } = new();
    public string TodayDate { get; set; } = "";
    public Dictionary<string, string> TodayTimes { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>practice-done-PCNAME.json (synced, one per computer): the latest round of each practice finished today.</summary>
public class PracticeDoneFile
{
    public string Day { get; set; } = "";
    /// <summary>Practice → the time (hh:mm) of the latest round finished or skipped today on that computer.</summary>
    public Dictionary<string, string> UpTo { get; set; } = new();
}

/// <summary>
/// Keeps Greek practice the same on all your computers: the practice timelines (practice-times.json) and which of
/// today's rounds are done (practice-done-PCNAME.json, one per computer so they never overwrite each other).
/// A round done on one laptop counts as done on the other, along with every earlier round that day.
/// </summary>
public partial class MainWindow
{
    private static string PracticeDoneName =>
        "practice-done-" + new string(Environment.MachineName.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray()) + ".json";

    // ---------- Timelines ----------

    /// <summary>Saves this computer's practice timelines for the others (after Use for today or Save as default).</summary>
    private void SaveSharedPracticeTimes()
    {
        try
        {
            var now = DateTime.UtcNow;
            Storage.Save("practice-times.json", new SharedPracticeTimes
            {
                Times = new Dictionary<string, string>(_settings.PracticeTimes),
                TodayDate = _settings.PracticeTodayDate ?? "",
                TodayTimes = new Dictionary<string, string>(_settings.PracticeTodayTimes),
                UpdatedUtc = now
            });
            _settings.PracticeTimesUpdatedUtc = now;
            SaveSettingsQuietly();
        }
        catch (Exception ex) { Log.Error("SaveSharedPracticeTimes", ex); }
    }

    /// <summary>
    /// Takes the timelines another computer saved, if they're newer than this computer's (rounds already done
    /// today stay done, matched by time). The first time, this computer's own timelines go into the file.
    /// </summary>
    private void AdoptSharedPracticeTimes()
    {
        try
        {
            if (!Storage.TryLoad<SharedPracticeTimes>("practice-times.json", out var shared) || shared.UpdatedUtc == default)
            {
                if (_settings.PracticeTimes.Count > 0) SaveSharedPracticeTimes();
                return;
            }
            if (shared.UpdatedUtc <= _settings.PracticeTimesUpdatedUtc) return;
            // Remember which of today's times were done, then switch to the other computer's timelines.
            var doneTimes = Practices.ToDictionary(p => p, p =>
            {
                var slots = SlotsFor(PerDayFor(p), p);
                return DoneToday(DoneRecordFor(p)).Where(i => i < slots.Count).Select(i => slots[i].TimeOfDay).ToHashSet();
            });
            _settings.PracticeTimes = new Dictionary<string, string>(shared.Times);
            _settings.PracticeTodayDate = shared.TodayDate;
            _settings.PracticeTodayTimes = new Dictionary<string, string>(shared.TodayTimes);
            _settings.PracticeTimesUpdatedUtc = shared.UpdatedUtc;
            foreach (var p in Practices)
            {
                var slots = SlotsFor(PerDayFor(p), p);
                var done = new HashSet<int>();
                for (int i = 0; i < slots.Count; i++) if (doneTimes[p].Contains(slots[i].TimeOfDay)) done.Add(i);
                SetDoneRecordFor(p, DoneRecord(done));
            }
            SaveSettingsQuietly();
            Log.Write("Practice timelines updated from another computer");
            try { UpdateGreekHeaders(); } catch (Exception ex) { Log.Error("AdoptSharedPracticeTimes.Headers", ex); }
        }
        catch (Exception ex) { Log.Error("AdoptSharedPracticeTimes", ex); }
    }

    // ---------- Rounds done ----------

    /// <summary>A round finished (or skipped) here: tells your other computers that this round, and earlier ones today, are done.</summary>
    private void PublishPracticeDone(string practice, int slot)
    {
        try
        {
            var slots = SlotsFor(PerDayFor(practice), practice);
            if (slot < 0 || slot >= slots.Count) return;
            string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var file = Storage.TryLoad<PracticeDoneFile>(PracticeDoneName, out var f) && f.Day == today ? f : new PracticeDoneFile { Day = today };
            string time = slots[slot].TimeOfDay.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            if (file.UpTo.TryGetValue(practice, out var had) && string.CompareOrdinal(had, time) >= 0) return;
            file.UpTo[practice] = time;
            Storage.Save(PracticeDoneName, file);
        }
        catch (Exception ex) { Log.Error("PublishPracticeDone", ex); }
    }

    /// <summary>Marks rounds done that you did on another computer today (that round and every earlier one).</summary>
    private void MergePracticeDoneFromOtherComputers()
    {
        try
        {
            string folder = Storage.SharedFolder;
            if (!Directory.Exists(folder)) return;
            string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var upTo = new Dictionary<string, TimeSpan>();
            foreach (var path in Directory.GetFiles(folder, "practice-done-*.json"))
            {
                string name = Path.GetFileName(path);
                if (name.Equals(PracticeDoneName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Storage.TryLoad<PracticeDoneFile>(name, out var f) || f.Day != today) continue;
                foreach (var (p, t) in f.UpTo)
                    if (TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var ts) && (!upTo.TryGetValue(p, out var cur) || ts > cur)) upTo[p] = ts;
            }
            bool changed = false;
            foreach (var (p, until) in upTo)
            {
                if (!Practices.Contains(p)) continue;
                var slots = SlotsFor(PerDayFor(p), p);
                var done = DoneToday(DoneRecordFor(p));
                int before = done.Count;
                for (int i = 0; i < slots.Count; i++) if (slots[i].TimeOfDay <= until) done.Add(i);
                if (done.Count == before) continue;
                SetDoneRecordFor(p, DoneRecord(done));
                changed = true;
                switch (p)
                {
                    case "alphabet": if (_alphaLaterSlot >= 0 && done.Contains(_alphaLaterSlot)) _alphaLaterSlot = -1; break;
                    case "concepts": if (_conceptLaterSlot >= 0 && done.Contains(_conceptLaterSlot)) _conceptLaterSlot = -1; break;
                    default: if (_vocabLaterSlot >= 0 && done.Contains(_vocabLaterSlot)) _vocabLaterSlot = -1; break;
                }
            }
            if (changed) { SaveSettingsQuietly(); Log.Write("Practice: rounds done on another computer marked done here"); }
        }
        catch (Exception ex) { Log.Error("MergePracticeDone", ex); }
    }
}
