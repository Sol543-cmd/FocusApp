using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace FocusApp;

/// <summary>
/// When Greek practice pops up: the number of rounds a day you choose, spread evenly between your
/// start and end times. A round never appears during a meeting or lunch; it waits for the next
/// focus session or break instead. "Later" brings it back in 20 minutes.
/// </summary>
public partial class MainWindow
{
    private DateTime _vocabLaterUntil = DateTime.MinValue;
    private int _vocabLaterSlot = -1;

    private void SetUpVocab()
    {
        Vocab.RetiredLevel = Math.Clamp(_settings.VocabRetireAfter, 3, 12);
        Vocab.RetireThreshold = Math.Clamp(_settings.VocabRetireThreshold, 10, 5000);
        Vocab.Load();
        Alphabet.IncludePairs = _settings.AlphaPairs;
        Alphabet.Load();
        PomoTasksCheck.IsChecked = _settings.PomoShowTasks;
        SetUpPractice();
        Vocab.Changed += () => Dispatcher.BeginInvoke(new Action(UpdateVocabSummary));
    }

    /// <summary>Today's practice times for words (or, with <paramref name="perDay"/>, for the alphabet).</summary>
    private List<DateTime> VocabSlots() => SlotsFor(_settings.VocabPerDay, "words");

    /// <summary>A practice's own hours ("words", "alphabet" or "concepts"); Alphabet and Concepts use the Words hours until you set theirs.</summary>
    private (TimeSpan From, TimeSpan To) HoursFor(string practice)
    {
        static TimeSpan? Read(string s) => TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : null;
        var wordsFrom = Read(_settings.VocabFrom) ?? new TimeSpan(8, 30, 0);
        var wordsTo = Read(_settings.VocabTo) ?? new TimeSpan(16, 0, 0);
        return practice switch
        {
            "alphabet" => (Read(_settings.AlphaFrom) ?? wordsFrom, Read(_settings.AlphaTo) ?? wordsTo),
            "concepts" => (Read(_settings.ConceptFrom) ?? wordsFrom, Read(_settings.ConceptTo) ?? wordsTo),
            _ => (wordsFrom, wordsTo)
        };
    }

    private List<DateTime> SlotsFor(int perDay, string practice)
    {
        var (from, to) = HoursFor(practice);
        if (to <= from) to = from + TimeSpan.FromHours(1);
        int n = Math.Clamp(perDay, 0, 12);
        if (n == 0) return new List<DateTime>();
        return Enumerable.Range(0, n).Select(k => DateTime.Today + from + TimeSpan.FromTicks((to - from).Ticks * (2 * k + 1) / (2 * n))).ToList();
    }

    private HashSet<int> VocabDoneToday() => DoneToday(_settings.VocabDone);

    private static HashSet<int> DoneToday(string? record)
    {
        string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var parts = (record ?? "").Split('|');
        if (parts.Length != 2 || parts[0] != today) return new HashSet<int>();
        return parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out int i) ? i : -1).Where(i => i >= 0).ToHashSet();
    }

    private void SetVocabDone(HashSet<int> done) { _settings.VocabDone = DoneRecord(done); SaveSettingsQuietly(); }
    private void SetAlphaDone(HashSet<int> done) { _settings.AlphaDone = DoneRecord(done); SaveSettingsQuietly(); }
    private static string DoneRecord(HashSet<int> done) =>
        DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + string.Join(",", done.OrderBy(i => i));

    private DateTime _alphaLaterUntil = DateTime.MinValue;
    private int _alphaLaterSlot = -1;

    /// <summary>The alphabet's rounds: same hours as the words, never in meetings or lunch, never at the same time.</summary>
    private void CheckAlphabet()
    {
        if (!_settings.On("alphabet") || VocabWindow.IsShowing || DateTime.Now < _alphaLaterUntil) { if (!VocabWindow.IsShowing) CheckConcepts(); return; }
        if (Application.Current.Windows.OfType<FlashWindow>().Any()) return;
        var now = DateTime.Now;
        var done = DoneToday(_settings.AlphaDone);
        var slots = SlotsFor(_settings.AlphaPerDay, "alphabet");
        int due = -1;
        for (int i = 0; i < slots.Count; i++)
            if (!done.Contains(i) && slots[i] <= now && now - slots[i] < TimeSpan.FromHours(3)) { due = i; break; }
        if (due < 0 && _alphaLaterSlot >= 0 && !done.Contains(_alphaLaterSlot)) due = _alphaLaterSlot;
        if (due < 0) { CheckConcepts(); return; }
        if (InMeetingOrLunch()) return;
        StartAlphaRound(due);
    }

    private void StartAlphaRound(int slot, bool keepGoing = false, Action<bool>? after = null)
    {
        if (VocabWindow.IsShowing) return;
        Alphabet.IncludePairs = _settings.AlphaPairs;
        int size = Math.Clamp(_settings.AlphaLetters, 3, 10);
        var letters = Alphabet.PickRound(size);
        if (letters.Count < 2) return;
        if (slot >= 0) { var d = DoneToday(_settings.AlphaDone); d.Add(slot); SetAlphaDone(d); }
        VocabWindow.ShowOnAllScreens(letters, finished =>
        {
            if (!finished && slot >= 0)
            {
                var d = DoneToday(_settings.AlphaDone); d.Remove(slot); SetAlphaDone(d);
                _alphaLaterSlot = slot;
                _alphaLaterUntil = DateTime.Now.AddMinutes(20);
            }
            else if (finished) _alphaLaterSlot = -1;
            UpdateAlphaSummary();
            after?.Invoke(finished);
        },
        nextRound: keepGoing ? n => { if (n != _settings.AlphaLetters) { _settings.AlphaLetters = n; SaveSettingsQuietly(); } return Alphabet.PickRound(n); } : null,
        startSize: size, deck: Alphabet.Instance);
    }

    private void DashAlpha_Click(object sender, RoutedEventArgs e) => StartAlphaRound(-1, keepGoing: true);
    private void AlphaPracticeNow_Click(object sender, RoutedEventArgs e) => StartAlphaRound(-1, keepGoing: true);

    private void UpdateAlphaSummary()
    {
        if (AlphaSummary == null) return;
        var cards = Alphabet.Cards;
        int known = cards.Count(c => c.Level >= 4), due = cards.Count(c => c.Due <= DateTime.Now);
        var done = DoneToday(_settings.AlphaDone);
        var next = SlotsFor(_settings.AlphaPerDay, "alphabet").Where((t, i) => t > DateTime.Now && !done.Contains(i)).Cast<DateTime?>().FirstOrDefault();
        AlphaSummary.Text = $"{cards.Count} letters and pairs: {known} known well, {due} due now." +
            (_settings.AlphaPerDay == 0 ? " No scheduled rounds (use αβγ on the dashboard)." : next is DateTime n ? $" Next round around {n:t}." : " No more rounds today.");
    }

    /// <summary>Shows or hides each practice's options as its switch is ticked (saved automatically).</summary>
    private void GreekSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (VocabOptions == null || AlphaOptions == null) return;
        VocabOptions.Visibility = VocabOnCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AlphaOptions.Visibility = AlphaOnCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>True during a meeting or lunch (in the running plan, or a busy event in your calendar right now).</summary>
    private bool InMeetingOrLunch()
    {
        if (_runner.IsRunning && _runner.Current is PlanBlock cur &&
            (cur.Kind == BlockKind.Meeting || cur.Name.Contains("lunch", StringComparison.OrdinalIgnoreCase))) return true;
        var now = DateTime.Now;
        try
        {
            if (CalendarService.BusyEventsOn(DateTime.Today).Any(e => e.Start <= now && e.End > now)) return true;
            var (blocks, start) = TodaysSchedule();
            var t = DateTime.Today + start;
            foreach (var b in blocks)
            {
                var end = t + TimeSpan.FromMinutes(b.Minutes);
                if (t <= now && now < end) return b.Kind == BlockKind.Meeting || b.Name.Contains("lunch", StringComparison.OrdinalIgnoreCase);
                t = end;
            }
        }
        catch (Exception ex) { Log.Error("InMeetingOrLunch", ex); }
        return false;
    }

    /// <summary>Checked every 30 seconds: shows a round when one is due (unless it's a meeting or lunch).</summary>
    private void CheckVocab()
    {
        // Outside a running focus day or session, the "outside sessions" schedule applies instead.
        if (!_runner.IsRunning) { CheckAwayPractice(); return; }
        if (!_settings.On("greek") || VocabWindow.IsShowing || DateTime.Now < _vocabLaterUntil) { CheckAlphabet(); return; }
        if (Application.Current.Windows.OfType<FlashWindow>().Any()) return; // let a session alert finish first
        var now = DateTime.Now;
        var done = VocabDoneToday();
        var slots = VocabSlots();
        int due = -1;
        for (int i = 0; i < slots.Count; i++)
            if (!done.Contains(i) && slots[i] <= now && now - slots[i] < TimeSpan.FromHours(3)) { due = i; break; }
        if (due < 0 && _vocabLaterSlot >= 0 && !done.Contains(_vocabLaterSlot)) due = _vocabLaterSlot;
        if (due < 0) { CheckAlphabet(); return; }
        if (InMeetingOrLunch()) return; // waits for the next focus session or break
        StartVocabRound(due);
    }

    private void StartVocabRound(int slot, Action<bool>? after = null)
    {
        if (_settings.VocabAutoAdd) Vocab.TopUp(20);
        var words = Vocab.PickRound(Math.Clamp(_settings.VocabWords, 3, 10));
        if (words.Count < 2)
        {
            if (slot < 0 && after == null) Info("Add a few words to practice first: Settings, Greek practice, Manage words.", "Greek practice");
            return;
        }
        if (slot >= 0) { var d = VocabDoneToday(); d.Add(slot); SetVocabDone(d); }
        VocabWindow.ShowOnAllScreens(words, finished =>
        {
            if (!finished && slot >= 0)
            {
                // Later: the same round comes back in 20 minutes.
                var d = VocabDoneToday(); d.Remove(slot); SetVocabDone(d);
                _vocabLaterSlot = slot;
                _vocabLaterUntil = DateTime.Now.AddMinutes(20);
            }
            else if (finished) _vocabLaterSlot = -1;
            UpdateVocabSummary();
            after?.Invoke(finished);
        });
    }

    private void PracticeNow_Click(object sender, RoutedEventArgs e) => StartVocabRound(-1);

    /// <summary>
    /// Review any time from the dashboard: rounds keep coming, from your whole bank (not just what's due),
    /// until you press Done. You can change the number of words per round as you go.
    /// </summary>
    private void DashGreek_Click(object sender, RoutedEventArgs e)
    {
        if (VocabWindow.IsShowing) return;
        if (_settings.VocabAutoAdd) Vocab.TopUp(20);
        if (Vocab.Active.Count() < 3)
        {
            Info("Add a few words first: Settings, Greek practice, Manage words.", "Greek practice");
            return;
        }
        VocabWindow.ShowOnAllScreens(Vocab.PickRound(Math.Clamp(_settings.VocabWords, 3, 10), anyTime: true), _ => UpdateVocabSummary(),
            nextRound: size =>
            {
                if (size != _settings.VocabWords) { _settings.VocabWords = size; SaveSettingsQuietly(); }
                return Vocab.PickRound(size, anyTime: true);
            }, startSize: Math.Clamp(_settings.VocabWords, 3, 10));
    }

    private void ManageWords_Click(object sender, RoutedEventArgs e)
    {
        var w = new VocabManagerWindow { Owner = this };
        w.ShowDialog();
        UpdateVocabSummary();
    }

    private void UpdateVocabSummary()
    {
        if (VocabSummary == null) return;
        var active = Vocab.Active.ToList();
        int total = active.Count, retired = active.Count(c => c.Retired), due = active.Count(c => !c.Retired && c.Due <= DateTime.Now);
        var next = VocabSlots().Where((t, i) => t > DateTime.Now && !VocabDoneToday().Contains(i)).Cast<DateTime?>().FirstOrDefault();
        VocabSummary.Text = total == 0 ? "No words yet. Practice adds the most frequent words automatically, or use Manage words."
            : $"{total} words: {total - retired} being learned, {retired} retired, {due} due now." +
              (next is DateTime n ? $" Next round around {n:t}." : " No more rounds today.");
    }

    private void FillVocabSettings()
    {
        VocabPerDayBox.Value = Math.Clamp(_settings.VocabPerDay, 1, 12);
        VocabWordsBox.Value = Math.Clamp(_settings.VocabWords, 3, 10);
        VocabFromBox.Text = DateTime.Today.Add(TimeSpan.TryParse(_settings.VocabFrom, CultureInfo.InvariantCulture, out var f) ? f : new TimeSpan(8, 30, 0)).ToString("t");
        VocabToBox.Text = DateTime.Today.Add(TimeSpan.TryParse(_settings.VocabTo, CultureInfo.InvariantCulture, out var t) ? t : new TimeSpan(16, 0, 0)).ToString("t");
        VocabAutoAddCheck.IsChecked = _settings.VocabAutoAdd;
        VocabRetireSlider.Value = Math.Clamp(_settings.VocabRetireAfter, 3, 12);
        VocabThresholdBox.Value = Math.Clamp(_settings.VocabRetireThreshold, 10, 5000);
        VocabOnCheck.IsChecked = _settings.On("greek");
        AlphaOnCheck.IsChecked = _settings.On("alphabet");
        AlphaPerDayBox.Value = Math.Clamp(_settings.AlphaPerDay, 0, 12);
        AlphaLettersBox.Value = Math.Clamp(_settings.AlphaLetters, 3, 10);
        AlphaPairsCheck.IsChecked = _settings.AlphaPairs;
        GreekSwitch_Changed(this, new RoutedEventArgs());
        UpdateAlphaSummary();
        FillPracticeSettings();
        UpdateRetireText();
        UpdateVocabSummary();
    }

    private void VocabRetire_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateRetireText();

    /// <summary>Explains the slider: how many right rounds, and roughly how long that takes.</summary>
    private void UpdateRetireText()
    {
        if (VocabRetireText == null || VocabRetireSlider == null) return;
        int levels = (int)Math.Round(VocabRetireSlider.Value);
        double[] waitDays = { 2.0 / 24, 1, 3, 7, 14, 21, 30, 40, 50, 60, 75 };
        double days = waitDays.Take(Math.Min(levels - 1, waitDays.Length)).Sum();
        string span = days < 14 ? $"about {Math.Max(1, Math.Round(days))} days" : days < 60 ? $"about {Math.Round(days / 7)} weeks" : $"about {Math.Round(days / 30)} months";
        VocabRetireText.Text = $"Retire-ready after {levels} right rounds ({span} of review)";
    }

    /// <summary>Reads the Greek practice settings; returns a problem to show, or null.</summary>
    private string? SaveVocabSettings()
    {
        VocabPerDayBox.Commit();
        VocabWordsBox.Commit();
        if (!TryParseTime(VocabFromBox.Text, out var from) || !TryParseTime(VocabToBox.Text, out var to) || to <= from)
            return "Enter the Greek practice hours like 8:30 AM to 4:00 PM.";
        _settings.VocabPerDay = VocabPerDayBox.Value;
        _settings.VocabWords = VocabWordsBox.Value;
        _settings.VocabFrom = from.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        _settings.VocabTo = to.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        _settings.VocabAutoAdd = VocabAutoAddCheck.IsChecked == true;
        _settings.VocabRetireAfter = (int)Math.Round(VocabRetireSlider.Value);
        AlphaPerDayBox.Commit();
        AlphaLettersBox.Commit();
        _settings.AlphaPerDay = AlphaPerDayBox.Value;
        _settings.AlphaLetters = AlphaLettersBox.Value;
        _settings.AlphaPairs = AlphaPairsCheck.IsChecked == true;
        Alphabet.IncludePairs = _settings.AlphaPairs;
        _settings.Features["greek"] = VocabOnCheck.IsChecked == true;
        _settings.Features["alphabet"] = AlphaOnCheck.IsChecked == true;
        if (SavePracticeSettings() is string practiceProblem) return practiceProblem;
        Vocab.RetiredLevel = _settings.VocabRetireAfter; // raising it brings retired words back into practice
        VocabThresholdBox.Commit();
        _settings.VocabRetireThreshold = VocabThresholdBox.Value;
        Vocab.RetireThreshold = _settings.VocabRetireThreshold;
        Vocab.RecomputeRetired();
        return null;
    }

    // ---------- The Pomodoro tab: your task list beside the timer ----------

    // The task list and the countdown are the dashboard's own; they're lent to the Pomodoro tab while it's open.
    private readonly Dictionary<FrameworkElement, (Panel Home, int Index)> _lent = new();

    private void Lend(FrameworkElement element, Action<UIElement> putThere)
    {
        if (_lent.ContainsKey(element) || LogicalTreeHelper.GetParent(element) is not Panel home) return;
        _lent[element] = (home, home.Children.IndexOf(element));
        home.Children.Remove(element);
        putThere(element);
    }

    private void GiveBack(FrameworkElement element, Action clearThere)
    {
        if (!_lent.TryGetValue(element, out var h)) return;
        clearThere();
        h.Home.Children.Insert(Math.Min(h.Index, h.Home.Children.Count), element);
        _lent.Remove(element);
    }

    /// <summary>
    /// On the Pomodoro tab: your task list sits on the right (if ticked), and once a Pomodoro is running the
    /// setup gives way to the countdown, large, on the left. Leaving the tab puts everything back on the dashboard.
    /// </summary>
    private void UpdatePomodoroLayout()
    {
        if (PomodoroPanel == null || PomoTasksCheck == null) return;
        try
        {
            bool onTab = PomodoroPanel.Visibility == Visibility.Visible;
            HookPersonalItemsInSession();
            bool tasks = onTab && _settings.PomoShowTasks;
            if (tasks) Lend(DashTasksPanel, e => PomoTasksHost.Content = e);
            else GiveBack(DashTasksPanel, () => PomoTasksHost.Content = null);
            PomoTasksCard.Visibility = tasks ? Visibility.Visible : Visibility.Collapsed;
            double share = Math.Clamp(_settings.PomoTasksShare, 0.25, 0.8);
            if (!_draggingPomo)
            {
                PomoLeftColumn.Width = new GridLength(1 - share, GridUnitType.Star);
                PomoTasksColumn.Width = tasks ? new GridLength(share, GridUnitType.Star) : new GridLength(0);
            }
            PomoTasksGap.Width = new GridLength(tasks ? 16 : 0);
            PomoSplitter.Visibility = tasks ? Visibility.Visible : Visibility.Collapsed;

            bool running = onTab && _runner.IsRunning && _mode == RunMode.Pomodoro;
            // The countdown ring (with the minutes inside it) is the panel around the dashboard's timer.
            if (LogicalTreeHelper.GetParent(DashTimer) is FrameworkElement ringPanel)
            {
                if (running) Lend(ringPanel, e => PomoRingHost.Child = e);
                else GiveBack(ringPanel, () => PomoRingHost.Child = null);
            }
            PomoRingArea.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            PomoSetupCard.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            if (running && _runner.Current is PlanBlock cur) PomoPhaseText.Text = cur.IsFocus ? "Focus" : "Break";
            if (tasks) RefreshDashTasks();
        }
        catch (Exception ex) { Log.Error("UpdatePomodoroLayout", ex); }
    }

    private bool _draggingPomo;

    /// <summary>Remembers how you divided the Pomodoro tab between the timer and your tasks.</summary>
    private void PomoSplitter_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => _draggingPomo = true;

    private void PomoSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _draggingPomo = false;
        double left = PomoLeftColumn.ActualWidth, right = PomoTasksColumn.ActualWidth;
        if (left + right <= 0) return;
        _settings.PomoTasksShare = Math.Clamp(right / (left + right), 0.25, 0.8);
        SaveSettingsQuietly();
        UpdatePomodoroLayout();
    }

    private void PomoTasks_Changed(object sender, RoutedEventArgs e)
    {
        if (PomoTasksCheck == null || _settings == null) return;
        _settings.PomoShowTasks = PomoTasksCheck.IsChecked == true;
        SaveSettingsQuietly();
        UpdatePomodoroLayout();
    }
}
