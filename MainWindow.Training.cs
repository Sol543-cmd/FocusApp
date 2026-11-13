using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>
/// Training (the book course) inside Focus: the course page, the three daily lesson times (the first two
/// pop up and can wait for the next time; at the last one the lesson opens and can't be closed until it's
/// finished; quitting Focus is the only way out and moves it to tomorrow), and your chosen block lists
/// during lessons. The course itself is in Training.cs; the lesson player is LessonWindow.cs.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _trainingTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private LessonWindow? _lessonWindow;
    private string _trainingPromptKey = "";
    private DateTime _trainingSnoozeUntil = DateTime.MinValue;

    private void SetUpTraining()
    {
        Course.Load();
        // Every answer changes the course; redrawing the Training page then (it opens each book) froze practice windows.
        // While a lesson or practice window is open, the page waits and redraws once when it closes.
        Course.Changed += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_lessonWindow != null) { _trainingPageStale = true; return; }
            _refreshCoursePage?.Invoke();
        }));
        _tray.ContextMenuStrip?.Items.Insert(1, new System.Windows.Forms.ToolStripMenuItem("Training…", null, (_, _) => { ShowFromTray(); OpenCoursePage(); }));
        _trainingTimer.Tick += (_, _) => CheckTraining();
        _trainingTimer.Start();
        // A lesson finished (or started) on another computer shows up here straight away.
        Sync.RemoteChanged += () => { try { Course.ReloadEvents(raiseChanged: true); } catch (Exception ex) { Log.Error("Training.RemoteChanged", ex); } };
    }

    private DateTime _trainingReloadedAt = DateTime.MinValue;

    private void Training_Click(object sender, RoutedEventArgs e) => OpenCoursePage();

    private List<TimeSpan> TrainingTimes() =>
        Course.Config.Times.Select(t => TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var v) ? v : (TimeSpan?)null)
              .Where(t => t != null).Select(t => t!.Value).OrderBy(t => t).ToList();

    /// <summary>Every 30 seconds: offers the day's lesson at each time, opens it (required) at the last one, and runs All-books practice.</summary>
    private void CheckTraining()
    {
        // Every two minutes, read the other devices' progress (OneDrive notices can be missed).
        if (DateTime.Now - _trainingReloadedAt > TimeSpan.FromMinutes(2))
        {
            _trainingReloadedAt = DateTime.Now;
            try { Course.ReloadEvents(raiseChanged: true); } catch (Exception ex) { Log.Error("Training.Reload", ex); }
            AdoptSharedPracticeTimes(); // timelines changed on another computer
        }
        if (_lessonWindow != null || _lessonPrompt != null || _conceptQuiz != null || VocabWindow.IsShowing) return;
        // Weekend pause: practice only when you start it (a lesson you put on this weekend day on the Training page still comes up).
        if (PracticePausedToday()) { if (Course.Config.WeekPlan.TryGetValue(Course.Today, out var wp) && !wp.Default) LessonTime(DateTime.Now.TimeOfDay); return; }
        var now = DateTime.Now.TimeOfDay;
        if (!LessonTime(now)) CheckAllBooks(now, last: false);
        if (_lessonWindow == null) CheckCardReview(now);
        if (_lessonWindow == null) CheckTimelinePractice(now);
    }

    private DateTime _lessonOfferedAt = DateTime.MinValue;

    /// <summary>A lesson time is remembered by the time itself (moving it makes it a new time that comes up).</summary>
    private static string LessonKey(TimeSpan t) => Course.Today + "#" + t.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// The lesson goes first in a chain: true while today's lesson is due and hasn't been offered yet, or was offered in
    /// the last 10 minutes (time to start it). After that, waiting rounds go ahead.
    /// </summary>
    private bool _trainingPageStale;

    private bool LessonGoesFirst()
    {
        // Fixed in 1.7.17: this used to check the open book (not the book whose lesson is due) and could stay true all day,
        // which held back every practice round. Now it holds rounds back for at most 10 minutes from the lesson time
        // (or from when the lesson was offered), and only while a lesson is really waiting.
        if (!Course.Config.On || PracticePausedToday() || DateTime.Now < _trainingSnoozeUntil) return false;
        if (Course.NextDueToday() is not CourseInfo due) return false;
        var times = TrainingTimes();
        int slot = times.FindLastIndex(t => t <= DateTime.Now.TimeOfDay);
        if (slot < 0 || !SlotLive(DateTime.Today + times[slot], TimeSpan.FromDays(1))) return false;
        var o = Course.OutlineOf(due.Id);
        if (!o.Lessons.Any(l => !o.Done.Contains(l.Index))) return false; // its next chapter isn't imported: nothing to offer
        var since = _trainingPromptKey == LessonKey(times[slot]) ? _lessonOfferedAt : DateTime.Today + times[slot];
        return DateTime.Now - since < TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// True when a lesson was offered or opened (or is waiting for a meeting or break). Each lesson time offers the day's
    /// next lesson not done (two lessons a day: the first, then the second). Once the day's lessons are done, the other
    /// times drop away. At the last time only the day's first lesson is required; a second one left undone becomes a catch-up.
    /// The open book switches only here, when its lesson actually comes up (never while you're doing an extra lesson).
    /// </summary>
    private bool LessonTime(TimeSpan now)
    {
        if (!Course.Config.On || Course.BooksFor(DateTime.Today).Count == 0) return false; // no lesson planned today
        var times = TrainingTimes();
        int slot = times.FindLastIndex(t => t <= now);
        if (slot < 0) return false;
        bool last = slot == times.Count - 1;
        // Lesson times before the end of the practice day reset at it; ones after it last until midnight. The day's last
        // lesson time always lasts until midnight: it keeps coming back until the lesson is done.
        if (!last && !SlotLive(DateTime.Today + times[slot], TimeSpan.FromDays(1))) return false;
        Course.ReloadEvents(); // another device may have finished today's lesson
        var day = Course.DayLessons(DateTime.Today);
        var due = day.FirstOrDefault(x => !x.Done).Book;
        if (due == null) return false;                                       // all done today: no more reminders
        bool required = last && !day.Any(x => x.Done);                       // only the first lesson is required
        if (_lessonPrompt != null) return true;                              // already on screen
        string key = LessonKey(times[slot]);
        if (!required && (_trainingPromptKey == key || DateTime.Now < _trainingSnoozeUntil)) return false;
        if (PracticeMustWait()) return true;                                 // waits until the meeting, break or lunch is over
        Course.UseBookQuietly(due.Id);
        if (Course.Packs.Count == 0 || (Course.NextLesson() == null && Course.ExamDue() == null)) return false;
        if (required)
        {
            // Every time Focus is running (or starts again) today: the popup, with no Later, until the lesson is done.
            ShowLessonPrompt(required: true, next: null);
            return true;
        }
        _trainingPromptKey = key;
        _lessonOfferedAt = DateTime.Now;
        ShowLessonPrompt(required: false, next: last ? null : times[slot + 1]);
        return true;
    }

    private LessonPromptWindow? _lessonPrompt;

    /// <summary>
    /// The lesson popup on the screen you're working on: Start (or Continue) and, except at the last lesson time, Later.
    /// The last one opens the lesson as required: it has to be finished, including a lesson half done earlier.
    /// </summary>
    private void ShowLessonPrompt(bool required, TimeSpan? next)
    {
        var exam = Course.ExamDue();
        var lesson = Course.NextLesson();
        bool part = exam == null && Course.InProgress(lesson);
        string title = exam?.Title ?? (lesson != null ? $"{ShortTitle(Course.Current)}, {lesson.Lesson.Title}" : "Today's lesson");
        string meta = exam != null ? "Today's lesson is a unit exam." :
                      lesson != null ? $"Chapter {lesson.Pack.Chapter} · about 25 minutes of reading and practice, then 5 minutes of cards" + (part ? " · you've started it" : "") : "";
        var day = Course.DayLessons(DateTime.Today);
        string which = day.Count > 1 ? $" {Math.Min(day.Count, day.Count(x => x.Done) + 1)} OF {day.Count}" : "";
        string label = required ? $"BOOK LESSON{which} · LAST TIME TODAY · {DateTime.Now:t}" : $"BOOK LESSON{which} · {DateTime.Now:t}";
        string bookId = Course.Current.Id;
        int doneBefore = Course.DoneTodayCount();
        string? note = required
            ? (part ? "Today's last lesson time: finish the lesson you started. " : "Today's last lesson time. ") +
              "This stays until the lesson is done. Quitting Focus from the tray puts it off, but it comes back each time Focus starts today."
            : null;
        string? laterText = required ? null : next is TimeSpan n ? $"Later · back at {DateTime.Today.Add(n):t}" : "Later";
        LessonPromptWindow? w = null;
        w = new LessonPromptWindow(VocabWindow.WorkingScreenBounds(), label, title, meta, part ? "Continue lesson" : "Start lesson", laterText, note,
            start: () =>
            {
                w?.CloseNow();
                Course.ReloadEvents();
                if (Course.DoneTodayCount() > doneBefore) { _toast.ShowMessage("Already done", "You finished this lesson on another device."); return; }
                Course.UseBookQuietly(bookId);
                StartLesson(required);
            },
            later: required ? null : () => w?.CloseNow());
        w.Closed += (_, _) => _lessonPrompt = null;
        _lessonPrompt = w;
        w.Show();
        WindowFront.Bring(w);
    }

    /// <param name="bookId">The book to do it in (a book page's Start lesson always uses its own book).</param>
    private void StartLesson(bool required, string? bookId = null)
    {
        if (_lessonWindow != null) { _lessonWindow.Activate(); return; }
        if (bookId != null) Course.UseBookQuietly(bookId);
        if (Course.ExamDue() is Course.Exam exam && exam.Questions.Count > 0)
        {
            // A unit exam (or the final) is the day's lesson once that part of the book is done.
            if (Course.DoneToday() && !required && !Ask($"You've already done today's lesson. Take {exam.Title} now anyway?", "Training", defaultNo: true)) return;
            OpenTrainingWindow(new LessonWindow(exam.Title, exam.Questions, exam.Key, _ => { }, required), blocking: true);
            return;
        }
        var lesson = Course.NextLesson();
        if (lesson == null) { Info("There's no lesson to do yet. Import the next chapter pack on the Training page.", "Training"); return; }
        if (Course.Book.Count == 0) { Info("Choose your book's Word file on the Training page first, so Focus can show the reading.", "Training"); return; }
        // An extra lesson (after today's, or on a weekend) is always fine: it counts, and the next weekday offers the one after it.

        OpenTrainingWindow(new LessonWindow(lesson, required, _ => { }), blocking: true);
    }

    /// <summary>Shows a lesson, exam or practice round, with the lesson block lists on while it's open.</summary>
    private void OpenTrainingWindow(LessonWindow window, bool blocking)
    {
        if (blocking) StartTrainingBlocking();
        Course.Pinned = Course.Current.Id; // the open book stays put while the window is open (bug: an extra lesson switched books)
        window.Closed += (_, _) =>
        {
            if (_trainingPageStale) { _trainingPageStale = false; Dispatcher.BeginInvoke(new Action(() => _refreshCoursePage?.Invoke())); }
            Course.Pinned = null;
            _lessonWindow = null;
            if (window.LeftPartWay && !window.ClosedByEndOfDay)
            {
                var times = TrainingTimes();
                var nextTime = times.FirstOrDefault(t => t > DateTime.Now.TimeOfDay);
                _toast.ShowMessage("Lesson saved", nextTime > TimeSpan.Zero
                    ? $"It picks up where you left off at {DateTime.Today.Add(nextTime):t}{(nextTime == times[^1] ? " (required)" : "")}, on any of your devices."
                    : "It picks up where you left off next time, on any of your devices.");
            }
            if (blocking) EndTrainingBlocking();
            _trainingSnoozeUntil = DateTime.Now.AddMinutes(1);
            _refreshCoursePage?.Invoke();
        };
        _lessonWindow = window;
        window.Show();
        WindowFront.Bring(window); // in front of what you're working in, not just a flashing taskbar button
    }

    // ---------- All-books practice ----------

    private string _allBooksPromptKey = "";

    /// <summary>After you finish a book: a short round at each All-books time (if it pops up by itself), never in meetings.</summary>
    private void CheckAllBooks(TimeSpan now, bool last)
    {
        if (!Course.Config.AllBooksAuto || Course.Config.Courses.Count == 0 || _lessonWindow != null) return;
        var times = Course.Config.AllBooksTimes.Select(t => TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var v) ? v : (TimeSpan?)null)
                          .Where(t => t != null).Select(t => t!.Value).OrderBy(t => t).ToList();
        int slot = times.FindLastIndex(t => t <= now);
        if (slot < 0 || !SlotLive(DateTime.Today + times[slot], TimeSpan.FromHours(2))) return;
        string key = Course.Today + "#ab" + times[slot].ToString(@"hh\:mm", CultureInfo.InvariantCulture); // by its time: a moved round is a new round
        if (_allBooksPromptKey == key || Course.Events.Any(e => e.Kind == "allBooksDone" && e.Day == Course.Today && e.At.ToLocalTime().TimeOfDay >= times[slot])) return;
        if (PracticeMustWait()) return;
        _allBooksPromptKey = key;
        StartAllBooks();
    }

    /// <summary>Books practice (it replaced All-books practice): Learn by default.</summary>
    private void StartAllBooks() => StartBooksPractice();

    // ---------- Evening card review ----------

    private string _cardReviewKey = "";

    /// <summary>At its time: a few minutes with today's hardest memory cards, once a day across all devices, never in meetings.</summary>
    private void CheckCardReview(TimeSpan now)
    {
        var cfg = Course.Config;
        if (!cfg.CardReviewOn || Course.Packs.Count == 0) return;
        var times = cfg.CardReviewTimes.Select(t => TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var v) ? v : (TimeSpan?)null)
                       .Where(t => t != null).Select(t => t!.Value).OrderBy(t => t).ToList();
        int slot = times.FindLastIndex(t => t <= now);
        if (slot < 0 || !SlotLive(DateTime.Today + times[slot], TimeSpan.FromHours(3))) return;
        string key = Course.Today + "#cr" + times[slot].ToString(@"hh\:mm", CultureInfo.InvariantCulture); // by its time: a moved round is a new round
        // Each time on the timeline is its own round (done once since that time, on any device).
        if (_cardReviewKey == key || Course.Events.Any(e => e.Kind == "cardReviewDone" && e.Day == Course.Today && e.At.ToLocalTime().TimeOfDay >= times[slot])) return;
        if (PracticeMustWait()) return; // waits until the meeting, break or lunch is over
        _cardReviewKey = key;
        StartCardReview(quietIfNone: true);
    }

    /// <summary>
    /// Timeline practice, its own spaced-repetition exercise. Learn (the default): what's due, then events from your last
    /// two lessons (in each of today's books). Review: an era, or a span of years.
    /// </summary>
    private void StartTimelinePractice(bool review = false, int from = 0, int to = 0, string era = "", bool quiet = false)
    {
        if (_lessonWindow != null) { _lessonWindow.Activate(); return; }
        try
        {
            int max = Math.Clamp(Course.Config.TimelineCount, 3, 15);
            var today = Course.BooksFor(DateTime.Today).Select(c => c.Id).Distinct().ToList();
            var books = review || today.Count == 0 ? Course.Config.Courses.Select(c => c.Id).ToList() : today;
            // Review (the usual round): everything you've learned, rustiest most often. Practice (review: true here): an era or span.
            var events = review ? Course.TimelineReview(books, from, to, max) : Course.TimelineRustiest(Course.Config.Courses.Select(c => c.Id), max);
            if (events.Count < 2)
            {
                if (quiet) return;
                if (!review)
                {
                    // Nothing due: open Review on the era of your latest events instead.
                    var learned = Course.LearnedEvents(Course.Config.Courses.Select(c => c.Id));
                    if (learned.Count < 2) { Info("No timeline yet: finish a lesson that has timeline events first.", "Timeline"); return; }
                    _toast.ShowMessage("Nothing due on the timeline", "Here's a review of everything you've learned.");
                    StartTimelinePractice(review: true, from: -9999, to: 9999, era: "Everything");
                    return;
                }
                Info("Fewer than two events you've learned fall in that span.", "Timeline");
                return;
            }
            var w = LessonWindow.TimelinePractice(events, _ => { });
            w.AnotherRound = () => Dispatcher.BeginInvoke(new Action(() => StartTimelinePractice(review, from, to, era)));
            w.BrowseTimeline = () => new TimelineBrowserWindow { Owner = w }.Show();
            w.AddToTopBar(TimelineModeBar(review, era, from, to));
            w.Closed += (_, _) => { Course.Pinned = null; Course.UseToday(); _refreshCoursePage?.Invoke(); };
            OpenTrainingWindow(w, blocking: false);
        }
        catch (Exception ex) { Log.Error("Timeline practice", ex); Course.UseToday(); }
    }

    /// <summary>Learn and Review; in Review, an era from the list or a span of years (negative or "BCE" for before Christ).</summary>
    private FrameworkElement TimelineModeBar(bool review, string era, int from, int to)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
        void Restart(bool r, int f, int t, string e) => Dispatcher.BeginInvoke(new Action(() => { _lessonWindow?.Close(); StartTimelinePractice(r, f, t, e); }));
        Button Mode(string text, bool on, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 6, 0) };
            if (on) b.SetResourceReference(StyleProperty, "PrimaryButton");
            b.Click += (_, _) => { if (!on) click(); };
            return b;
        }
        var learned = Course.LearnedEvents(Course.Config.Courses.Select(c => c.Id));
        var eras = Course.Eras.Where(x => learned.Any(l => l.E.Year >= x.From && l.E.Year <= x.To)).ToList();
        bar.Children.Add(Mode("Review", !review, () => Restart(false, 0, 0, "")));
        bar.Children.Add(Mode("Practice", review, () =>
        {
            var first = eras.Count > 0 ? eras[^1] : ("Everything", -9999, 9999);
            Restart(true, first.Item2, first.Item3, first.Item1);
        }));
        if (!review) return bar;

        var pick = new ComboBox { MinWidth = 230, Margin = new Thickness(10, 0, 6, 0), MaxDropDownHeight = 420 };
        pick.Items.Add(new ComboBoxItem { Content = "Everything", Tag = ("Everything", -9999, 9999) });
        foreach (var x in eras) pick.Items.Add(new ComboBoxItem { Content = $"{x.Name}  ({PackEvent.Label(x.From)} – {PackEvent.Label(x.To)})", Tag = (x.Name, x.From, x.To) });
        var spanItem = new ComboBoxItem { Content = "A span of years…", Tag = ("Span", from, to) };
        pick.Items.Add(spanItem);
        pick.SelectedItem = pick.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is ValueTuple<string, int, int> t && t.Item1 == era) ?? spanItem;
        bar.Children.Add(pick);

        var span = new StackPanel { Orientation = Orientation.Horizontal, Visibility = era == "Span" ? Visibility.Visible : Visibility.Collapsed };
        TextBox YearBox(int y) => new() { Text = era == "Span" ? (y < 0 ? $"{-y} BCE" : $"{y}") : "", Width = 82, Margin = new Thickness(4, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "A year: 586 BCE, or 70" };
        var fromBox = YearBox(from); var toBox = YearBox(to);
        static int? ParseYear(string text)
        {
            var t = text.Trim().ToUpperInvariant().Replace(".", "");
            bool bce = t.EndsWith("BCE") || t.EndsWith("BC");
            t = t.Replace("BCE", "").Replace("BC", "").Replace("CE", "").Replace("AD", "").Trim();
            if (!int.TryParse(t, out int y)) return null;
            return bce ? -Math.Abs(y) : y;
        }
        var go = new Button { Content = "Go", Padding = new Thickness(14, 5, 14, 5) };
        go.SetResourceReference(StyleProperty, "PrimaryButton");
        go.Click += (_, _) =>
        {
            if (ParseYear(fromBox.Text) is not int f || ParseYear(toBox.Text) is not int t) { Info("Type two years, like 586 BCE and 70.", "Timeline"); return; }
            Restart(true, Math.Min(f, t), Math.Max(f, t), "Span");
        };
        var fromLabel = new TextBlock { Text = "from", VerticalAlignment = VerticalAlignment.Center };
        var toLabel = new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center };
        span.Children.Add(fromLabel); span.Children.Add(fromBox); span.Children.Add(toLabel); span.Children.Add(toBox); span.Children.Add(go);
        bar.Children.Add(span);
        pick.SelectionChanged += (_, _) =>
        {
            if (pick.SelectedItem is not ComboBoxItem { Tag: ValueTuple<string, int, int> t }) return;
            if (t.Item1 == "Span") { span.Visibility = Visibility.Visible; fromBox.Focus(); return; }
            Restart(true, t.Item2, t.Item3, t.Item1);
        };
        return bar;
    }

    private string _timelinePromptKey = "";

    /// <summary>At its times on the practice timeline: a timeline round (Learn), once per time, never in meetings.</summary>
    private void CheckTimelinePractice(TimeSpan now)
    {
        var cfg = Course.Config;
        if (!cfg.TimelineOn || _lessonWindow != null || cfg.Courses.Count == 0) return;
        var times = cfg.TimelineTimes.Select(t => TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var v) ? v : (TimeSpan?)null)
                       .Where(t => t != null).Select(t => t!.Value).OrderBy(t => t).ToList();
        int slot = times.FindLastIndex(t => t <= now);
        if (slot < 0 || !SlotLive(DateTime.Today + times[slot], TimeSpan.FromHours(2))) return;
        string key = Course.Today + "#tl" + times[slot].ToString(@"hh\:mm", CultureInfo.InvariantCulture); // by its time: a moved round is a new round
        if (_timelinePromptKey == key || Course.Events.Any(e => e.Kind == "timelineDone" && e.Day == Course.Today && e.At.ToLocalTime().TimeOfDay >= times[slot])) return;
        if (PracticeMustWait()) return;
        _timelinePromptKey = key;
        StartTimelinePractice(quiet: true);
    }

    /// <summary>Book flashcards (it replaced the evening card review): Learn by default.</summary>
    private void StartCardReview(bool quietIfNone) => StartBookFlashcards(quiet: quietIfNone);

    // ---------- Highlights and notes ----------

    private void OpenNotesWindow(Window owner)
    {
        var body = new StackPanel { Margin = new Thickness(26, 20, 26, 26) };
        var w = new Window { Title = "Highlights and notes", Width = 820, Height = 760, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                             Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        w.SetResourceReference(BackgroundProperty, "Panel");
        w.SetResourceReference(ForegroundProperty, "Text");
        void Fill()
        {
            body.Children.Clear();
            var head = new DockPanel();
            var export = new Button { Content = "Export to Markdown…", Padding = new Thickness(14, 5, 14, 5) };
            export.SetResourceReference(StyleProperty, "PrimaryButton");
            export.Click += (_, _) => ExportNotes(w);
            DockPanel.SetDock(export, Dock.Right);
            head.Children.Add(export);
            var title = new TextBlock { Text = "Highlights and notes" };
            title.SetResourceReference(StyleProperty, "Heading");
            head.Children.Add(title);
            body.Children.Add(head);
            var all = Course.Highlights();
            if (all.Count == 0)
            {
                var t = new TextBlock { Text = "Nothing yet. While reading, select text and pick a color, or Highlight + note.", Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
                t.SetResourceReference(StyleProperty, "Caption");
                body.Children.Add(t);
            }
            foreach (var chapter in all.GroupBy(h => h.Chapter).OrderBy(g => g.Key))
            {
                var ch = new TextBlock { Text = $"Chapter {chapter.Key}" + (Course.Packs.TryGetValue(chapter.Key, out var pk) ? ": " + pk.Title : ""), Margin = new Thickness(0, 20, 0, 6) };
                ch.SetResourceReference(StyleProperty, "SubHeading");
                body.Children.Add(ch);
                foreach (var h in chapter.OrderBy(h => h.Para))
                {
                    var card = new StackPanel();
                    var quote = new TextBlock { Text = "“" + h.Quote + "”", TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic, FontSize = 15 };
                    card.Children.Add(quote);
                    if (h.Note.Length > 0) card.Children.Add(new TextBlock { Text = h.Note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
                    var links = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                    Button Link(string text, Action click)
                    {
                        var b = new Button { Content = text, Margin = new Thickness(0, 0, 16, 0) };
                        b.SetResourceReference(StyleProperty, "LinkButton");
                        b.Click += (_, _) => click();
                        return b;
                    }
                    var hh = h;
                    links.Children.Add(Link("Show in the book", () =>
                    {
                        var (cs, ce) = Course.Packs.TryGetValue(hh.Chapter, out var p) ? Course.ChapterRange(p) : (Math.Max(0, hh.Para - 20), hh.Para + 20);
                        LessonWindow.OpenPassage(w, Math.Max(0, cs), ce, hh.Para, $"Chapter {hh.Chapter}", hh.Quote);
                    }));
                    links.Children.Add(Link(h.Note.Length > 0 ? "Edit note" : "Add note", () =>
                    {
                        var box = new TextBox { Text = hh.Note, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, Margin = new Thickness(18, 18, 18, 10) };
                        var ok = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(18, 0, 18, 18), HorizontalAlignment = HorizontalAlignment.Right };
                        var sp = new StackPanel(); sp.Children.Add(box); sp.Children.Add(ok);
                        var dlg = new Window { Title = "Note", Width = 460, SizeToContent = SizeToContent.Height, Content = sp, Owner = w, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                        dlg.SetResourceReference(BackgroundProperty, "Panel");
                        dlg.SetResourceReference(ForegroundProperty, "Text");
                        ok.Click += (_, _) => dlg.DialogResult = true;
                        if (dlg.ShowDialog() == true) { Course.Add(new TrainingEvent { Kind = "highlightNote", Question = hh.Id, Note = box.Text.Trim() }); Fill(); }
                    }));
                    links.Children.Add(Link("Remove", () => { Course.Add(new TrainingEvent { Kind = "unhighlight", Question = hh.Id }); Fill(); }));
                    card.Children.Add(links);
                    var border = new Border { Child = card, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(4, 0, 0, 0) };
                    border.SetResourceReference(Border.BackgroundProperty, "Panel2");
                    border.BorderBrush = new SolidColorBrush(h.Color switch { "green" => Color.FromRgb(0x5C, 0xA8, 0x5E), "blue" => Color.FromRgb(0x4A, 0x88, 0xD0), _ => Color.FromRgb(0xE0, 0xB8, 0x30) });
                    body.Children.Add(border);
                }
            }
        }
        Fill();
        w.Show();
    }

    private void ExportNotes(Window owner)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown (*.md)|*.md", FileName = Path.GetFileName(Course.ExportPath), Title = "Export highlights and notes" };
        try { dlg.InitialDirectory = Path.GetDirectoryName(Course.ExportPath); } catch { }
        if (dlg.ShowDialog(owner) != true) return;
        try { File.WriteAllText(dlg.FileName, Course.ExportMarkdown()); Info("Exported to " + dlg.FileName, "Training"); }
        catch (Exception ex) { Info("Couldn't save the file:\n\n" + ex.Message, "Training"); }
    }

    // ---------- Block lists during a lesson ----------

    private void StartTrainingBlocking()
    {
        try
        {
            var lists = Course.Config.BlockListIds.Select(BlockLists.Find).Where(l => l != null).Cast<BlockList>().ToList();
            var apps = lists.SelectMany(l => l.Apps).GroupBy(a => a.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (apps.Count > 0 && !(_runner.IsRunning && _runner.Current is PlanBlock b && b.IsFocus)) _blocker.Begin(apps, WarningSeconds);
            var freedom = lists.SelectMany(l => l.FreedomBlocklistIds).Distinct().ToList();
            if (freedom.Count > 0 && FreedomService.SignedIn && FreedomService.Data.DeviceIds.Count > 0)
                _ = StartFreedomForLesson(freedom);
        }
        catch (Exception ex) { Log.Error("StartTrainingBlocking", ex); }
    }

    private async System.Threading.Tasks.Task StartFreedomForLesson(List<string> lists)
    {
        try { await FreedomService.StartAsync(lists, FreedomService.Data.DeviceIds, 45); }
        catch (Exception ex) { Log.Write("Freedom for lesson: " + ex.Message); }
    }

    private void EndTrainingBlocking()
    {
        try { ApplyBlockingForCurrent(force: true); }
        catch (Exception ex) { Log.Error("EndTrainingBlocking", ex); }
    }

    // ---------- The Training page (in the main window) ----------

    private Action? _refreshCoursePage;
    private string _trainingView = "library"; // library, book or settings

    /// <summary>Opens the Training page (from the tray menu or anywhere else).</summary>
    private void OpenCoursePage()
    {
        if (NavTraining.IsChecked == true) RefreshTraining();
        else NavTraining.IsChecked = true;
    }

    private TextBlock TT(string text, double size = 14, bool bold = false, string? brush = null, double top = 0)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        if (brush != null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private Border TCard(UIElement child, double top = 14)
    {
        var b = new Border { Child = child, CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 16, 20, 18), Margin = new Thickness(0, top, 0, 0) };
        b.SetResourceReference(Border.BackgroundProperty, "Panel");
        return b;
    }

    private Button TButton(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 10, 0) };
        if (primary) b.SetResourceReference(StyleProperty, "PrimaryButton");
        b.Click += (_, _) => click();
        return b;
    }

    private Button TLink(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(StyleProperty, "LinkButton");
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A header row: an optional back link, the title, and the gear for Training settings.</summary>
    private DockPanel TrainingHeader(string title, string? subtitle, bool back, bool gear)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (gear)
        {
            var g = new Button { Content = "\uE713", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18, Padding = new Thickness(10, 8, 10, 8),
                                 ToolTip = "Training settings: lesson times, block lists, All-books practice", VerticalAlignment = VerticalAlignment.Top };
            g.SetResourceReference(StyleProperty, "IconButton");
            g.Click += (_, _) => { _trainingView = "settings"; RefreshTraining(); };
            DockPanel.SetDock(g, Dock.Right);
            row.Children.Add(g);
        }
        var left = new StackPanel();
        if (back)
        {
            var b = TLink("← All books", () => { _trainingView = "library"; RefreshTraining(); });
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(0, 0, 0, 8);
            left.Children.Add(b);
        }
        var h = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap };
        h.SetResourceReference(StyleProperty, "Heading");
        left.Children.Add(h);
        if (subtitle != null) left.Children.Add(TT(subtitle, 14, brush: "Muted", top: 4));
        row.Children.Add(left);
        return row;
    }

    private void RefreshTraining()
    {
        if (TrainingHost == null) return;
        TrainingHost.Children.Clear();
        _refreshCoursePage = () => { if (NavTraining.IsChecked == true) RefreshTraining(); };
        if (_trainingView == "settings") BuildTrainingSettings();
        else if (_trainingView == "book" && Course.HasBook) BuildBookPage();
        else BuildLibrary();
    }

    // ----- The library: a calm board of your books -----

    // ---------- The Training page: today, review, this week, your books ----------

    private static readonly SolidColorBrush Amber = new(Color.FromRgb(0xE8, 0xA8, 0x4A));

    /// <summary>A finished mark: a solid circle with a check inside.</summary>
    private static FrameworkElement DoneMark(double size = 18)
    {
        var g = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var dot = new System.Windows.Shapes.Ellipse();
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FocusBrush");
        g.Children.Add(dot);
        var tick = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse($"M {size * 0.28},{size * 0.52} L {size * 0.44},{size * 0.68} L {size * 0.74},{size * 0.36}"),
            StrokeThickness = Math.Max(1.6, size / 10), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
        };
        tick.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "OnAccent");
        g.Children.Add(tick);
        return g;
    }

    private Border Chip(string text, bool strong)
    {
        var b = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(0, 0, 6, 0), Child = TT(text, 11.5, bold: strong, brush: strong ? "FocusBrush" : "Muted") };
        b.SetResourceReference(Border.BackgroundProperty, strong ? "FocusTint" : "Panel2");
        return b;
    }

    private static string ShortTitle(CourseInfo c) => c.Title.Split(',')[0].Trim();

    private Border PageCard(UIElement child, bool accent = false, Brush? edge = null)
    {
        var card = new Border { Child = child, CornerRadius = new CornerRadius(14), Padding = new Thickness(22, 20, 22, 20), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 16, 16) };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        if (edge != null) card.BorderBrush = edge; else card.SetResourceReference(Border.BorderBrushProperty, accent ? "FocusBrush" : "Line");
        return card;
    }

    /// <summary>A ring showing how much is done (0 to 1).</summary>
    private static FrameworkElement ProgressRing(double fraction, double size)
    {
        var g = new Grid { Width = size, Height = size };
        var back = new System.Windows.Shapes.Ellipse { StrokeThickness = 7 };
        back.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Panel2");
        g.Children.Add(back);
        if (fraction > 0)
        {
            double r = (size - 7) / 2, c = size / 2, angle = Math.Min(fraction, 0.9999) * 2 * Math.PI;
            var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
            var fig = new PathFigure { StartPoint = new Point(c, c - r) };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true));
            var arc = new System.Windows.Shapes.Path { Data = new PathGeometry(new[] { fig }), StrokeThickness = 7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            arc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "FocusBrush");
            g.Children.Add(arc);
        }
        return g;
    }

    private void AddBook()
    {
        var box = new TextBox { Margin = new Thickness(20, 8, 20, 12), Padding = new Thickness(6, 4, 6, 4) };
        var ok = new Button { Content = "Next: choose its Word file", IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(20, 0, 20, 20), HorizontalAlignment = HorizontalAlignment.Right };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "The book's title (and author, if you like)", Margin = new Thickness(20, 20, 20, 0) });
        sp.Children.Add(box); sp.Children.Add(ok);
        var dlg = new Window { Title = "Add a book", Width = 460, SizeToContent = SizeToContent.Height, Content = sp, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        dlg.SetResourceReference(BackgroundProperty, "Panel");
        dlg.SetResourceReference(ForegroundProperty, "Text");
        ok.Click += (_, _) => { if (box.Text.Trim().Length > 0) dlg.DialogResult = true; };
        dlg.Loaded += (_, _) => box.Focus();
        if (dlg.ShowDialog() != true) return;
        Course.AddCourse(box.Text.Trim());
        _trainingView = "book";
        ChooseBookFile();
        RefreshTraining();
    }

    private void ChooseBookFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Word documents (*.docx)|*.docx", Title = "Choose the book's Word file" };
        if (dlg.ShowDialog(this) != true) return;
        try { int n = Course.ImportBook(dlg.FileName); Info($"Done: {n:N0} paragraphs read from {Path.GetFileName(dlg.FileName)}.", "Training"); }
        catch (Exception ex) { Info("That file couldn't be read:\n\n" + ex.Message, "Training"); }
        RefreshTraining();
    }

    private void ImportPack(string path)
    {
        try
        {
            var pack = ChapterPack.Read(path);
            if (pack.Course.Length > 0 && pack.Course != Course.Current.Id &&
                !Ask($"This pack was made for \"{pack.Book}\". Add it to \"{Course.Current.Title}\" anyway?", "Check chapter pack", defaultNo: true)) return;
            // Re-importing a chapter: its lessons must stay the same, or your place in the book would shift.
            if (Course.Packs.TryGetValue(pack.Chapter, out var old) && old.Lessons.Count != pack.Lessons.Count &&
                !Ask($"Chapter {pack.Chapter} already has {old.Lessons.Count} lessons, and this pack has {pack.Lessons.Count}. Replacing it would move your place in the book " +
                     "(lessons are counted in order). Replace it anyway?", "Check chapter pack", defaultNo: true)) return;
            var problems = Course.Check(pack, out string summary);
            if (problems.Count == 0)
            {
                if (!Ask(summary + "\n\n✓ Every lesson's start was found in your book.\n✓ Every question has an answer and a link to its passage.\n\nAdd it to the course?", "Check chapter pack")) return;
            }
            else if (!Ask(summary + "\n\nProblems found:\n• " + string.Join("\n• ", problems.Take(12)) + (problems.Count > 12 ? $"\n…and {problems.Count - 12} more" : "") +
                          "\n\nAdd it anyway? Questions whose passage wasn't found just won't have a working Show where link.", "Check chapter pack", defaultNo: true)) return;
            Course.AddPack(path, pack);
            RefreshTraining();
        }
        catch (Exception ex) { Info("That chapter pack couldn't be read:\n\n" + ex.Message, "Training"); }
    }

    private void TrainingPanel_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files.Where(f => f.EndsWith(".focuspack", StringComparison.OrdinalIgnoreCase))) ImportPack(f);
    }

    // ----- One book -----

    private void BuildBookPage()
    {
        var info = Course.Current;
        string pageBook = info.Id;
        // The header: the cover (click to change it) beside the title, with Rename and the cover's links under it.
        var header = TrainingHeader(info.Title, info.BookFileName.Length > 0 ? $"{info.BookFileName} · {Course.Book.Count:N0} paragraphs" : "No Word file yet", back: true, gear: true);
        if (header.Children[^1] is StackPanel titleBlock)
        {
            var links = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            links.Children.Add(TLink("Rename…", () => RenameBook(info)));
            links.Children.Add(TLink(Course.CoverPath(info) != null ? "Change cover photo…" : "Add a cover photo…", () => ChooseCover(info)));
            if (info.Cover.Length > 0) links.Children.Add(TLink("Remove cover photo", () => { Course.SetCover(info.Id, null); RefreshTraining(); }));
            titleBlock.Children.Add(links);
            header.Children.Remove(titleBlock);
            var withCover = new DockPanel();
            var cover = CoverImage(info, 96, 140);
            cover.Margin = new Thickness(0, 0, 22, 0); cover.VerticalAlignment = VerticalAlignment.Top; cover.Cursor = System.Windows.Input.Cursors.Hand;
            cover.ToolTip = "Change the cover photo";
            cover.MouseLeftButtonUp += (_, _) => ChooseCover(info);
            DockPanel.SetDock(cover, Dock.Left);
            withCover.Children.Add(cover);
            withCover.Children.Add(titleBlock);
            header.Children.Add(withCover);
        }
        TrainingHost.Children.Add(header);

        // ----- A dashboard for the book -----
        var next = Course.NextLesson();
        var exam = Course.ExamDue();
        bool partWay = exam == null && Course.InProgress(next);
        bool extra = Course.DoneToday() || PracticePausedToday();
        var doneSet = Course.DoneLessons();
        var lessonsAll = Course.Lessons();
        Border Tile(UIElement child, double pad = 18) { var t = TCard(child, pad); t.Margin = new Thickness(0, 0, 14, 14); return t; }

        // Today (left) and progress + evening review (right)
        var top = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var today = new StackPanel();
        today.Children.Add(TT(Course.DoneToday() ? "TODAY · DONE" : partWay ? "TODAY · CONTINUE WHERE YOU LEFT OFF" : "TODAY", 11.5, bold: true, brush: "FocusBrush"));
        string headline = exam != null ? $"{exam.Title} · {exam.Questions.Count} questions"
                        : next != null ? next.Lesson.Title
                        : Course.Book.Count == 0 ? "Choose the book's Word file to begin."
                        : Course.Packs.Count == 0 ? "Import the chapter 1 pack to begin." : "Every imported lesson is done. Import the next chapter pack.";
        today.Children.Add(TT(headline, 21, bold: true, top: 8));
        if (next != null && exam == null)
        {
            today.Children.Add(TT($"Chapter {next.Pack.Chapter}: {next.Pack.Title}", 13, brush: "Muted", top: 2));
            // Where the lesson stands: each stage done, current or still to come.
            var d = Course.StepsDone(next.Index);
            var hid = Course.Hidden();
            bool reading = d.Contains("reading");
            bool practice = reading && next.Lesson.Practice.Where(q => !hid.Contains(q.Id)).All(q => d.Contains("practice:" + q.Id));
            bool cards = practice && next.Lesson.Cards.Where(q => !hid.Contains(q.Id)).All(q => d.Contains("card:" + q.Id));
            bool timeline = cards && (d.Contains("timeline") || next.Lesson.Timeline.Count == 0);
            var stages = new (string Name, bool Done)[] { ("Warm-up", reading || d.Any(k => k.StartsWith("warm:"))), ("Reading", reading), ("Practice", practice), ("Cards", cards), ("Timeline", timeline), ("Review", false) };
            int current = Array.FindIndex(stages, x => !x.Done);
            var chips = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            for (int i = 0; i < stages.Length; i++)
            {
                var chip = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1),
                                        Child = TT(stages[i].Name, 12, bold: i == current, brush: stages[i].Done || i == current ? "Text" : "Muted") };
                if (stages[i].Done) chip.SetResourceReference(Border.BackgroundProperty, "FocusTint");
                chip.SetResourceReference(Border.BorderBrushProperty, i == current ? "FocusBrush" : stages[i].Done ? "FocusTint" : "Line");
                chips.Children.Add(chip);
            }
            today.Children.Add(chips);
        }
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (next != null || exam != null) row.Children.Add(TButton(exam != null ? "Start exam" : partWay ? "Continue lesson" : extra ? "Do an extra lesson" : "Start lesson", () => StartLesson(required: false, bookId: pageBook), primary: true));
        var times = TrainingTimes();
        if (times.Count > 0)
        {
            var when = TT((Course.DoneToday() ? "Next time tomorrow" : "Comes up at " + string.Join(", ", times.Select(t => DateTime.Today.Add(t).ToString("t")))) +
                          (times.Count > 0 && !Course.DoneToday() ? $" · required at {DateTime.Today.Add(times[^1]):t}" : ""), 13, brush: "Muted");
            when.VerticalAlignment = VerticalAlignment.Center; when.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(when);
        }
        today.Children.Add(row);
        var todayTile = Tile(today, 22);
        top.Children.Add(todayTile);

        var side = new StackPanel();
        // Book progress: finished chapters plus the share of the current chapter's lessons.
        int chapters = Math.Max(1, info.Chapters);
        double fraction = 0;
        foreach (var ch in Course.Packs.Keys)
        {
            var mine = lessonsAll.Where(l => l.Pack.Chapter == ch).ToList();
            if (mine.Count > 0) fraction += mine.Count(l => doneSet.Contains(l.Index)) / (double)mine.Count;
        }
        fraction = Math.Clamp(fraction / chapters, 0, 1);
        var ringRow = new StackPanel { Orientation = Orientation.Horizontal };
        ringRow.Children.Add(ProgressRing(fraction, 64));
        var ringText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        ringText.Children.Add(TT($"{fraction:P0}", 22, bold: true));
        ringText.Children.Add(TT($"of the book · {doneSet.Count(i => i >= 0)} lessons done", 13, brush: "Muted"));
        ringRow.Children.Add(ringText);
        var ringTile = Tile(ringRow);
        ringTile.Margin = new Thickness(0, 0, 0, 14);
        side.Children.Add(ringTile);
        var eve = new StackPanel();
        string crTime = Course.Config.CardReviewTimes.Count > 0 && TimeSpan.TryParse(Course.Config.CardReviewTimes[0], CultureInfo.InvariantCulture, out var crt) ? DateTime.Today.Add(crt).ToString("t") : "";
        eve.Children.Add(TT("This evening", 13, brush: "Muted"));
        eve.Children.Add(TT(!Course.Config.CardReviewOn ? "Book flashcards are off" : Course.CardReviewDoneToday() ? "Book flashcards done" : $"Book flashcards at {crTime}", 16, bold: true, top: 2));
        int ups = lessonsAll.SelectMany(l => l.Lesson.Cards).Count(q => Course.VoteOf(q.Id) > 0);
        if (ups > 0) eve.Children.Add(TT($"{ups} upvoted card{(ups == 1 ? "" : "s")} come first", 12.5, brush: "BreakBrush", top: 2));
        var eveLink = TLink("Review now", () => StartCardReview(quietIfNone: false));
        eveLink.Margin = new Thickness(0, 6, 0, 0); eveLink.HorizontalAlignment = HorizontalAlignment.Left;
        eve.Children.Add(eveLink);
        var eveTile = Tile(eve);
        eveTile.Margin = new Thickness(0, 0, 0, 14);
        side.Children.Add(eveTile);
        Grid.SetColumn(side, 1);
        top.Children.Add(side);
        TrainingHost.Children.Add(top);

        // Four numbers
        var stats = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
        var allQuestions = lessonsAll.Where(l => doneSet.Contains(l.Index)).SelectMany(l => l.Lesson.Practice.Concat(l.Lesson.Cards)).ToList();
        void Stat(string label, string value) { var sp = new StackPanel(); sp.Children.Add(TT(label, 12.5, brush: "Muted")); sp.Children.Add(TT(value, 22, bold: true, top: 2)); stats.Children.Add(Tile(sp, 16)); }
        Stat("Due for review", Course.DueReview(999).Count.ToString());
        int waiting = Course.WaitingCards();
        if (waiting > 0) Stat("Cards waiting to join", waiting.ToString());
        Stat("Known well", allQuestions.Count(q => Course.StateOf(q.Id).Streak >= 3).ToString());
        Stat("Streak", $"{Course.Streak()} day{(Course.Streak() == 1 ? "" : "s")}");
        Stat("Highlights and notes", Course.Highlights().Count().ToString());
        TrainingHost.Children.Add(stats);

        // Chapters, each with its progress
        var cp = new StackPanel();
        var cpHead = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(TLink("Import chapter pack…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Chapter packs (*.focuspack)|*.focuspack|All files|*.*", Title = "Import a chapter pack" };
            if (dlg.ShowDialog(this) == true) ImportPack(dlg.FileName);
        }));
        actions.Children.Add(TLink(Course.Book.Count == 0 ? "Choose Word file…" : "Replace Word file…", ChooseBookFile));
        DockPanel.SetDock(actions, Dock.Right);
        cpHead.Children.Add(actions);
        cpHead.Children.Add(TT("Chapters", 15, bold: true));
        cp.Children.Add(cpHead);
        int lastCh = Math.Max(Course.Packs.Keys.DefaultIfEmpty(0).Max() + 1, 1);
        for (int ch = 1; ch <= Math.Min(Math.Max(info.Chapters, lastCh), lastCh); ch++)
        {
            var line = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var num = TT(ch.ToString(), 13, brush: "Muted"); num.Width = 28;
            DockPanel.SetDock(num, Dock.Left); line.Children.Add(num);
            if (Course.Packs.TryGetValue(ch, out var pk))
            {
                var mine = lessonsAll.Where(l => l.Pack.Chapter == ch).ToList();
                int got = mine.Count(l => doneSet.Contains(l.Index));
                var count = TT($"{got} of {mine.Count}", 13, brush: "Muted"); count.Width = 70; count.TextAlignment = TextAlignment.Right;
                DockPanel.SetDock(count, Dock.Right); line.Children.Add(count);
                var bar = new Grid { Width = 200, Height = 6, VerticalAlignment = VerticalAlignment.Center };
                var bt = new Border { CornerRadius = new CornerRadius(3) }; bt.SetResourceReference(Border.BackgroundProperty, "Panel2");
                var bf = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = mine.Count == 0 ? 0 : 200.0 * got / mine.Count };
                bf.SetResourceReference(Border.BackgroundProperty, "FocusBrush");
                bar.Children.Add(bt); bar.Children.Add(bf);
                DockPanel.SetDock(bar, Dock.Right); line.Children.Add(bar);
                line.Children.Add(TT(pk.Title, 14));
            }
            else line.Children.Add(TT("Next chapter: not imported yet (drag a .focuspack file onto this page)", 14, brush: "Muted"));
            cp.Children.Add(line);
        }
        var more = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        more.Children.Add(TLink("Highlights and notes", () => OpenNotesWindow(this)));
        more.Children.Add(TLink("Export notes to Markdown…", () => ExportNotes(this)));
        more.Children.Add(TLink("Books practice", StartAllBooks));
        cp.Children.Add(more);
        TrainingHost.Children.Add(TCard(cp, 20));

        var weak = Course.WeakSpots(5);
        if (weak.Count > 0)
        {
            var wp = new StackPanel();
            wp.Children.Add(TT("Weak spots", 15, bold: true));
            foreach (var (concept, missed) in weak) wp.Children.Add(TT($"{concept}  ·  missed {missed}×", 13, top: 4));
            TrainingHost.Children.Add(TCard(wp));
        }

        // Exams (this book)
        var ep = new StackPanel();
        ep.Children.Add(TT("Exams", 15, bold: true));
        ep.Children.Add(TT("A unit exam follows the last chapter of each part of the book, and a final exam the whole book. Enter the chapters where each part ends, for example 3, 10, 19. Passing the final finishes the book; its questions then join All-books practice.", 12, brush: "Muted", top: 2));
        var unitRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var units = new TextBox { Width = 200, Padding = new Thickness(6, 4, 6, 4), Text = string.Join(", ", info.UnitEnds), Margin = new Thickness(0, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center };
        unitRow.Children.Add(units);
        unitRow.Children.Add(TButton("Save", () =>
        {
            var list = new List<int>();
            foreach (var part in units.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out int n) && n >= 1 && n <= info.Chapters) list.Add(n); else { Info($"\"{part}\" isn't a chapter number from 1 to {info.Chapters}.", "Training"); return; }
            info.UnitEnds = list.Distinct().OrderBy(n => n).ToList();
            Course.SaveConfig();
        }));
        ep.Children.Add(unitRow);
        var passed = Course.ExamsPassed();
        if (passed.Count > 0) ep.Children.Add(TT("Passed: " + string.Join(", ", passed.OrderBy(x => x)), 12, brush: "Muted", top: 6));
        TrainingHost.Children.Add(TCard(ep));

        // Hidden and flagged
        var hidden = Course.Hidden().ToList();
        var flagged = Course.Flagged().ToList();
        var hp = new StackPanel();
        hp.Children.Add(TT("Hidden and flagged questions", 15, bold: true));
        hp.Children.Add(TT($"{hidden.Count} hidden · {flagged.Count} flagged" + (flagged.Count > 0 ? $" ({string.Join(", ", flagged.Take(8))}{(flagged.Count > 8 ? "…" : "")}): ask Claude to fix these in their chapter packs." : ""), 13, brush: "Muted", top: 2));
        var all = Course.Packs.Values.SelectMany(p => p.Lessons).SelectMany(l => l.AllQuestions).GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var id in hidden.Take(30))
        {
            var r = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            string qid = id;
            var restore = TLink("Restore", () => Course.Add(new TrainingEvent { Kind = "unhide", Question = qid }));
            DockPanel.SetDock(restore, Dock.Right);
            r.Children.Add(restore);
            r.Children.Add(TT(all.TryGetValue(id, out var q) ? (q.Kind == "card" ? q.Front : q.Prompt) : id, 13));
            hp.Children.Add(r);
        }
        TrainingHost.Children.Add(TCard(hp));

        var remove = TLink("Remove this book from the library…", () =>
        {
            if (!Ask($"Remove \"{info.Title}\" from your library? Its files and your progress stay in the training folder, so adding it again later keeps everything.", "Training", defaultNo: true)) return;
            Course.RemoveCourse(info.Id);
            _trainingView = "library";
            RefreshTraining();
        });
        remove.Margin = new Thickness(0, 18, 0, 0);
        remove.HorizontalAlignment = HorizontalAlignment.Left;
        TrainingHost.Children.Add(remove);
    }

    // ----- Settings for all of Training (behind the gear) -----
}
