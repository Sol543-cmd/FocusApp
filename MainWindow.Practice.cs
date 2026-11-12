using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace FocusApp;

/// <summary>
/// Concepts (your own questions and answers) and the extra unmatched answers. When rounds come up is set on the
/// practice timelines (MainWindow.Timelines.cs).
/// </summary>
public partial class MainWindow
{
    private DateTime _conceptLaterUntil = DateTime.MinValue;
    private int _conceptLaterSlot = -1;

    private void SetUpPractice()
    {
        // Chains: the moment a lesson or round closes, the next one due (at the same time) starts, back to back.
        var chain = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        bool wasBusy = false;
        chain.Tick += (_, _) =>
        {
            bool busy = VocabWindow.IsShowing || _lessonWindow != null || _lessonPrompt != null || _conceptQuiz != null;
            if (wasBusy && !busy)
            {
                try { CheckTraining(); if (_lessonWindow == null && !VocabWindow.IsShowing) CheckVocab(); }
                catch (Exception ex) { Log.Error("Practice chain", ex); }
            }
            wasBusy = VocabWindow.IsShowing || _lessonWindow != null || _lessonPrompt != null || _conceptQuiz != null;
        };
        chain.Start();
        Concepts.Load();
        ApplyExtraSettings();
    }

    private void ApplyExtraSettings()
    {
        WordsDeck.ExtraSetting = Math.Clamp(_settings.VocabExtras, 0, 3);
        Alphabet.ExtraSetting = Math.Clamp(_settings.AlphaExtras, 0, 3);
        Concepts.ExtraSetting = Math.Clamp(_settings.ConceptExtras, 0, 3);
    }

    // ---------- Concepts ----------

    private void SetConceptDone(HashSet<int> done) { _settings.ConceptDone = DoneRecord(done); SaveSettingsQuietly(); }

    /// <summary>
    /// A Greek Concepts round (from the timeline, or Practice now): the concept quiz. Learn mode uses the lesson chosen in
    /// Settings (or the most recent); the round can be switched to Review on the quiz. Later brings it back in 20 minutes.
    /// </summary>
    private void StartConceptRound(int slot, bool keepGoing = false, Action<bool>? after = null)
    {
        if (VocabWindow.IsShowing || _conceptQuiz != null) return;
        var packs = ConceptQuiz.All();
        if (packs.Count == 0)
        {
            if (slot < 0 && after == null) Info("No question packs yet: Settings, Greek practice, Question packs, then Import.", "Concept quiz");
            return;
        }
        if (slot >= 0) { var d = DoneToday(_settings.ConceptDone); d.Add(slot); SetConceptDone(d); }
        var w = new ConceptQuizWindow(VocabWindow.WorkingScreenBounds(), ConceptQuiz.Chosen(_settings.ConceptQuizLesson), canReview: true,
            laterText: slot >= 0 ? "Later" : "Close", finished: finished =>
        {
            _conceptQuiz = null;
            if (!finished && slot >= 0)
            {
                var d = DoneToday(_settings.ConceptDone); d.Remove(slot); SetConceptDone(d);
                _conceptLaterSlot = slot;
                _conceptLaterUntil = DateTime.Now.AddMinutes(20);
            }
            else if (finished) { _conceptLaterSlot = -1; if (slot >= 0) PublishPracticeDone("concepts", slot); }
            UpdateConceptSummary();
            after?.Invoke(finished);
        });
        _conceptQuiz = w;
        w.Show();
        WindowFront.Bring(w);
    }

    private ConceptQuizWindow? _conceptQuiz;

    /// <summary>The dashboard's Training menu: Greek Concepts opens the quiz now.</summary>
    private void StartConceptQuiz(bool fromMenu) { if (_conceptQuiz != null) _conceptQuiz.Activate(); else StartConceptRound(-1); }

    private void ConceptsPracticeNow_Click(object sender, RoutedEventArgs e) => StartConceptRound(-1, keepGoing: true);

    private void ManageConcepts_Click(object sender, RoutedEventArgs e)
    {
        // The old typed-in concept cards are retired; the quiz uses question packs, one per lesson.
        var w = new QuestionPacksWindow(() => _settings.ConceptQuizLesson, v => { _settings.ConceptQuizLesson = v; SaveSettingsQuietly(); UpdateConceptSummary(); }) { Owner = this };
        w.ShowDialog();
        UpdateConceptSummary();
    }

    private void UpdateConceptSummary()
    {
        if (ConceptSummary == null) return;
        var packs = ConceptQuiz.All();
        var chosen = ConceptQuiz.Chosen(_settings.ConceptQuizLesson);
        ConceptSummary.Text = packs.Count == 0 ? "No question packs yet. Import them with Question packs."
            : $"{packs.Count} lesson{(packs.Count == 1 ? "" : "s")} imported · Learn mode uses Lesson {chosen?.Lesson}" + (_settings.ConceptQuizLesson.Length == 0 ? " (the most recent)" : "") + ".";
    }

    // ---------- Settings for all three practices (extras, Concepts) ----------

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
        static string Extras(int n) => n == 0 ? "" : n == 1 ? "1 extra answer" : $"{n} extra answers";
        static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));
        string Times(string practice)
        {
            var times = SlotsFor(PerDayFor(practice), practice);
            return times.Count == 0 ? "no times on its timeline" : "at " + string.Join(", ", times.Select(t => t.ToString("t")));
        }
        void State(string key, bool on)
        {
            if (FindName(key + "State") is not System.Windows.Controls.TextBlock t) return;
            t.Text = on ? "On" : "Off";
            t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, on ? "BreakBrush" : "Muted");
        }
        bool words = _settings.On("greek"), alpha = _settings.On("alphabet"), concepts = _settings.On("concepts");
        State("Vocab", words); State("Alpha", alpha); State("Concept", concepts);
        VocabHeaderSummary.Text = !words ? "Matching Greek words to their meanings" :
            Join(Times("words"), $"{_settings.VocabWords} words a round", Extras(_settings.VocabExtras));
        AlphaHeaderSummary.Text = !alpha ? "Matching letters to their modern Greek sounds" :
            Join(Times("alphabet"), $"{_settings.AlphaLetters} letters a round", Extras(_settings.AlphaExtras));
        ConceptHeaderSummary.Text = !concepts ? "Your own questions and answers" :
            Join(Times("concepts"), $"{Concepts.Cards.Count} questions", Extras(_settings.ConceptExtras));
    }
}
