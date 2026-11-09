using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FocusApp;

/// <summary>
/// The Training page (design A1): today's lesson and this week's numbers, today's practice on a day line to scale,
/// a card for each practice, your books (with Go for an extra lesson), and Lessons this week, where lessons can be
/// dragged to another day, removed or added for that week only (the weekly plan stays the default).
/// </summary>
public partial class MainWindow
{
    private int _weekOffset; // 0 = this week, 1 = next week...

    private static readonly string[] BookColors = { "#6FB5A8", "#8FA8E0", "#E0A86F", "#C79BE0", "#E08FA8" };
    private static Brush BookBrush(CourseInfo c)
    {
        int i = Math.Max(0, Course.Config.Courses.IndexOf(c));
        return (SolidColorBrush)new BrushConverter().ConvertFromString(BookColors[i % BookColors.Length])!;
    }

    private static Border TBox(UIElement child, bool accent = false, Thickness? padding = null)
    {
        var b = new Border { Child = child, CornerRadius = new CornerRadius(14), Padding = padding ?? new Thickness(24, 20, 24, 20), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "Panel");
        b.SetResourceReference(Border.BorderBrushProperty, accent ? "FocusBrush" : "Line");
        return b;
    }

    private TextBlock TLabel(string text, string brush = "Muted")
    {
        var t = TT(text, 11, brush: brush);
        t.FontWeight = FontWeights.SemiBold;
        return t;
    }

    private void BuildLibrary()
    {
        _trainingView = "library";
        Course.UseToday(); // the page opens on today's next lesson's book
        TrainingHost.Children.Add(TrainingHeader("Training", DateTime.Today.ToString("dddd, MMMM d"), back: false, gear: true));
        if (Course.Config.Courses.Count == 0)
        {
            var empty = new StackPanel();
            empty.Children.Add(TT("Add your first book", 20, bold: true));
            empty.Children.Add(TT("Choose its Word file, then import its chapter packs.", 13, brush: "Muted", top: 6));
            empty.Children.Add(TButton("Add a book", AddBook, primary: true));
            ((Button)empty.Children[^1]).Margin = new Thickness(0, 14, 0, 0);
            ((Button)empty.Children[^1]).HorizontalAlignment = HorizontalAlignment.Left;
            var ec = TBox(empty); ec.Margin = new Thickness(0, 18, 0, 0);
            TrainingHost.Children.Add(ec);
            return;
        }

        // ----- Today's lesson and This week -----
        var top = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var day = Course.DayLessons(DateTime.Today);
        var dueBook = Course.NextDueToday();
        var tp = new StackPanel();
        if (day.Count == 0)
        {
            tp.Children.Add(TLabel("TODAY"));
            tp.Children.Add(TT(PracticePausedToday() || DateTime.Today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "Weekend: no lessons" : "No lesson today", 22, bold: true, top: 8));
            tp.Children.Add(TT("Use Go beside a book below for an extra lesson, or add one to today in Lessons this week.", 13, brush: "Muted", top: 6));
        }
        else if (dueBook == null)
        {
            tp.Children.Add(TLabel(day.Count > 1 ? "TODAY'S LESSONS" : "TODAY'S LESSON", "FocusBrush"));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            row.Children.Add(DoneMark(24));
            var t = TT(day.Count > 1 ? "Today's lessons are done" : "Today's lesson is done", 22, bold: true);
            t.Margin = new Thickness(10, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(t);
            tp.Children.Add(row);
            tp.Children.Add(TT("Nice work. Use Go beside a book for an extra lesson.", 13, brush: "Muted", top: 8));
        }
        else
        {
            var o = Course.OutlineOf(dueBook.Id);
            int doneN = day.Count(x => x.Done);
            string which = day.Count > 1 ? $" · {doneN + 1} OF {day.Count}" : "";
            tp.Children.Add(TLabel($"TODAY'S LESSON{which} · {Course.RoleOf(dueBook).ToUpperInvariant()}", "FocusBrush"));
            var next = o.Lessons.FirstOrDefault(l => !o.Done.Contains(l.Index));
            if (next != null)
            {
                tp.Children.Add(TT($"{ShortTitle(dueBook)}, Lesson {next.Index + 1}: {next.Lesson.Title}", 22, bold: true, top: 8));
                tp.Children.Add(TT($"Chapter {next.Pack.Chapter} · about 25 minutes, then 5 minutes of cards" + (o.InProgress == next.Index ? " · you've started it" : ""), 13.5, brush: "Muted", top: 6));
                string id = dueBook.Id;
                var go = TButton(o.InProgress == next.Index ? "Continue lesson" : "Start lesson", () => StartLesson(required: false, bookId: id), primary: true);
                go.Padding = new Thickness(24, 10, 24, 10); go.Margin = new Thickness(0, 16, 0, 0); go.HorizontalAlignment = HorizontalAlignment.Left;
                tp.Children.Add(go);
            }
            else
            {
                tp.Children.Add(TT($"{ShortTitle(dueBook)}: import chapter {o.NextChapter}", 22, bold: true, top: 8));
                tp.Children.Add(TT("Its next lesson needs the next chapter pack.", 13.5, brush: "Muted", top: 6));
                string id = dueBook.Id;
                var imp = TButton($"Import chapter {o.NextChapter}…", () => ImportPackFor(id), primary: true);
                imp.Margin = new Thickness(0, 16, 0, 0); imp.HorizontalAlignment = HorizontalAlignment.Left;
                tp.Children.Add(imp);
            }
        }
        UIElement todayContent = tp;
        if (dueBook != null)
        {
            var withCover = new DockPanel();
            var cover = CoverImage(dueBook, 76, 110);
            cover.Margin = new Thickness(0, 0, 22, 0); cover.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(cover, Dock.Left);
            withCover.Children.Add(cover);
            withCover.Children.Add(tp);
            todayContent = withCover;
        }
        var todayCard = TBox(todayContent, accent: dueBook != null);
        top.Children.Add(todayCard);

        var stats = new StackPanel();
        stats.Children.Add(TLabel("THIS WEEK"));
        var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        int planned = 0, done = 0;
        for (int i = 0; i < 7; i++) { var dl = Course.DayLessons(monday.AddDays(i)); planned += dl.Count; done += dl.Count(x => x.Done); }
        string ymdMon = monday.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        int rounds = Course.Events.Count(e => e.Kind is "allBooksDone" or "cardReviewDone" or "timelineDone" && string.CompareOrdinal(e.Day, ymdMon) >= 0);
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
        void Stat(string value, string what)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 8, 8, 0) };
            sp.Children.Add(TT(value, 24, bold: true));
            sp.Children.Add(TT(what, 12, brush: "Muted"));
            grid.Children.Add(sp);
        }
        Stat(Course.PagesReadThisWeek().ToString(), "pages read");
        Stat($"{done} of {planned}", "lessons done");
        Stat(rounds.ToString(), "practice rounds");
        Stat(WeeksInARow().ToString(), "weeks in a row");
        stats.Children.Add(grid);
        var statCard = TBox(stats);
        Grid.SetColumn(statCard, 2);
        top.Children.Add(statCard);
        TrainingHost.Children.Add(top);

        // ----- Catch-up (optional, one at a time) -----
        if (Course.MissedLesson() is { } missed)
        {
            var missBook = missed.Book; var missDay = missed.Day;
            var row = new DockPanel();
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            btns.Children.Add(TButton("Do it now", () => StartLesson(required: false, bookId: missBook.Id)));
            btns.Children.Add(TButton("Let it go", () => { Course.SkipCatchUp(missBook, missDay); RefreshTraining(); }));
            DockPanel.SetDock(btns, Dock.Right);
            row.Children.Add(btns);
            var label = TT($"CATCH-UP · OPTIONAL  ·  {ShortTitle(missBook)}, from {missDay:dddd}", 13, bold: true);
            label.Foreground = Amber; label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            var cc = TBox(row, padding: new Thickness(22, 12, 18, 12));
            cc.BorderBrush = Amber; cc.Margin = new Thickness(0, 16, 0, 0);
            TrainingHost.Children.Add(cc);
        }

        // ----- Today's practice: a day line to scale -----
        if (Course.Config.ShowPracticeOnTraining)
        {
            var pr = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var edit = TLink("Edit timeline", OpenPracticeTimelines); edit.Margin = new Thickness(0); DockPanel.SetDock(edit, Dock.Right);
            head.Children.Add(edit);
            head.Children.Add(TT("Today's practice", 16, bold: true));
            pr.Children.Add(head);
            var roundsToday = PracticeRoundStates();
            if (roundsToday.Count == 0) pr.Children.Add(TT(PracticePausedToday() ? "Practice is paused today." : "No practice rounds left today.", 13, brush: "Muted"));
            else
            {
                var canvas = new Canvas { Height = 64 + 22 * Math.Max(0, roundsToday.GroupBy(r => r.At).Max(g => g.Count()) - 1), ClipToBounds = false };
                canvas.SizeChanged += (_, _) => DrawDayLine(canvas, roundsToday);
                pr.Children.Add(canvas);
            }
            var pc = TBox(pr, padding: new Thickness(24, 18, 24, 16)); pc.Margin = new Thickness(0, 16, 0, 0);
            TrainingHost.Children.Add(pc);
        }

        // ----- Practices -----
        var practices = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 8, -14, 0) };
        var all = TodaysPracticeRounds();
        string NextAt(string name) => all.Where(r => r.Name == name && r.At > DateTime.Now.TimeOfDay).Select(r => "next at " + DateTime.Today.Add(r.At).ToString("t")).FirstOrDefault() ?? "";
        string Join(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0)) is { Length: > 0 } j ? char.ToUpper(j[0]) + j[1..] : "Nothing set today";
        int dueAll = Course.DueByBook().Sum(d => d.Due);
        void Practice(string name, string status, Action start)
        {
            var row = new DockPanel();
            var b = TButton("Start", start); b.Margin = new Thickness(10, 0, 0, 0); b.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(b, Dock.Right);
            row.Children.Add(b);
            var sp = new StackPanel();
            sp.Children.Add(TT(name, 15, bold: true));
            sp.Children.Add(TT(status, 12.5, brush: "Muted", top: 3));
            row.Children.Add(sp);
            var c = TBox(row, padding: new Thickness(18, 14, 16, 14)); c.Margin = new Thickness(0, 8, 14, 0);
            practices.Children.Add(c);
        }
        Practice("Books practice", Join(dueAll > 0 ? $"{dueAll} due" : "Nothing due", NextAt("Books practice")), () => StartBooksPractice());
        Practice("Book flashcards", Join(Course.CardReviewDoneToday() ? "Done today" : "", NextAt("Book flashcards")), () => StartBookFlashcards());
        int tlDue = Course.TimelineDueCount();
        Practice("Timeline", Join(tlDue > 0 ? $"{tlDue} due" : Course.TimelineDoneToday() ? "Done today" : "", NextAt("Timeline")), () => StartTimelinePractice());
        foreach (var (p, name, start) in new (string, string, Action)[]
                 {
                     ("words", "Greek words", () => DashGreek_Click(this, new RoutedEventArgs())),
                     ("alphabet", "Greek alphabet", () => DashAlpha_Click(this, new RoutedEventArgs())),
                     ("concepts", "Greek concepts", () => StartConceptQuiz(fromMenu: true))
                 })
        {
            string status;
            try
            {
                int slots = SlotsFor(PerDayFor(p), p).Count, doneSlots = DoneToday(DoneRecordFor(p)).Count(i => i < slots);
                status = slots == 0 ? "Not set for today" : doneSlots >= slots ? "Done today" : Join($"{doneSlots} of {slots} done", NextAt(name));
            }
            catch { status = ""; }
            Practice(name, status, start);
        }
        TrainingHost.Children.Add(practices);

        // ----- Books -----
        var books = new StackPanel();
        var bh = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var addLink = TLink("+ Add a book", AddBook); addLink.Margin = new Thickness(0); DockPanel.SetDock(addLink, Dock.Right);
        bh.Children.Add(addLink);
        bh.Children.Add(TT("Books", 16, bold: true));
        books.Children.Add(bh);
        foreach (var c in Course.Config.Courses.OrderBy(x => Array.IndexOf(Course.Roles, Course.RoleOf(x))))
        {
            var o = Course.OutlineOf(c.Id);
            var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel { Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var nrow = new StackPanel { Orientation = Orientation.Horizontal };
            var thumb = CoverImage(c, 30, 44);
            thumb.Margin = new Thickness(0, 0, 12, 0);
            thumb.ToolTip = "Cover photo and name";
            var book = c;
            thumb.MouseLeftButtonUp += (_, e) => { e.Handled = true; CoverMenu(thumb, book); };
            nrow.Children.Add(thumb);
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(TT(c.Title.Length > 38 ? ShortTitle(c) : c.Title, 14, bold: true));
            titles.Children.Add(TT($"{Course.RoleOf(c)} · {Course.DaysText(c)}", 12, brush: "Muted", top: 2));
            nrow.Children.Add(titles);
            name.Children.Add(nrow);
            string id = c.Id;
            name.MouseLeftButtonUp += (_, _) => { if (id != Course.Current.Id) Course.Open(id); _trainingView = "book"; RefreshTraining(); };
            g.Children.Add(name);
            double frac = o.Lessons.Count == 0 ? 0 : (double)o.Done.Count / Math.Max(o.Lessons.Count, 1);
            var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
            var track = new Border { CornerRadius = new CornerRadius(3) }; track.SetResourceReference(Border.BackgroundProperty, "Panel2");
            var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Background = BookBrush(c) };
            bar.Children.Add(track); bar.Children.Add(fill);
            bar.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * Math.Clamp(frac, 0, 1);
            Grid.SetColumn(bar, 1); g.Children.Add(bar);
            var prog = TT($"{o.Done.Count} of {o.Lessons.Count} lessons", 12.5, brush: "Muted");
            prog.TextAlignment = TextAlignment.Right; prog.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(prog, 2); g.Children.Add(prog);
            var go = TButton("Go", () => StartLesson(required: false, bookId: id));
            go.Margin = new Thickness(14, 0, 0, 0); go.VerticalAlignment = VerticalAlignment.Center;
            go.ToolTip = "Start the next lesson in this book (an extra lesson)";
            go.IsEnabled = o.Lessons.Any(l => !o.Done.Contains(l.Index));
            Grid.SetColumn(go, 3); g.Children.Add(go);
            books.Children.Add(g);
        }
        var bc = TBox(books, padding: new Thickness(24, 16, 24, 18)); bc.Margin = new Thickness(0, 16, 0, 0);
        TrainingHost.Children.Add(bc);

        // ----- Lessons this week -----
        TrainingHost.Children.Add(BuildWeekLessons());
    }

    /// <summary>A book's cover photo, or (with none) a tile in the book's color with its first letter.</summary>
    private static FrameworkElement CoverImage(CourseInfo c, double w, double h)
    {
        var frame = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(Math.Max(3, w / 16)), ClipToBounds = true, BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        if (Course.CoverPath(c) is string path)
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = (int)(w * 2); bmp.UriSource = new Uri(path); bmp.EndInit();
                frame.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                return frame;
            }
            catch (Exception ex) { Log.Error("Cover " + c.Id, ex); }
        }
        frame.Background = BookBrush(c);
        frame.Child = new TextBlock { Text = c.Title.Trim().Length > 0 ? c.Title.Trim()[..1].ToUpperInvariant() : "?", FontSize = Math.Max(10, w * 0.45), FontWeight = FontWeights.SemiBold,
                                      Foreground = Brushes.White, Opacity = 0.9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return frame;
    }

    /// <summary>Asks for a new name for a book.</summary>
    private void RenameBook(CourseInfo c)
    {
        var box = new TextBox { Text = c.Title, Margin = new Thickness(20, 8, 20, 12), Padding = new Thickness(6, 4, 6, 4) };
        var ok = new Button { Content = "Rename", IsDefault = true, Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(20, 0, 20, 20), HorizontalAlignment = HorizontalAlignment.Right };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "The book's title (and author, if you like). Its lessons and progress stay with it.", Margin = new Thickness(20, 20, 20, 0), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(box); sp.Children.Add(ok);
        var dlg = new Window { Title = "Rename book", Width = 460, SizeToContent = SizeToContent.Height, Content = sp, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        dlg.SetResourceReference(BackgroundProperty, "Panel");
        dlg.SetResourceReference(ForegroundProperty, "Text");
        ok.Click += (_, _) => { if (box.Text.Trim().Length > 0) dlg.DialogResult = true; };
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        if (dlg.ShowDialog() != true) return;
        Course.RenameCourse(c.Id, box.Text);
        RefreshTraining();
    }

    /// <summary>Chooses a picture for a book's cover.</summary>
    private void ChooseCover(CourseInfo c)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures (*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp", Title = $"Cover photo for {ShortTitle(c)}" };
        if (dlg.ShowDialog(this) != true) return;
        try { Course.SetCover(c.Id, dlg.FileName); }
        catch (Exception ex) { Info("That picture couldn't be used:\n\n" + ex.Message, "Training"); }
        RefreshTraining();
    }

    /// <summary>The menu on a book's cover: change or remove the cover, and rename the book.</summary>
    private void CoverMenu(FrameworkElement target, CourseInfo c)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string text, Action run) { var item = new MenuItem { Header = text }; item.Click += (_, _) => run(); menu.Items.Add(item); }
        Add(Course.CoverPath(c) != null ? "Change cover photo…" : "Add a cover photo…", () => ChooseCover(c));
        if (c.Cover.Length > 0) Add("Remove cover photo", () => { Course.SetCover(c.Id, null); RefreshTraining(); });
        Add("Rename book…", () => RenameBook(c));
        menu.IsOpen = true;
    }

    private void ImportPackFor(string bookId)
    {
        Course.UseBookQuietly(bookId);
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Chapter packs (*.focuspack)|*.focuspack|All files|*.*", Title = "Import a chapter pack" };
        if (dlg.ShowDialog(this) == true) ImportPack(dlg.FileName);
        RefreshTraining();
    }

    /// <summary>Weeks in a row (ending this week) with every planned lesson done; this week counts once nothing is missed so far.</summary>
    private static int WeeksInARow()
    {
        var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        int n = 0;
        for (int w = 0; w < 104; w++)
        {
            var start = monday.AddDays(-7 * w);
            int planned = 0, done = 0;
            for (int i = 0; i < 7; i++)
            {
                var d = start.AddDays(i);
                if (d >= DateTime.Today) continue; // today and later don't count against a week
                var dl = Course.DayLessons(d); planned += dl.Count; done += dl.Count(x => x.Done);
            }
            if (planned == 0) { if (w == 0) continue; break; }
            if (done < planned) break;
            n++;
        }
        return n;
    }

    /// <summary>Today's practice rounds with done, next and required marks.</summary>
    private List<(TimeSpan At, string Name, bool Done, bool Next, bool Required)> PracticeRoundStates()
    {
        var list = new List<(TimeSpan, string, bool, bool, bool)>();
        var rounds = TodaysPracticeRounds();
        int lessonsDone = Course.DoneTodayCount(), lessonSeen = 0;
        var lessonTimes = rounds.Where(r => r.Name == "Book lesson").Select(r => r.At).ToList();
        var greekDone = new Dictionary<string, HashSet<TimeSpan>>();
        foreach (var (p, name) in new[] { ("words", "Greek words"), ("alphabet", "Greek alphabet"), ("concepts", "Greek concepts") })
        {
            try
            {
                var slots = SlotsFor(PerDayFor(p), p);
                greekDone[name] = DoneToday(DoneRecordFor(p)).Where(i => i < slots.Count).Select(i => slots[i].TimeOfDay).ToHashSet();
            }
            catch { greekDone[name] = new(); }
        }
        bool DoneSince(string kind, TimeSpan at) => Course.Events.Any(e => e.Kind == kind && e.Day == Course.Today && e.At.ToLocalTime().TimeOfDay >= at);
        foreach (var (at, name) in rounds)
        {
            bool done = name switch
            {
                "Book lesson" => lessonSeen++ < lessonsDone,
                "Books practice" => DoneSince("allBooksDone", at),
                "Book flashcards" => DoneSince("cardReviewDone", at),
                "Timeline" => DoneSince("timelineDone", at),
                _ => greekDone.TryGetValue(name, out var set) && set.Contains(at)
            };
            bool required = name == "Book lesson" && lessonTimes.Count > 0 && at == lessonTimes[^1] && lessonsDone == 0;
            list.Add((at, name, done, false, required));
        }
        int next = list.FindIndex(x => !x.Item3 && x.Item1 >= DateTime.Now.TimeOfDay);
        if (next >= 0) { var x = list[next]; list[next] = (x.Item1, x.Item2, x.Item3, true, x.Item5); }
        return list;
    }

    /// <summary>The day line: 6 AM to 10 PM (wider if a round falls outside), a now line, and each round at its exact time.
    /// Rounds at the same time form a small chain under it.</summary>
    private void DrawDayLine(Canvas canvas, List<(TimeSpan At, string Name, bool Done, bool Next, bool Required)> rounds)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth;
        if (w < 100) return;
        double startH = Math.Min(6, Math.Floor(rounds.Min(r => r.At.TotalHours)));
        double endH = Math.Max(22, Math.Ceiling(rounds.Max(r => r.At.TotalHours)));
        double X(TimeSpan t) => 8 + (t.TotalHours - startH) / (endH - startH) * (w - 16);
        const double lineY = 22;
        var track = new Rectangle { Width = w - 16, Height = 4, RadiusX = 2, RadiusY = 2 };
        track.SetResourceReference(Shape.FillProperty, "Panel2");
        Canvas.SetLeft(track, 8); Canvas.SetTop(track, lineY); canvas.Children.Add(track);
        var now = DateTime.Now.TimeOfDay;
        if (now.TotalHours > startH && now.TotalHours < endH)
        {
            var past = new Rectangle { Width = Math.Max(0, X(now) - 8), Height = 4, RadiusX = 2, RadiusY = 2, Opacity = 0.45 };
            past.SetResourceReference(Shape.FillProperty, "FocusBrush");
            Canvas.SetLeft(past, 8); Canvas.SetTop(past, lineY); canvas.Children.Add(past);
            var nowLine = new Rectangle { Width = 2, Height = 32, Fill = (Brush)new BrushConverter().ConvertFromString("#E0705F")! };
            Canvas.SetLeft(nowLine, X(now) - 1); Canvas.SetTop(nowLine, lineY - 14); canvas.Children.Add(nowLine);
        }
        // Hour labels along the bottom of the line.
        for (double h = Math.Ceiling(startH / 3) * 3; h <= endH; h += 3)
        {
            var lab = TT(h == 12 ? "Noon" : DateTime.Today.AddHours(h).ToString("h tt").Replace(":00", ""), 10.5, brush: "Muted");
            lab.Width = 50; lab.TextAlignment = TextAlignment.Center; lab.Opacity = 0.8;
            Canvas.SetLeft(lab, X(TimeSpan.FromHours(h)) - 25); Canvas.SetTop(lab, -2); canvas.Children.Add(lab);
        }
        double lastLabelRight = -1e9; int stagger = 0;
        foreach (var group in rounds.GroupBy(r => r.At))
        {
            double x = X(group.Key);
            int k = 0;
            foreach (var r in group)
            {
                double y = lineY + 2 + k * 22;
                if (k > 0)
                {
                    var link = new Rectangle { Width = 2, Height = 12 };
                    link.SetResourceReference(Shape.FillProperty, "Line");
                    Canvas.SetLeft(link, x - 1); Canvas.SetTop(link, y - 14); canvas.Children.Add(link);
                }
                var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2) };
                if (r.Done) { dot.SetResourceReference(Border.BackgroundProperty, "FocusBrush"); dot.SetResourceReference(Border.BorderBrushProperty, "FocusBrush"); dot.Child = new TextBlock { Text = "✓", FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("OnAccent") }; }
                else
                {
                    dot.SetResourceReference(Border.BackgroundProperty, "Panel");
                    if (r.Next || r.Required) dot.BorderBrush = Amber; else dot.SetResourceReference(Border.BorderBrushProperty, "FocusBrush");
                }
                dot.ToolTip = $"{DateTime.Today.Add(r.At):t} · {r.Name}" + (r.Done ? " · done" : r.Required ? " · required" : r.Next ? " · next" : "");
                Canvas.SetLeft(dot, x - 9); Canvas.SetTop(dot, y - 7); canvas.Children.Add(dot);
                // Its time and name to the right of the dot (first in a chain: under the line if crowded).
                string text = (k == 0 ? DateTime.Today.Add(r.At).ToString("t") + " " : "") + r.Name + (r.Required ? " · required" : r.Next ? " · next" : "");
                var lab = TT(text, 11, brush: r.Next || r.Required ? null : r.Done ? "FocusBrush" : "Muted");
                if (r.Next || r.Required) lab.Foreground = Amber;
                lab.TextWrapping = TextWrapping.NoWrap;
                lab.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lx = x + 12, ly = y - 6;
                if (k == 0)
                {
                    if (lx < lastLabelRight + 6) { stagger = (stagger + 1) % 2; } else stagger = 0;
                    if (stagger == 1) { lx = x - lab.DesiredSize.Width / 2; ly = y + 14; }
                    lastLabelRight = Math.Max(lastLabelRight, lx + lab.DesiredSize.Width);
                }
                if (lx + lab.DesiredSize.Width > w) lx = x - 12 - lab.DesiredSize.Width;
                Canvas.SetLeft(lab, lx); Canvas.SetTop(lab, ly); canvas.Children.Add(lab);
                k++;
            }
        }
    }

    // ---------- Lessons this week ----------

    /// <summary>Mon to Sun, each day's lessons; drag a lesson to another day (onto a day with one to make it two), × to
    /// remove one, + Add to choose a book. Changes apply to that week only. This week and the weeks ahead.</summary>
    private Border BuildWeekLessons()
    {
        var thisMonday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        var monday = thisMonday.AddDays(7 * _weekOffset);
        var panel = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        if (Course.WeekChanged(monday) && monday.AddDays(6) >= DateTime.Today)
        {
            var reset = TLink("Back to my weekly plan", () => { Course.ResetWeek(monday); RefreshTraining(); });
            nav.Children.Add(reset);
        }
        var prev = TButton("‹", () => { _weekOffset = Math.Max(0, _weekOffset - 1); RefreshTraining(); }); prev.IsEnabled = _weekOffset > 0; prev.Padding = new Thickness(10, 3, 10, 4); prev.Margin = new Thickness(0, 0, 6, 0);
        var range = TT($"{monday:MMM d} – {monday.AddDays(6):MMM d}", 13, brush: "Muted"); range.VerticalAlignment = VerticalAlignment.Center; range.Margin = new Thickness(4, 0, 10, 0);
        var nextW = TButton("›", () => { _weekOffset = Math.Min(8, _weekOffset + 1); RefreshTraining(); }); nextW.Padding = new Thickness(10, 3, 10, 4); nextW.Margin = new Thickness(0);
        nav.Children.Add(prev); nav.Children.Add(range); nav.Children.Add(nextW);
        DockPanel.SetDock(nav, Dock.Right);
        head.Children.Add(nav);
        head.Children.Add(TT(_weekOffset == 0 ? "Lessons this week" : _weekOffset == 1 ? "Lessons next week" : "Lessons, week of " + monday.ToString("MMM d"), 16, bold: true));
        panel.Children.Add(head);

        // Which lesson each planned slot will be: walk from today to the shown week's end; each book's lessons in order.
        var pointer = Course.Config.Courses.ToDictionary(c => c.Id, _ => 0);
        var labels = new Dictionary<(DateTime, int), (string Text, string Sub)>();
        for (var d = DateTime.Today; d <= monday.AddDays(6); d = d.AddDays(1))
        {
            var dl = Course.DayLessons(d);
            for (int i = 0; i < dl.Count; i++)
            {
                if (d == DateTime.Today && dl[i].Done) continue;
                var c = dl[i].Book;
                var o = Course.OutlineOf(c.Id);
                var undone = o.Lessons.Where(l => !o.Done.Contains(l.Index)).ToList();
                int k = pointer[c.Id]++;
                labels[(d, i)] = k < undone.Count
                    ? ($"{ShortTitle(c)} · Lesson {undone[k].Index + 1}", undone[k].Lesson.Title + (o.InProgress == undone[k].Index ? " · started" : ""))
                    : (ShortTitle(c), $"Import chapter {o.NextChapter + (k - undone.Count) / 3}");
            }
        }

        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7 };
        for (int di = 0; di < 7; di++)
        {
            var d = monday.AddDays(di);
            bool past = d < DateTime.Today, isToday = d == DateTime.Today;
            var cell = new StackPanel();
            cell.Children.Add(TT(d.ToString("ddd d"), 11.5, bold: isToday, brush: isToday ? "FocusBrush" : "Muted"));
            var books = Course.BooksFor(d);
            var dl = Course.DayLessons(d);
            // Done lessons on past days (and today) show what was actually done.
            var doneEvents = Course.Events.Where(e => e.Kind == "lessonDone" && e.Lesson >= 0 && e.Day == d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToList();
            for (int i = 0; i < dl.Count; i++)
            {
                var (c, isDone) = dl[i];
                string text, sub, state;
                if (isDone || past)
                {
                    int nth = dl.Take(i + 1).Count(x => x.Book.Id == c.Id);
                    var ev = doneEvents.Where(e => (e.CourseId.Length == 0 ? "desilva-nt" : e.CourseId) == c.Id).Skip(nth - 1).FirstOrDefault();
                    var o = Course.OutlineOf(c.Id);
                    var l = ev != null ? o.Lessons.FirstOrDefault(x => x.Index == ev.Lesson) : null;
                    text = l != null ? $"{ShortTitle(c)} · Lesson {l.Index + 1}" : ShortTitle(c);
                    sub = l?.Lesson.Title ?? (isDone ? "" : "Not done");
                    state = isDone ? "Done" : "Missed";
                }
                else
                {
                    (text, sub) = labels.TryGetValue((d, i), out var lab) ? lab : (ShortTitle(c), "");
                    state = isToday ? "Today" : "Planned";
                }
                cell.Children.Add(LessonCard(c, text, sub, state, d, i, editable: !past && !isDone, books));
            }
            if (!past)
            {
                var add = new Button { Content = "+ Add", Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 4, 6, 4), HorizontalAlignment = HorizontalAlignment.Stretch, Opacity = 0.75 };
                add.SetResourceReference(StyleProperty, "LinkButton");
                var day = d;
                add.Click += (_, _) =>
                {
                    var menu = new ContextMenu { PlacementTarget = add, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                    foreach (var c in Course.Config.Courses)
                    {
                        var item = new MenuItem { Header = ShortTitle(c) };
                        string id = c.Id;
                        item.Click += (_, _) => { var list = Course.BooksFor(day).Select(x => x.Id).ToList(); list.Add(id); Course.SetDay(day, list); RefreshTraining(); };
                        menu.Items.Add(item);
                    }
                    menu.IsOpen = true;
                };
                cell.Children.Add(add);
            }
            var border = new Border { Child = cell, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 8, 8, 10), Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(1), MinHeight = 110, AllowDrop = !past };
            border.SetResourceReference(Border.BorderBrushProperty, isToday ? "FocusBrush" : "Line");
            if (isToday) border.SetResourceReference(Border.BackgroundProperty, "FocusTint"); else border.Background = Brushes.Transparent;
            if (past) border.Opacity = 0.7;
            var target = d;
            border.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("focus-lesson") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            border.Drop += (_, e) =>
            {
                if (e.Data.GetData("focus-lesson") is not string data) return;
                var parts = data.Split('|');
                var from = DateTime.ParseExact(parts[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                int pos = int.Parse(parts[1]);
                if (from == target) return;
                var src = Course.BooksFor(from).Select(x => x.Id).ToList();
                if (pos < 0 || pos >= src.Count) return;
                string id = src[pos];
                src.RemoveAt(pos);
                var dst = Course.BooksFor(target).Select(x => x.Id).ToList();
                dst.Add(id);
                Course.SetDay(from, src, save: false);
                Course.SetDay(target, dst);
                Dispatcher.BeginInvoke(new Action(RefreshTraining));
            };
            grid.Children.Add(border);
        }
        panel.Children.Add(grid);
        var help = TT("Drag a lesson to another day (onto a day with a lesson to make it two), × to remove one, + Add to choose a book. " +
                      "Changes apply to that week only; your weekly plan stays the default.", 12, brush: "Muted", top: 10);
        panel.Children.Add(help);
        var card = TBox(panel, padding: new Thickness(22, 16, 14, 16));
        card.Margin = new Thickness(0, 16, 0, 24);
        return card;
    }

    /// <summary>One lesson in Lessons this week: the book's color, its lesson, Done/Today/Planned, × to remove, and a drag handle.</summary>
    private Border LessonCard(CourseInfo c, string text, string sub, string state, DateTime day, int pos, bool editable, List<CourseInfo> books)
    {
        var g = new DockPanel();
        if (editable)
        {
            var x = new Button { Content = "×", FontSize = 13, Padding = new Thickness(4, 0, 4, 1), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Remove this lesson (this week only)" };
            x.SetResourceReference(StyleProperty, "LinkButton");
            x.Click += (_, _) =>
            {
                var list = books.Select(b => b.Id).ToList();
                if (pos < list.Count) list.RemoveAt(pos);
                Course.SetDay(day, list);
                RefreshTraining();
            };
            DockPanel.SetDock(x, Dock.Right);
            g.Children.Add(x);
        }
        var sp = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = BookBrush(c), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var t = TT(text, 12, bold: true); t.TextWrapping = TextWrapping.Wrap; t.MaxWidth = 120;
        row.Children.Add(t);
        sp.Children.Add(row);
        if (sub.Length > 0) { var s = TT(sub, 11, brush: "Muted", top: 2); s.MaxHeight = 30; s.TextTrimming = TextTrimming.CharacterEllipsis; sp.Children.Add(s); }
        var st = TT(state, 10.5, brush: state == "Done" ? "FocusBrush" : "Muted", top: 3);
        if (state == "Missed") st.Foreground = Amber;
        sp.Children.Add(st);
        g.Children.Add(sp);
        var card = new Border { Child = g, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 4, 7), Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = BookBrush(c) };
        card.SetResourceReference(Border.BackgroundProperty, "Panel2");
        if (!editable) return card;
        card.Cursor = Cursors.SizeAll;
        card.ToolTip = "Drag to another day";
        Point? down = null;
        card.PreviewMouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) down = e.GetPosition(card); };
        card.PreviewMouseLeftButtonUp += (_, _) => down = null;
        card.MouseMove += (_, e) =>
        {
            if (down is not Point p || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(card);
            if (Math.Abs(now.X - p.X) < 5 && Math.Abs(now.Y - p.Y) < 5) return;
            down = null;
            var data = new DataObject("focus-lesson", day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "|" + pos);
            card.Opacity = 0.5;
            try { DragDrop.DoDragDrop(card, data, DragDropEffects.Move); }
            finally { card.Opacity = 1; }
        };
        return card;
    }
}
