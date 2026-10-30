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

    /// <summary>Every 30 seconds: offers the day's lesson at each time, and opens it (required) at the last one.</summary>
    private void CheckTraining()
    {
        if (!Course.Config.On || _lessonWindow != null || VocabWindow.IsShowing) return;
        var times = TrainingTimes();
        if (times.Count == 0) return;
        var now = DateTime.Now.TimeOfDay;
        int slot = times.FindLastIndex(t => t <= now);
        if (slot < 0) return;
        if (Course.Packs.Count == 0) return;
        Course.ReloadEvents(); // another device may have finished today's lesson
        if (Course.DoneToday() || Course.NextLesson() == null) return;
        if (InMeetingOrLunch()) return;                                 // waits until the meeting ends
        bool last = slot == times.Count - 1;
        if (!last && _runner.IsRunning && _runner.Current is PlanBlock cur && cur.IsFocus) return; // waits for a break
        string key = Course.Today + "#" + slot;
        if (last) { StartLesson(required: true); return; }
        if (_trainingPromptKey == key || DateTime.Now < _trainingSnoozeUntil) return;
        _trainingPromptKey = key;
        var next = Course.NextLesson()!;
        _toast.ShowMessage("Today's lesson is ready", $"{next.Lesson.Title}. Later waits until the next lesson time.", "Start lesson", () => StartLesson(required: false));
    }

    private void StartLesson(bool required)
    {
        if (_lessonWindow != null) { _lessonWindow.Activate(); return; }
        var lesson = Course.NextLesson();
        if (lesson == null) { Info("There's no lesson to do yet. Import the next chapter pack on the Training page.", "Training"); return; }
        if (Course.Book.Count == 0) { Info("Choose your book's Word file on the Training page first, so Focus can show the reading.", "Training"); return; }
        if (Course.DoneToday() && !required &&
            !Ask("You've already done today's lesson. Start the next one now anyway?", "Training", defaultNo: true)) return;

        StartTrainingBlocking();
        var window = new LessonWindow(lesson, required, done =>
        {
            _lessonWindow = null;
            EndTrainingBlocking();
            if (!done) _trainingSnoozeUntil = DateTime.Now.AddMinutes(1);
            _refreshCoursePage?.Invoke();
        });
        _lessonWindow = window;
        window.Show();
        window.Activate();
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
            tp.Children.Add(T("Lesson times", 14, bold: true));
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
