using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace FocusApp;

/// <summary>
/// Greek practice beyond the session schedule: Concepts (your own questions and answers), practice that pops up
/// now and then while Focus runs outside focus sessions, and the extra unmatched answers. During a running
/// focus day or session, the session schedules ("rounds a day") take over and the outside schedule pauses.
/// </summary>
public partial class MainWindow
{
    private DateTime _conceptLaterUntil = DateTime.MinValue;
    private int _conceptLaterSlot = -1;
    private readonly Dictionary<string, DateTime> _awayLast = new();
    private DateTime _lastAwayRound = DateTime.MinValue;

    private void SetUpPractice()
    {
        Concepts.Load();
        ApplyExtraSettings();
        // Outside-session practice waits its full interval after Focus opens before the first round.
        foreach (var key in new[] { "words", "alphabet", "concepts" }) _awayLast[key] = DateTime.Now;
    }

    private void ApplyExtraSettings()
    {
        WordsDeck.ExtraSetting = Math.Clamp(_settings.VocabExtras, 0, 3);
        Alphabet.ExtraSetting = Math.Clamp(_settings.AlphaExtras, 0, 3);
        Concepts.ExtraSetting = Math.Clamp(_settings.ConceptExtras, 0, 3);
    }

    // ---------- Concepts: the session schedule ----------

    private void CheckConcepts()
    {
        if (!_settings.On("concepts") || VocabWindow.IsShowing || DateTime.Now < _conceptLaterUntil || Concepts.Cards.Count < 2) return;
        if (Application.Current.Windows.OfType<FlashWindow>().Any()) return;
        var now = DateTime.Now;
        var done = DoneToday(_settings.ConceptDone);
        var slots = SlotsFor(_settings.ConceptPerDay, "concepts");
        int due = -1;
        for (int i = 0; i < slots.Count; i++)
            if (!done.Contains(i) && slots[i] <= now && now - slots[i] < TimeSpan.FromHours(3)) { due = i; break; }
        if (due < 0 && _conceptLaterSlot >= 0 && !done.Contains(_conceptLaterSlot)) due = _conceptLaterSlot;
        if (due < 0 || InMeetingOrLunch()) return;
        StartConceptRound(due);
    }

    private void SetConceptDone(HashSet<int> done) { _settings.ConceptDone = DoneRecord(done); SaveSettingsQuietly(); }

    private void StartConceptRound(int slot, bool keepGoing = false, Action<bool>? after = null)
    {
        if (VocabWindow.IsShowing) return;
        int size = Math.Clamp(_settings.ConceptCount, 2, 8);
        var cards = Concepts.PickRound(size);
        if (cards.Count < 2)
        {
            if (slot < 0 && after == null) Info("Add at least two questions and answers first: Settings, Greek practice, Manage concepts.", "Concepts");
            return;
        }
        if (slot >= 0) { var d = DoneToday(_settings.ConceptDone); d.Add(slot); SetConceptDone(d); }
        VocabWindow.ShowOnAllScreens(cards, finished =>
        {
            if (!finished && slot >= 0)
            {
                var d = DoneToday(_settings.ConceptDone); d.Remove(slot); SetConceptDone(d);
                _conceptLaterSlot = slot;
                _conceptLaterUntil = DateTime.Now.AddMinutes(20);
            }
            else if (finished) _conceptLaterSlot = -1;
            UpdateConceptSummary();
            after?.Invoke(finished);
        },
        nextRound: keepGoing ? n => { int k = Math.Clamp(n, 2, 8); if (k != _settings.ConceptCount) { _settings.ConceptCount = k; SaveSettingsQuietly(); } return Concepts.PickRound(k); } : null,
        startSize: size, deck: Concepts.Instance);
    }

    private void DashConcepts_Click(object sender, RoutedEventArgs e) => StartConceptRound(-1, keepGoing: true);
    private void ConceptsPracticeNow_Click(object sender, RoutedEventArgs e) => StartConceptRound(-1, keepGoing: true);

    private void ManageConcepts_Click(object sender, RoutedEventArgs e)
    {
        var w = new ConceptsWindow { Owner = this };
        w.ShowDialog();
        UpdateConceptSummary();
    }

    private void UpdateConceptSummary()
    {
        if (ConceptSummary == null) return;
        int n = Concepts.Cards.Count, due = Concepts.Cards.Count(c => c.Due <= DateTime.Now);
        ConceptSummary.Text = n == 0 ? "No questions yet. Add them with Manage concepts."
            : $"{n} question{(n == 1 ? "" : "s")}, {due} due now.";
    }

    // ---------- Outside focus sessions: now and then, while Focus runs ----------

    private bool InPracticeHours(DateTime now, string practice)
    {
        var (from, to) = HoursFor(practice);
        return now.TimeOfDay >= from && now.TimeOfDay <= to;
    }

    /// <summary>
    /// While no focus day or session is running: each practice you switched on for outside sessions comes up
    /// every so many minutes (within your practice hours, never in a meeting or lunch, at least 15 minutes apart).
    /// </summary>
    private void CheckAwayPractice()
    {
        if (VocabWindow.IsShowing || Application.Current.Windows.OfType<FlashWindow>().Any()) return;
        var now = DateTime.Now;
        if (now - _lastAwayRound < TimeSpan.FromMinutes(15)) return;
        var options = new List<(string Key, int Minutes, Action<Action<bool>> Start)>();
        if (_settings.On("greek") && _settings.VocabAwayOn) options.Add(("words", _settings.VocabAwayMinutes, done => StartVocabRound(-1, done)));
        if (_settings.On("alphabet") && _settings.AlphaAwayOn) options.Add(("alphabet", _settings.AlphaAwayMinutes, done => StartAlphaRound(-1, false, done)));
        if (_settings.On("concepts") && _settings.ConceptAwayOn && Concepts.Cards.Count >= 2) options.Add(("concepts", _settings.ConceptAwayMinutes, done => StartConceptRound(-1, false, done)));
        var due = options
            .Select(o => (o.Key, o.Minutes, o.Start, Over: now - (_awayLast.TryGetValue(o.Key, out var last) ? last : now) - TimeSpan.FromMinutes(Math.Clamp(o.Minutes, 10, 600))))
            .Where(o => o.Over >= TimeSpan.Zero && InPracticeHours(now, o.Key == "words" ? "words" : o.Key)) // each within its own hours
            .OrderByDescending(o => o.Over)
            .FirstOrDefault();
        if (due.Key == null || InMeetingOrLunch()) return;
        string key = due.Key;
        int minutes = Math.Clamp(due.Minutes, 10, 600);
        _lastAwayRound = now;
        _awayLast[key] = now; // so it isn't started twice
        due.Start(finished =>
        {
            _lastAwayRound = DateTime.Now;
            // Later: back in 20 minutes rather than a whole interval.
            _awayLast[key] = finished ? DateTime.Now : DateTime.Now - TimeSpan.FromMinutes(minutes) + TimeSpan.FromMinutes(20);
        });
    }

    // ---------- Settings for all three practices (extras, outside sessions, Concepts) ----------

    private void FillPracticeSettings()
    {
        VocabExtrasBox.Value = Math.Clamp(_settings.VocabExtras, 0, 3);
        AlphaExtrasBox.Value = Math.Clamp(_settings.AlphaExtras, 0, 3);
        ConceptExtrasBox.Value = Math.Clamp(_settings.ConceptExtras, 0, 3);
        VocabAwayCheck.IsChecked = _settings.VocabAwayOn;
        VocabAwayBox.Value = Math.Clamp(_settings.VocabAwayMinutes, 10, 600);
        AlphaAwayCheck.IsChecked = _settings.AlphaAwayOn;
        AlphaAwayBox.Value = Math.Clamp(_settings.AlphaAwayMinutes, 10, 600);
        ConceptAwayCheck.IsChecked = _settings.ConceptAwayOn;
        ConceptAwayBox.Value = Math.Clamp(_settings.ConceptAwayMinutes, 10, 600);
        ConceptOnCheck.IsChecked = _settings.On("concepts");
        ConceptPerDayBox.Value = Math.Clamp(_settings.ConceptPerDay, 0, 12);
        ConceptCountBox.Value = Math.Clamp(_settings.ConceptCount, 2, 8);
        ConceptOptions.Visibility = _settings.On("concepts") ? Visibility.Visible : Visibility.Collapsed;
        var (af, at) = HoursFor("alphabet");
        var (cf, ct) = HoursFor("concepts");
        AlphaFromBox.Text = DateTime.Today.Add(af).ToString("t");
        AlphaToBox.Text = DateTime.Today.Add(at).ToString("t");
        ConceptFromBox.Text = DateTime.Today.Add(cf).ToString("t");
        ConceptToBox.Text = DateTime.Today.Add(ct).ToString("t");
        UpdateConceptSummary();
        UpdateGreekHeaders();
    }

    /// <summary>Saves the extras, outside-session and Concepts settings, and each practice's hours; returns a problem to show, or null.</summary>
    private string? SavePracticeSettings()
    {
        if (!TryParseTime(AlphaFromBox.Text, out var af) || !TryParseTime(AlphaToBox.Text, out var at) || at <= af)
            return "Enter the alphabet practice hours like 8:30 AM to 4:00 PM.";
        if (!TryParseTime(ConceptFromBox.Text, out var cf) || !TryParseTime(ConceptToBox.Text, out var ct) || ct <= cf)
            return "Enter the concepts practice hours like 8:30 AM to 4:00 PM.";
        _settings.AlphaFrom = af.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        _settings.AlphaTo = at.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        _settings.ConceptFrom = cf.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        _settings.ConceptTo = ct.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        foreach (var box in new[] { VocabExtrasBox, AlphaExtrasBox, ConceptExtrasBox, VocabAwayBox, AlphaAwayBox, ConceptAwayBox, ConceptPerDayBox, ConceptCountBox }) box.Commit();
        _settings.VocabExtras = VocabExtrasBox.Value;
        _settings.AlphaExtras = AlphaExtrasBox.Value;
        _settings.ConceptExtras = ConceptExtrasBox.Value;
        _settings.VocabAwayOn = VocabAwayCheck.IsChecked == true;
        _settings.VocabAwayMinutes = VocabAwayBox.Value;
        _settings.AlphaAwayOn = AlphaAwayCheck.IsChecked == true;
        _settings.AlphaAwayMinutes = AlphaAwayBox.Value;
        _settings.ConceptAwayOn = ConceptAwayCheck.IsChecked == true;
        _settings.ConceptAwayMinutes = ConceptAwayBox.Value;
        _settings.Features["concepts"] = ConceptOnCheck.IsChecked == true;
        _settings.ConceptPerDay = ConceptPerDayBox.Value;
        _settings.ConceptCount = ConceptCountBox.Value;
        ApplyExtraSettings();
        Dispatcher.BeginInvoke(new Action(UpdateGreekHeaders)); // after the switches are saved too
        return null;
    }

    private void ConceptSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (ConceptOptions == null || ConceptOnCheck == null) return;
        ConceptOptions.Visibility = ConceptOnCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- The Greek practice page: a card for each practice, opened by clicking it ----------

    private void GreekHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key } || FindName(key + "Body") is not FrameworkElement body) return;
        bool open = body.Visibility != Visibility.Visible;
        body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (FindName(key + "Chevron") is System.Windows.Controls.TextBlock chevron) chevron.Text = open ? "▾" : "▸";
    }

    /// <summary>Each card's one-line summary and On/Off, from the current settings.</summary>
    private void UpdateGreekHeaders()
    {
        if (VocabHeaderSummary == null) return;
        static string Outside(bool on, int minutes) => on ? $"every {minutes} min outside sessions" : "";
        static string Extras(int n) => n == 0 ? "" : n == 1 ? "1 extra answer" : $"{n} extra answers";
        static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));
        string Hours(string practice) { var (f, t) = HoursFor(practice); return $"{DateTime.Today.Add(f):t} to {DateTime.Today.Add(t):t}"; }
        void State(string key, bool on)
        {
            if (FindName(key + "State") is not System.Windows.Controls.TextBlock t) return;
            t.Text = on ? "On" : "Off";
            t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, on ? "BreakBrush" : "Muted");
        }
        bool words = _settings.On("greek"), alpha = _settings.On("alphabet"), concepts = _settings.On("concepts");
        State("Vocab", words); State("Alpha", alpha); State("Concept", concepts);
        VocabHeaderSummary.Text = !words ? "Matching Greek words to their meanings" :
            Join($"{_settings.VocabPerDay} a day in sessions", Outside(_settings.VocabAwayOn, _settings.VocabAwayMinutes), Hours("words"), $"{_settings.VocabWords} words a round", Extras(_settings.VocabExtras));
        AlphaHeaderSummary.Text = !alpha ? "Matching letters to their modern Greek sounds" :
            Join($"{_settings.AlphaPerDay} a day in sessions", Outside(_settings.AlphaAwayOn, _settings.AlphaAwayMinutes), Hours("alphabet"), $"{_settings.AlphaLetters} letters a round", Extras(_settings.AlphaExtras));
        ConceptHeaderSummary.Text = !concepts ? "Your own questions and answers" :
            Join($"{_settings.ConceptPerDay} a day in sessions", Outside(_settings.ConceptAwayOn, _settings.ConceptAwayMinutes), Hours("concepts"), $"{Concepts.Cards.Count} questions", Extras(_settings.ConceptExtras));
    }
}
