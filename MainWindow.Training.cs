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
        Course.Changed += () => Dispatcher.BeginInvoke(new Action(() => _refreshCoursePage?.Invoke()));
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
        }
        if (_lessonWindow != null || VocabWindow.IsShowing) return;
        if (PracticePausedToday()) return; // weekend pause: lessons, All-books and card review only when you start them
        var now = DateTime.Now.TimeOfDay;
        if (!LessonTime(now)) CheckAllBooks(now, last: false);
        if (_lessonWindow == null) CheckCardReview(now);
    }

    /// <summary>True when a lesson was offered or opened (or is waiting for a meeting or break).</summary>
    private bool LessonTime(TimeSpan now)
    {
        if (!Course.Config.On || Course.Packs.Count == 0) return false;
        var times = TrainingTimes();
        int slot = times.FindLastIndex(t => t <= now);
        if (slot < 0) return false;
        // Lesson times before the end of the practice day reset at it; ones after it last until midnight.
        if (!SlotLive(DateTime.Today + times[slot], TimeSpan.FromDays(1))) return false;
        Course.ReloadEvents(); // another device may have finished today's lesson
        if (Course.DoneToday() || (Course.NextLesson() == null && Course.ExamDue() == null)) return false;
        if (PracticeMustWait()) return true;                                 // waits until the meeting, break or lunch is over
        bool last = slot == times.Count - 1;
        if (last) { StartLesson(required: true); return true; }
        string key = Course.Today + "#" + slot;
        if (_trainingPromptKey == key || DateTime.Now < _trainingSnoozeUntil) return false;
        _trainingPromptKey = key;
        string what = Course.ExamDue()?.Title ?? Course.NextLesson()?.Lesson.Title ?? "Today's lesson";
        bool part = Course.ExamDue() == null && Course.InProgress(Course.NextLesson());
        _toast.ShowMessage(part ? "Finish today's lesson" : "Today's lesson is ready",
                           part ? $"{what}: pick up where you left off. Later waits until the next lesson time." : $"{what}. Later waits until the next lesson time.",
                           part ? "Continue" : "Start lesson", () =>
                           {
                               Course.ReloadEvents();
                               if (Course.DoneToday()) { _toast.ShowMessage("Already done today", "You finished today's lesson on another device."); return; }
                               StartLesson(required: false);
                           });
        return true;
    }

    private void StartLesson(bool required)
    {
        if (_lessonWindow != null) { _lessonWindow.Activate(); return; }
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
        if (Course.DoneToday() && !required &&
            !Ask("You've already done today's lesson. Start the next one now anyway?", "Training", defaultNo: true)) return;

        OpenTrainingWindow(new LessonWindow(lesson, required, _ => { }), blocking: true);
    }

    /// <summary>Shows a lesson, exam or practice round, with the lesson block lists on while it's open.</summary>
    private void OpenTrainingWindow(LessonWindow window, bool blocking)
    {
        if (blocking) StartTrainingBlocking();
        window.Closed += (_, _) =>
        {
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
        window.Activate();
    }

    // ---------- All-books practice ----------

    private string _allBooksPromptKey = "";

    /// <summary>After you finish a book: a short round at each All-books time (if it pops up by itself), never in meetings.</summary>
    private void CheckAllBooks(TimeSpan now, bool last)
    {
        if (!Course.Finished || !Course.Config.AllBooksAuto || _lessonWindow != null) return;
        var times = Course.Config.AllBooksTimes.Select(t => TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var v) ? v : (TimeSpan?)null)
                          .Where(t => t != null).Select(t => t!.Value).OrderBy(t => t).ToList();
        int slot = times.FindLastIndex(t => t <= now);
        if (slot < 0 || !SlotLive(DateTime.Today + times[slot], TimeSpan.FromHours(2))) return;
        string key = Course.Today + "#ab" + slot;
        if (_allBooksPromptKey == key || Course.Events.Any(e => e.Kind == "allBooksDone" && e.Day == Course.Today && e.At.ToLocalTime().TimeOfDay >= times[slot])) return;
        if (PracticeMustWait()) return;
        _allBooksPromptKey = key;
        StartAllBooks();
    }

    private void StartAllBooks()
    {
        if (_lessonWindow != null) { _lessonWindow.Activate(); return; }
        var round = Course.AllBooksRound(6);
        if (round.Count == 0) { Info("All-books practice starts once you've finished a book (passed its final exam).", "Training"); return; }
        OpenTrainingWindow(new LessonWindow("All-books practice", round, null, _ => { }), blocking: false);
    }

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
        string key = Course.Today + "#cr" + slot;
        if (_cardReviewKey == key || Course.CardReviewDoneToday()) return;
        if (PracticeMustWait()) return; // waits until the meeting, break or lunch is over
        _cardReviewKey = key;
        StartCardReview(quietIfNone: true);
    }

    private void StartCardReview(bool quietIfNone)
    {
        if (_lessonWindow != null) { _lessonWindow.Activate(); return; }
        var cards = Course.HardestCardsToday();
        if (cards.Count == 0)
        {
            if (!quietIfNone) Info("No cards to review: there's no lesson from today yet, or you knew every card well.", "Training");
            return;
        }
        OpenTrainingWindow(LessonWindow.CardReview(cards, TimeSpan.FromMinutes(Math.Clamp(Course.Config.CardReviewMinutes, 1, 30)), _ => { }), blocking: false);
    }

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

    private void BuildLibrary()
    {
        _trainingView = "library";
        TrainingHost.Children.Add(TrainingHeader("Training", "Your books. Open one to see its lessons, chapters and notes.", back: false, gear: true));
        var board = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        foreach (var c in Course.Config.Courses.OrderByDescending(c => c.Id == Course.Current.Id).ThenBy(c => c.Title))
        {
            bool open = c.Id == Course.Current.Id;
            var panel = new StackPanel();
            panel.Children.Add(TT(c.Title, 17, bold: true));
            string status;
            double progress = 0;
            if (open)
            {
                var lessons = Course.Lessons();
                int done = Course.DoneLessons().Count;
                progress = lessons.Count == 0 ? 0 : (double)done / lessons.Count;
                var next = Course.NextLesson();
                status = Course.Finished ? "Finished" : next != null ? $"Chapter {next.Pack.Chapter} of {c.Chapters} · {done} of {lessons.Count} imported lessons done" :
                         Course.Packs.Count == 0 ? "No chapter packs yet" : "Waiting for the next chapter pack";
            }
            else status = c.BookFileName.Length > 0 ? "Open to see progress" : "No book file yet";
            panel.Children.Add(TT(status, 13, brush: "Muted", top: 6));
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 14, 0, 0) };
            track.SetResourceReference(Border.BackgroundProperty, "Panel2");
            var fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 228 * Math.Clamp(progress, 0, 1) };
            fill.SetResourceReference(Border.BackgroundProperty, "FocusBrush");
            var bar = new Grid(); bar.Children.Add(track); bar.Children.Add(fill);
            panel.Children.Add(bar);
            if (open)
            {
                var badge = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                                         Child = TT(Course.Finished ? "Finished" : Course.DoneToday() ? "Today's lesson done" : "Daily lessons", 11.5, bold: true, brush: "FocusBrush") };
                badge.SetResourceReference(Border.BackgroundProperty, "FocusTint");
                panel.Children.Add(badge);
            }
            var card = new Border { Child = panel, Width = 268, MinHeight = 150, CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 18, 20, 18),
                                    Margin = new Thickness(0, 0, 14, 14), Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "Panel");
            card.SetResourceReference(Border.BorderBrushProperty, open ? "FocusBrush" : "Line");
            string id = c.Id;
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (id != Course.Current.Id)
                {
                    if (!Ask($"Open \"{c.Title}\"? Its lessons become your daily lessons instead of \"{Course.Current.Title}\".", "Training")) return;
                    Course.Open(id);
                }
                _trainingView = "book";
                RefreshTraining();
            };
            board.Children.Add(card);
        }
        // Add a book
        var add = new Border { Width = 268, MinHeight = 150, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 14, 14), Cursor = System.Windows.Input.Cursors.Hand,
                               BorderThickness = new Thickness(1.5), Background = Brushes.Transparent };
        add.SetResourceReference(Border.BorderBrushProperty, "Line");
        var addText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        addText.Children.Add(TT("+", 28, brush: "Muted"));
        addText.Children.Add(TT("Add a book", 14, bold: true, brush: "Muted"));
        ((TextBlock)addText.Children[0]).HorizontalAlignment = HorizontalAlignment.Center;
        add.Child = addText;
        add.MouseLeftButtonUp += (_, _) => AddBook();
        board.Children.Add(add);
        TrainingHost.Children.Add(board);
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
        TrainingHost.Children.Add(TrainingHeader(info.Title, info.BookFileName.Length > 0 ? $"{info.BookFileName} · {Course.Book.Count:N0} paragraphs" : "No Word file yet", back: true, gear: true));

        // Next lesson
        var next = Course.NextLesson();
        var exam = Course.ExamDue();
        var np = new StackPanel();
        bool partWay = exam == null && Course.InProgress(next);
        np.Children.Add(TT(Course.DoneToday() ? "Today's lesson is done" : partWay ? "Lesson in progress: picks up where you left off" : "Next lesson", 13, brush: "Muted"));
        string headline = exam != null ? $"{exam.Title} · {exam.Questions.Count} questions"
                        : next != null ? $"Chapter {next.Pack.Chapter}: {next.Pack.Title}  ·  {next.Lesson.Title}"
                        : Course.Book.Count == 0 ? "Choose the book's Word file to begin."
                        : Course.Packs.Count == 0 ? "Import the chapter 1 pack to begin." : "Every imported lesson is done. Import the next chapter pack.";
        np.Children.Add(TT(headline, 19, bold: true, top: 4));
        var times = TrainingTimes();
        np.Children.Add(TT($"Streak {Course.Streak()} · {Course.DoneLessons().Count} of {Course.Lessons().Count} imported lessons done" +
                           (times.Count > 0 ? $" · comes up at {string.Join(", ", times.Select(t => DateTime.Today.Add(t).ToString("t")))}" : ""), 13, brush: "Muted", top: 6));
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (next != null || exam != null) row.Children.Add(TButton(exam != null ? "Start exam" : partWay ? "Continue lesson" : "Start lesson", () => StartLesson(required: false), primary: true));
        row.Children.Add(TButton("Import chapter pack…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Chapter packs (*.focuspack)|*.focuspack|All files|*.*", Title = "Import a chapter pack" };
            if (dlg.ShowDialog(this) == true) ImportPack(dlg.FileName);
        }));
        row.Children.Add(TButton(Course.Book.Count == 0 ? "Choose Word file…" : "Replace Word file…", ChooseBookFile));
        np.Children.Add(row);
        var links = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        links.Children.Add(TLink("Highlights and notes", () => OpenNotesWindow(this)));
        links.Children.Add(TLink("Export notes to Markdown…", () => ExportNotes(this)));
        links.Children.Add(TLink("All-books practice", StartAllBooks));
        links.Children.Add(TLink("Review today's hardest cards", () => StartCardReview(quietIfNone: false)));
        np.Children.Add(links);
        np.Children.Add(TT("You can also drag a .focuspack file onto this page.", 12, brush: "Muted", top: 10));
        TrainingHost.Children.Add(TCard(np, 18));

        // Chapters
        var strip = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var done = Course.DoneLessons();
        var lessonsAll = Course.Lessons();
        for (int ch = 1; ch <= Math.Max(info.Chapters, Course.Packs.Keys.DefaultIfEmpty(0).Max()); ch++)
        {
            string state, brush;
            var mine = lessonsAll.Where(l => l.Pack.Chapter == ch).ToList();
            if (!Course.Packs.ContainsKey(ch)) { state = "no pack yet"; brush = "Panel2"; }
            else if (mine.Count > 0 && mine.All(l => done.Contains(l.Index))) { state = "finished"; brush = "FocusBrush"; }
            else if (mine.Any(l => done.Contains(l.Index))) { state = "in progress"; brush = "BreakBrush"; }
            else { state = "ready"; brush = "FocusTint"; }
            var box = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 6, 6),
                                   ToolTip = Course.Packs.TryGetValue(ch, out var pk) ? $"Chapter {ch}: {pk.Title} ({state})" : $"Chapter {ch} ({state})",
                                   Child = new TextBlock { Text = ch.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 } };
            box.SetResourceReference(Border.BackgroundProperty, brush);
            strip.Children.Add(box);
        }
        var cp = new StackPanel();
        cp.Children.Add(TT("Chapters", 15, bold: true));
        cp.Children.Add(strip);
        cp.Children.Add(TT("Shaded: no pack yet · Tinted: ready · Amber: in progress · Filled: finished", 12, brush: "Muted", top: 4));
        TrainingHost.Children.Add(TCard(cp));

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

    private void BuildTrainingSettings()
    {
        var cfg = Course.Config;
        var head = TrainingHeader("Training settings", "These apply to every book.", back: false, gear: false);
        var back = TLink("← Back", () => { _trainingView = Course.HasBook && Course.Config.Courses.Count > 0 ? "book" : "library"; RefreshTraining(); });
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.Margin = new Thickness(0, 0, 0, 8);
        TrainingHost.Children.Add(back);
        TrainingHost.Children.Add(head);

        // Lesson times
        var times = TrainingTimes();
        var tp = new StackPanel();
        tp.Children.Add(TT("Lesson times", 15, bold: true));
        tp.Children.Add(TT("The first two pop up and can wait for the next time. At the last one the lesson opens and can't be closed until it's finished; quitting Focus moves it to tomorrow. Lessons never interrupt a meeting or lunch, and the first two wait for a break. These also show on the practice timelines (Settings, Greek practice).", 12, brush: "Muted", top: 2));
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var boxes = new List<TextBox>();
        for (int i = 0; i < 3; i++)
        {
            var tb = new TextBox { Width = 90, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 10, 0), Text = i < times.Count ? DateTime.Today.Add(times[i]).ToString("t") : "", ToolTip = i == 2 ? "Required" : "Optional pop-up" };
            boxes.Add(tb);
            timeRow.Children.Add(tb);
        }
        timeRow.Children.Add(TButton("Save times", () =>
        {
            var parsed = new List<string>();
            foreach (var tb in boxes)
            {
                if (tb.Text.Trim().Length == 0) continue;
                if (!TryParseTime(tb.Text, out var t)) { Info($"\"{tb.Text}\" isn't a time. Use something like 7:00 AM.", "Training"); return; }
                parsed.Add(t.ToString(@"hh\:mm", CultureInfo.InvariantCulture));
            }
            cfg.Times = parsed.Distinct().OrderBy(x => x).ToList();
            Course.SaveConfig();
        }));
        tp.Children.Add(timeRow);
        var onCheck = new CheckBox { Content = "Lessons pop up at these times", IsChecked = cfg.On, Margin = new Thickness(0, 12, 0, 0) };
        onCheck.Checked += (_, _) => { cfg.On = true; Course.SaveConfig(); };
        onCheck.Unchecked += (_, _) => { cfg.On = false; Course.SaveConfig(); };
        tp.Children.Add(onCheck);
        TrainingHost.Children.Add(TCard(tp, 18));

        // Block lists
        var bp = new StackPanel();
        bp.Children.Add(TT("Block lists during lessons", 15, bold: true));
        bp.Children.Add(TT("These switch on when a lesson starts (apps and any Freedom web blocking in them) and off when it ends.", 12, brush: "Muted", top: 2));
        var bl = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var list in BlockLists.All.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var c = new CheckBox { Content = list.Name, IsChecked = cfg.BlockListIds.Contains(list.Id), Margin = new Thickness(0, 4, 18, 4) };
            string id = list.Id;
            c.Checked += (_, _) => { if (!cfg.BlockListIds.Contains(id)) cfg.BlockListIds.Add(id); Course.SaveConfig(); };
            c.Unchecked += (_, _) => { cfg.BlockListIds.Remove(id); Course.SaveConfig(); };
            bl.Children.Add(c);
        }
        if (BlockLists.All.Count == 0) bl.Children.Add(TT("No block lists yet (Block lists in the sidebar).", 13, brush: "Muted"));
        bp.Children.Add(bl);
        TrainingHost.Children.Add(TCard(bp));

        // Reading and review
        var rp = new StackPanel();
        rp.Children.Add(TT("Reading and review", 15, bold: true));
        var page = new CheckBox { Content = "Read in page view (turn pages like a book)", IsChecked = cfg.PageView, Margin = new Thickness(0, 10, 0, 0) };
        page.Checked += (_, _) => { cfg.PageView = true; Course.SaveConfig(); };
        page.Unchecked += (_, _) => { cfg.PageView = false; Course.SaveConfig(); };
        rp.Children.Add(page);
        var capRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        capRow.Children.Add(TT("Review questions per lesson, at most", 13));
        var cap = new TextBox { Width = 50, Margin = new Thickness(10, 0, 10, 0), Padding = new Thickness(6, 2, 6, 2), Text = cfg.ReviewCap.ToString() };
        cap.LostFocus += (_, _) => { if (int.TryParse(cap.Text, out int n) && n >= 0 && n <= 50) { cfg.ReviewCap = n; Course.SaveConfig(); } else cap.Text = cfg.ReviewCap.ToString(); };
        capRow.Children.Add(cap);
        rp.Children.Add(capRow);
        TrainingHost.Children.Add(TCard(rp));

        // How the reading looks
        var look = new StackPanel();
        look.Children.Add(TT("How the reading looks", 15, bold: true));
        look.Children.Add(TT("Font, size and background of the book's text in lessons (also under Aa while reading).", 12, brush: "Muted", top: 2));
        var lookPanel = LessonWindow.AppearancePanel(() => { });
        lookPanel.Margin = new Thickness(0, 12, 0, 0);
        look.Children.Add(lookPanel);
        TrainingHost.Children.Add(TCard(look));

        // Weekends
        var wk = new StackPanel();
        wk.Children.Add(TT("Weekends and the daily reset", 15, bold: true));
        var wkCheck = new CheckBox { Content = "Pause all practice on weekends", IsChecked = _settings.PauseWeekends, Margin = new Thickness(0, 8, 0, 0) };
        wkCheck.Checked += (_, _) => SetPauseWeekends(true);
        wkCheck.Unchecked += (_, _) => SetPauseWeekends(false);
        wk.Children.Add(wkCheck);
        wk.Children.Add(TT("On Saturdays and Sundays no lesson, card review, All-books round or Greek practice pops up by itself, and no lesson is required. You can still start any of them yourself, from this page or the dashboard.", 12, brush: "Muted", top: 4));
        var resetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var resetCheck = new CheckBox { Content = "Reset practice at", IsChecked = _settings.PracticeEndOn, VerticalAlignment = VerticalAlignment.Center };
        var resetBox = new TextBox { Width = 90, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2), IsEnabled = _settings.PracticeEndOn,
                                     Text = PracticeDayEnd is TimeSpan pe ? DateTime.Today.Add(pe).ToString("t") : "4:30 PM" };
        void ApplyReset() { SetPracticeEnd(resetCheck.IsChecked == true, ReadClockTime(resetBox.Text)); resetBox.IsEnabled = resetCheck.IsChecked == true; }
        resetCheck.Checked += (_, _) => ApplyReset();
        resetCheck.Unchecked += (_, _) => ApplyReset();
        resetBox.LostFocus += (_, _) => ApplyReset();
        resetRow.Children.Add(resetCheck);
        resetRow.Children.Add(resetBox);
        wk.Children.Add(resetRow);
        wk.Children.Add(TT("Lesson, review and practice times before the reset stop at it, and anything left on screen while you're away closes (a lesson moves to tomorrow). Times after the reset still come up and last until midnight.", 12, brush: "Muted", top: 4));
        TrainingHost.Children.Add(TCard(wk));

        // Evening card review
        var cr = new StackPanel();
        cr.Children.Add(TT("Evening card review", 15, bold: true));
        var crTime = cfg.CardReviewTimes.Count > 0 && TimeSpan.TryParse(cfg.CardReviewTimes[0], CultureInfo.InvariantCulture, out var crt) ? DateTime.Today.Add(crt).ToString("t") : "no time set";
        cr.Children.Add(TT($"A short pop-up with the hardest memory cards from that day's lesson (ones you marked ✕ come first, then ✓). Comes up at {crTime}; move it with its slider on the practice timelines. Never in a meeting or focus session, and once a day across your devices.", 12, brush: "Muted", top: 2));
        var crOn = new CheckBox { Content = "Pop up each evening", IsChecked = cfg.CardReviewOn, Margin = new Thickness(0, 10, 0, 0) };
        crOn.Checked += (_, _) => { cfg.CardReviewOn = true; Course.SaveConfig(); };
        crOn.Unchecked += (_, _) => { cfg.CardReviewOn = false; Course.SaveConfig(); };
        cr.Children.Add(crOn);
        var minRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        minRow.Children.Add(TT("Length, in minutes", 13));
        var mins = new TextBox { Width = 50, Margin = new Thickness(10, 0, 10, 0), Padding = new Thickness(6, 2, 6, 2), Text = cfg.CardReviewMinutes.ToString() };
        mins.LostFocus += (_, _) => { if (int.TryParse(mins.Text, out int n) && n >= 1 && n <= 30) { cfg.CardReviewMinutes = n; Course.SaveConfig(); } else mins.Text = cfg.CardReviewMinutes.ToString(); };
        minRow.Children.Add(mins);
        cr.Children.Add(minRow);
        var crLinks = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        crLinks.Children.Add(TLink("Set its time on the practice timelines", OpenPracticeTimelines));
        crLinks.Children.Add(TLink("Review now", () => StartCardReview(quietIfNone: false)));
        cr.Children.Add(crLinks);
        TrainingHost.Children.Add(TCard(cr));

        // All-books practice
        var ap = new StackPanel();
        ap.Children.Add(TT("All-books practice", 15, bold: true));
        ap.Children.Add(TT("Short rounds of questions from books you've finished. Set its times on the practice timelines.", 12, brush: "Muted", top: 2));
        var auto = new CheckBox { Content = "Pop up by itself at its times", IsChecked = cfg.AllBooksAuto, Margin = new Thickness(0, 10, 0, 0) };
        auto.Checked += (_, _) => { cfg.AllBooksAuto = true; Course.SaveConfig(); };
        auto.Unchecked += (_, _) => { cfg.AllBooksAuto = false; Course.SaveConfig(); };
        ap.Children.Add(auto);
        var tl = TLink("Open the practice timelines", OpenPracticeTimelines);
        tl.Margin = new Thickness(0, 10, 0, 0);
        tl.HorizontalAlignment = HorizontalAlignment.Left;
        ap.Children.Add(tl);
        TrainingHost.Children.Add(TCard(ap));
    }
}
