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
    private Window? _coursePage;
    private string _trainingPromptKey = "";
    private DateTime _trainingSnoozeUntil = DateTime.MinValue;

    private void SetUpTraining()
    {
        Course.Load();
        Course.Changed += () => Dispatcher.BeginInvoke(new Action(() => _refreshCoursePage?.Invoke()));
        _tray.ContextMenuStrip?.Items.Insert(1, new System.Windows.Forms.ToolStripMenuItem("Training…", null, (_, _) => { ShowFromTray(); OpenCoursePage(); }));
        _trainingTimer.Tick += (_, _) => CheckTraining();
        _trainingTimer.Start();
    }

    private void Training_Click(object sender, RoutedEventArgs e) => OpenCoursePage();

    private List<TimeSpan> TrainingTimes() =>
        Course.Config.Times.Select(t => TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var v) ? v : (TimeSpan?)null)
              .Where(t => t != null).Select(t => t!.Value).OrderBy(t => t).ToList();

    /// <summary>Every 30 seconds: offers the day's lesson at each time, opens it (required) at the last one, and runs All-books practice.</summary>
    private void CheckTraining()
    {
        if (_lessonWindow != null || VocabWindow.IsShowing) return;
        var now = DateTime.Now.TimeOfDay;
        if (!LessonTime(now)) CheckAllBooks(now, last: false);
    }

    /// <summary>True when a lesson was offered or opened (or is waiting for a meeting or break).</summary>
    private bool LessonTime(TimeSpan now)
    {
        if (!Course.Config.On || Course.Packs.Count == 0) return false;
        var times = TrainingTimes();
        int slot = times.FindLastIndex(t => t <= now);
        if (slot < 0) return false;
        Course.ReloadEvents(); // another device may have finished today's lesson
        if (Course.DoneToday() || (Course.NextLesson() == null && Course.ExamDue() == null)) return false;
        if (InMeetingOrLunch()) return true;                                 // waits until the meeting ends
        bool last = slot == times.Count - 1;
        if (!last && _runner.IsRunning && _runner.Current is PlanBlock cur && cur.IsFocus) return true; // waits for a break
        if (last) { StartLesson(required: true); return true; }
        string key = Course.Today + "#" + slot;
        if (_trainingPromptKey == key || DateTime.Now < _trainingSnoozeUntil) return false;
        _trainingPromptKey = key;
        string what = Course.ExamDue()?.Title ?? Course.NextLesson()?.Lesson.Title ?? "Today's lesson";
        _toast.ShowMessage("Today's lesson is ready", $"{what}. Later waits until the next lesson time.", "Start lesson", () => StartLesson(required: false));
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
        if (slot < 0 || now - times[slot] > TimeSpan.FromHours(2)) return;
        string key = Course.Today + "#ab" + slot;
        if (_allBooksPromptKey == key || Course.Events.Any(e => e.Kind == "allBooksDone" && e.Day == Course.Today && e.At.ToLocalTime().TimeOfDay >= times[slot])) return;
        if (_runner.IsRunning && _runner.Current is PlanBlock cur && cur.IsFocus) return;
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
                        LessonWindow.OpenPassage(w, Math.Max(0, cs), ce, hh.Para, $"Chapter {hh.Chapter}");
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

    // ---------- The course page ----------

    private Action? _refreshCoursePage;

    private void OpenCoursePage()
    {
        if (_coursePage != null) { _coursePage.Activate(); return; }
        var body = new StackPanel { Margin = new Thickness(28, 22, 28, 28), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        var window = new Window
        {
            Title = "Training", Width = 960, Height = 820, Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = IsVisible ? this : null, AllowDrop = true
        };
        window.SetResourceReference(BackgroundProperty, "Panel");
        window.SetResourceReference(ForegroundProperty, "Text");
        _coursePage = window;

        TextBlock T(string text, double size = 14, bool bold = false, string? brush = null, double top = 0)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
            if (brush != null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }
        Border Card(UIElement child, double top = 14)
        {
            var b = new Border { Child = child, CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 14, 18, 16), Margin = new Thickness(0, top, 0, 0) };
            b.SetResourceReference(Border.BackgroundProperty, "Panel2");
            return b;
        }
        Button B(string text, Action click, bool primary = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 10, 0) };
            if (primary) b.SetResourceReference(StyleProperty, "PrimaryButton");
            b.Click += (_, _) => click();
            return b;
        }
        Button L(string text, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            b.SetResourceReference(StyleProperty, "LinkButton");
            b.Click += (_, _) => click();
            return b;
        }

        void ImportPack(string path)
        {
            try
            {
                var pack = ChapterPack.Read(path);
                var problems = Course.Check(pack, out string summary);
                string text = summary + "\n\n" + (problems.Count == 0
                    ? "✓ Every lesson's start was found in your book.\n✓ Every question has an answer and a link to its passage.\n\nAdd it to the course?"
                    : "Problems found:\n• " + string.Join("\n• ", problems.Take(12)) + (problems.Count > 12 ? $"\n…and {problems.Count - 12} more" : "") +
                      "\n\nAdd it anyway? Questions whose passage wasn't found just won't have a working Show where link.");
                if (!Ask(text, "Check chapter pack", defaultNo: problems.Count > 0)) return;
                Course.AddPack(path, pack);
            }
            catch (Exception ex) { Info("That chapter pack couldn't be read:\n\n" + ex.Message, "Training"); }
        }

        void Refresh()
        {
            body.Children.Clear();
            var cfg = Course.Config;
            var title = T("Training", 26, bold: true);
            body.Children.Add(title);
            body.Children.Add(T(cfg.Title, 15, brush: "Muted", top: 2));

            // Book file
            var bookRow = new DockPanel();
            var choose = B(Course.Book.Count == 0 ? "Choose book file…" : "Replace…", () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Word documents (*.docx)|*.docx", Title = "Choose your book's Word file" };
                if (dlg.ShowDialog(window) != true) return;
                try { int n = Course.ImportBook(dlg.FileName); Info($"Done: {n:N0} paragraphs read from {Path.GetFileName(dlg.FileName)}.", "Training"); }
                catch (Exception ex) { Info("That file couldn't be read:\n\n" + ex.Message, "Training"); }
                Refresh();
            }, primary: Course.Book.Count == 0);
            DockPanel.SetDock(choose, Dock.Right);
            bookRow.Children.Add(choose);
            var bookText = new StackPanel();
            bookText.Children.Add(T("Book file", 14, bold: true));
            bookText.Children.Add(T(Course.Book.Count == 0 ? "Not chosen yet. The reading always comes from your own Word file, word for word."
                                                            : $"{cfg.BookFileName} · {Course.Book.Count:N0} paragraphs", 13, brush: "Muted", top: 2));
            bookRow.Children.Add(bookText);
            body.Children.Add(Card(bookRow, 18));

            // Next lesson
            var next = Course.NextLesson();
            var nextPanel = new StackPanel();
            bool doneToday = Course.DoneToday();
            nextPanel.Children.Add(T(doneToday ? "Today's lesson is done" : "Next lesson", 13, brush: "Muted"));
            nextPanel.Children.Add(T(next == null ? (Course.Packs.Count == 0 ? "Import the chapter 1 pack to begin." : "Every imported lesson is done. Import the next chapter pack.")
                                                  : $"Chapter {next.Pack.Chapter}: {next.Pack.Title}  ·  {next.Lesson.Title}", 18, bold: true, top: 4));
            var times = TrainingTimes();
            nextPanel.Children.Add(T($"Streak: {Course.Streak()} day{(Course.Streak() == 1 ? "" : "s")}  ·  Lessons done: {Course.DoneLessons().Count} of {Course.Lessons().Count} imported" +
                                     (times.Count > 0 ? $"  ·  Comes up at {string.Join(", ", times.Select(t => DateTime.Today.Add(t).ToString("t")))}" : ""), 13, brush: "Muted", top: 6));
            var startRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            if (next != null) startRow.Children.Add(B("Start lesson", () => StartLesson(required: false), primary: true));
            startRow.Children.Add(B("Import chapter pack…", () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Chapter packs (*.focuspack)|*.focuspack|All files|*.*", Title = "Import a chapter pack" };
                if (dlg.ShowDialog(window) == true) ImportPack(dlg.FileName);
            }));
            nextPanel.Children.Add(startRow);
            var more = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            more.Children.Add(L("Highlights and notes", () => OpenNotesWindow(window)));
            more.Children.Add(L("Export notes to Markdown…", () => ExportNotes(window)));
            more.Children.Add(L("All-books practice", StartAllBooks));
            nextPanel.Children.Add(more);
            if (Course.ExamDue() is Course.Exam due)
                nextPanel.Children.Add(T($"Next up: {due.Title} ({due.Questions.Count} questions) instead of a reading lesson.", 13, bold: true, top: 8));
            nextPanel.Children.Add(T("Or drag a .focuspack file onto this window.", 12, brush: "Muted", top: 8));
            body.Children.Add(Card(nextPanel));

            // Chapter strip
            var strip = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var done = Course.DoneLessons();
            var lessons = Course.Lessons();
            for (int ch = 1; ch <= Math.Max(cfg.Chapters, Course.Packs.Keys.DefaultIfEmpty(0).Max()); ch++)
            {
                string state, brush;
                var mine = lessons.Where(l => l.Pack.Chapter == ch).ToList();
                if (!Course.Packs.ContainsKey(ch)) { state = "no pack yet"; brush = "Line"; }
                else if (mine.Count > 0 && mine.All(l => done.Contains(l.Index))) { state = "finished"; brush = "FocusBrush"; }
                else if (mine.Any(l => done.Contains(l.Index))) { state = "in progress"; brush = "BreakBrush"; }
                else { state = "ready"; brush = "Panel"; }
                var box = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1),
                                       ToolTip = Course.Packs.TryGetValue(ch, out var pk) ? $"Chapter {ch}: {pk.Title} ({state})" : $"Chapter {ch} ({state})",
                                       Child = new TextBlock { Text = ch.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 } };
                box.SetResourceReference(Border.BackgroundProperty, brush);
                box.SetResourceReference(Border.BorderBrushProperty, "Line");
                strip.Children.Add(box);
            }
            var stripPanel = new StackPanel();
            stripPanel.Children.Add(T("Chapters", 14, bold: true));
            stripPanel.Children.Add(strip);
            stripPanel.Children.Add(T("Gray: no pack yet · Plain: ready · Amber: in progress · Filled: finished", 12, brush: "Muted", top: 2));
            body.Children.Add(Card(stripPanel));

            // Weak spots
            var weak = Course.WeakSpots(5);
            if (weak.Count > 0)
            {
                var wp = new StackPanel();
                wp.Children.Add(T("Weak spots", 14, bold: true));
                foreach (var (concept, missed) in weak) wp.Children.Add(T($"{concept}  ·  missed {missed}×", 13, top: 4));
                body.Children.Add(Card(wp));
            }

            // Times
            var tp = new StackPanel();
            tp.Children.Add(T("Lesson times (also on the practice timelines in Settings, Greek practice)", 14, bold: true));
            tp.Children.Add(T("The first two pop up and can wait for the next time. At the last one the lesson opens and can't be closed until it's finished; quitting Focus moves it to tomorrow. A lesson never interrupts a meeting or lunch, and the first two wait for a break between focus sessions.", 12, brush: "Muted", top: 2));
            var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var boxes = new List<TextBox>();
            for (int i = 0; i < 3; i++)
            {
                var tb = new TextBox { Width = 90, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 10, 0),
                                       Text = i < times.Count ? DateTime.Today.Add(times[i]).ToString("t") : "", ToolTip = i == 2 ? "Required" : "Optional pop-up" };
                boxes.Add(tb);
                timeRow.Children.Add(tb);
            }
            timeRow.Children.Add(B("Save times", () =>
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
            var onCheck = new CheckBox { Content = "Lessons pop up at these times", IsChecked = cfg.On, Margin = new Thickness(0, 10, 0, 0) };
            onCheck.Checked += (_, _) => { cfg.On = true; Course.SaveConfig(); };
            onCheck.Unchecked += (_, _) => { cfg.On = false; Course.SaveConfig(); };
            tp.Children.Add(onCheck);
            body.Children.Add(Card(tp));

            // Exams and All-books practice
            var ep = new StackPanel();
            ep.Children.Add(T("Exams and All-books practice", 14, bold: true));
            ep.Children.Add(T("A unit exam follows the last chapter of each part of the book, and a final exam the whole book. Enter the chapters where each part ends (for example 3, 9, 18). Passing the final finishes the book; its questions then join All-books practice.", 12, brush: "Muted", top: 2));
            var unitRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var units = new TextBox { Width = 200, Padding = new Thickness(6, 4, 6, 4), Text = string.Join(", ", cfg.UnitEnds), Margin = new Thickness(0, 0, 10, 0) };
            unitRow.Children.Add(units);
            unitRow.Children.Add(B("Save", () =>
            {
                var list = new List<int>();
                foreach (var part in units.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(part, out int n) && n >= 1 && n <= cfg.Chapters) list.Add(n); else { Info($"\"{part}\" isn't a chapter number from 1 to {cfg.Chapters}.", "Training"); return; }
                cfg.UnitEnds = list.Distinct().OrderBy(n => n).ToList();
                Course.SaveConfig();
            }));
            ep.Children.Add(unitRow);
            var passed = Course.ExamsPassed();
            if (passed.Count > 0) ep.Children.Add(T("Passed: " + string.Join(", ", passed.OrderBy(x => x)), 12, brush: "Muted", top: 6));
            var abAuto = new CheckBox { Content = "All-books practice pops up by itself at its times (set them on the practice timelines)", IsChecked = cfg.AllBooksAuto, Margin = new Thickness(0, 10, 0, 0) };
            abAuto.Checked += (_, _) => { cfg.AllBooksAuto = true; Course.SaveConfig(); };
            abAuto.Unchecked += (_, _) => { cfg.AllBooksAuto = false; Course.SaveConfig(); };
            ep.Children.Add(abAuto);
            ep.Children.Add(T(Course.Finished ? "All-books practice is ready." : "All-books practice starts once you've passed this book's final exam.", 12, brush: "Muted", top: 4));
            body.Children.Add(Card(ep));

            // Block lists
            var bp = new StackPanel();
            bp.Children.Add(T("Block lists during lessons", 14, bold: true));
            bp.Children.Add(T("These switch on when a lesson starts (apps and any Freedom web blocking in them) and off when it ends.", 12, brush: "Muted", top: 2));
            var bl = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var list in BlockLists.All.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var c = new CheckBox { Content = list.Name, IsChecked = cfg.BlockListIds.Contains(list.Id), Margin = new Thickness(0, 4, 18, 4) };
                string id = list.Id;
                c.Checked += (_, _) => { if (!cfg.BlockListIds.Contains(id)) cfg.BlockListIds.Add(id); Course.SaveConfig(); };
                c.Unchecked += (_, _) => { cfg.BlockListIds.Remove(id); Course.SaveConfig(); };
                bl.Children.Add(c);
            }
            if (BlockLists.All.Count == 0) bl.Children.Add(T("No block lists yet (Block lists in the sidebar).", 13, brush: "Muted"));
            bp.Children.Add(bl);
            body.Children.Add(Card(bp));

            // Hidden and flagged
            var hidden = Course.Hidden().ToList();
            var flagged = Course.Flagged().ToList();
            var hp = new StackPanel();
            hp.Children.Add(T("Hidden and flagged questions", 14, bold: true));
            hp.Children.Add(T($"{hidden.Count} hidden · {flagged.Count} flagged" + (flagged.Count > 0 ? $" ({string.Join(", ", flagged.Take(8))}{(flagged.Count > 8 ? "…" : "")}): ask Claude to fix these in their chapter packs." : ""), 13, brush: "Muted", top: 2));
            var all = Course.Packs.Values.SelectMany(p => p.Lessons).SelectMany(l => l.AllQuestions).ToDictionary(q => q.Id, q => q, StringComparer.Ordinal);
            foreach (var id in hidden.Take(30))
            {
                var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
                string qid = id;
                var restore = L("Restore", () => Course.Add(new TrainingEvent { Kind = "unhide", Question = qid }));
                DockPanel.SetDock(restore, Dock.Right);
                row.Children.Add(restore);
                string text = all.TryGetValue(id, out var q) ? (q.Kind == "card" ? q.Front : q.Prompt) : id;
                row.Children.Add(T(text, 13));
                hp.Children.Add(row);
            }
            body.Children.Add(Card(hp));
        }

        window.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files.Where(f => f.EndsWith(".focuspack", StringComparison.OrdinalIgnoreCase))) ImportPack(f);
        };
        _refreshCoursePage = () => { if (_coursePage != null) Refresh(); };
        window.Closed += (_, _) => { _coursePage = null; _refreshCoursePage = null; };
        Refresh();
        window.Show();
    }
}
