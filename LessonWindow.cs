using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>
/// A book-course lesson, full screen: Warm-up (unscored guesses), the Reading (whole, no timer, its own scroll bar,
/// your font and background), Review (due questions from earlier lessons), Practice (multiple choice, Sure or Guessing),
/// Memory cards (click to flip, then ✕ again / ✓✓ know it well / ✓ know it), the Chapter quiz (last lesson of a chapter), then Done.
/// Every answer has "Show where this is in the text" (the passage flashes), Hide and Flag.
///
/// Progress is saved after every step, on every device, so a lesson can be left and finished later (here or on another
/// computer). In required mode (the day's last time) it can't be closed until finished; quitting Focus is the only way out.
/// The same window runs exams, All-books practice rounds and the evening card review.
/// </summary>
public class LessonWindow : Window
{
    private sealed record Step(string Key, Action Show);

    private readonly Course.LessonRef _lesson;
    private readonly bool _required;
    private readonly Action<bool> _finished;
    private readonly List<Step> _steps = new();
    private int _step = -1;
    private bool _done;
    private int _right, _asked;

    private readonly Grid _root = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _page = new() { MaxWidth = 760, Margin = new Thickness(32, 28, 32, 40) };
    private readonly TextBlock _stepLabel = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _readingLayer = new() { Visibility = Visibility.Collapsed };
    private readonly Button _closeButton = new() { Padding = new Thickness(14, 5, 14, 5) };

    private readonly string? _examKey;
    private readonly List<(Course.LessonRef Lesson, PackQuestion Q)>? _questionsOnly;
    private readonly List<(Course.LessonRef Lesson, PackQuestion Q)>? _cardsOnly;
    private readonly TimeSpan _limit;
    private DateTime _deadline = DateTime.MaxValue;
    private readonly string _title;

    /// <summary>Progress is saved for lessons (not exams or practice rounds).</summary>
    private bool TrackProgress => _questionsOnly == null && _cardsOnly == null;
    private HashSet<string> _stepsDone = new();
    private Action<Key>? _keys;

    /// <summary>True when the lesson was left part-way (its place is saved).</summary>
    public bool LeftPartWay => TrackProgress && !_done && _stepsDone.Count > 0;

    /// <summary>An exam (examKey "unit1"... or "final") or an All-books practice round (examKey null): multiple choice only.</summary>
    public LessonWindow(string title, List<(Course.LessonRef Lesson, PackQuestion Q)> questions, string? examKey, Action<bool> finished, bool required = false)
        : this(questions.Count > 0 ? questions[0].Lesson : Course.Lessons().First(), required, finished, title, questions, examKey, null, TimeSpan.Zero) { }

    public LessonWindow(Course.LessonRef lesson, bool required, Action<bool> finished)
        : this(lesson, required, finished, null, null, null, null, TimeSpan.Zero) { }

    /// <summary>The evening card review: today's hardest memory cards for a few minutes.</summary>
    public static LessonWindow CardReview(List<(Course.LessonRef Lesson, PackQuestion Q)> cards, TimeSpan limit, Action<bool> finished) =>
        new(cards[0].Lesson, false, finished, "Evening card review", null, null, cards, limit);

    private LessonWindow(Course.LessonRef lesson, bool required, Action<bool> finished, string? title,
                         List<(Course.LessonRef Lesson, PackQuestion Q)>? questions, string? examKey,
                         List<(Course.LessonRef Lesson, PackQuestion Q)>? cards, TimeSpan limit)
    {
        _lesson = lesson;
        _questionsOnly = questions;
        _examKey = examKey;
        _cardsOnly = cards;
        _limit = limit;
        _title = title ?? lesson.Lesson.Title;
        _required = required;
        _finished = finished;
        Title = "Focus · " + _title;
        WindowState = cards != null ? WindowState.Normal : WindowState.Maximized;
        Width = cards != null ? 900 : 1100; Height = cards != null ? 760 : 800;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = required;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        FontSize = 16;

        var top = new DockPanel { Margin = new Thickness(24, 14, 24, 10) };
        _closeButton.IsEnabled = !required;
        _closeButton.Content = required ? "Required today" : TrackProgress ? "Finish later" : "Close";
        _closeButton.ToolTip = required ? "Today's last lesson time: finish the lesson to close it (or quit Focus, which moves it to tomorrow)."
                             : TrackProgress ? "Close and pick up right here next time (your place is saved after every step, on every device)" : "Close";
        _closeButton.Click += (_, _) => Close();
        DockPanel.SetDock(_closeButton, Dock.Right);
        top.Children.Add(_closeButton);
        var titleText = new TextBlock { Text = _title, FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        top.Children.Add(titleText);
        _stepLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        top.Children.Add(_stepLabel);

        _scroll.Content = _page;
        var main = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        main.Children.Add(top);
        main.Children.Add(_scroll);
        _root.Children.Add(main);
        _root.Children.Add(_readingLayer);
        Content = _root;

        BuildSteps();
        Closing += (_, e) =>
        {
            if (_required && !_done) { e.Cancel = true; return; }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (_keys == null || _readingLayer.Visibility == Visibility.Visible || e.OriginalSource is TextBox) return;
            _keys(e.Key);
        };
        Course.Changed += CourseChanged;
        Closed += (_, _) => { Course.Changed -= CourseChanged; _finished(_done); };
        Loaded += (_, _) => Next();
    }

    /// <summary>Another device finished this lesson: say so, and let this window close (even in required mode).</summary>
    private void CourseChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(CourseChanged)); return; }
        if (_done || !TrackProgress || !Course.DoneLessons().Contains(_lesson.Index)) return;
        _done = true;
        _keys = null;
        RestorePage();
        _page.Children.Clear();
        _readingLayer.Visibility = Visibility.Collapsed;
        SetStep("Done");
        _page.Children.Add(Text("Finished on another device", 28, bold: true));
        _page.Children.Add(Text("You finished this lesson on another computer, so there's nothing more to do here today.", 17, top: 10));
        _page.Children.Add(Primary("Close", Close));
        _closeButton.IsEnabled = true;
        _closeButton.Content = "Close";
    }

    // ---------- Steps ----------

    private void BuildSteps()
    {
        if (_cardsOnly != null)
        {
            _steps.Add(new("intro", () =>
            {
                SetStep("Start");
                _page.Children.Add(Text("Evening card review", 28, bold: true));
                _page.Children.Add(Text($"The hardest memory cards from today's lesson ({_cardsOnly.Count}), for about {Math.Max(1, (int)Math.Round(_limit.TotalMinutes))} minutes. " +
                                        "Flip each card, then ✕ (again), ✓✓ (know it well) or ✓ (know it).", 16, top: 10));
                _page.Children.Add(Primary("Begin", () => { _deadline = DateTime.Now + _limit; Next(); }));
            }));
            for (int i = 0; i < _cardsOnly.Count; i++)
            {
                var (cl, cq) = _cardsOnly[i]; int n = i;
                _steps.Add(new("card:" + cq.Id, () => ShowCard(cq, cl, $"Card {n + 1} of {_cardsOnly.Count}")));
            }
            _steps.Add(new("finish", Finish));
            return;
        }
        if (_questionsOnly != null)
        {
            _steps.Add(new("intro", () =>
            {
                SetStep("Start");
                _page.Children.Add(Text(_title, 28, bold: true));
                _page.Children.Add(Text(_examKey == null ? $"{_questionsOnly.Count} questions from the books you've finished. Each names its chapter."
                                                         : $"{_questionsOnly.Count} questions, mostly ones you haven't seen. Pass with 70%.", 16, top: 10));
                _page.Children.Add(Primary("Begin", Next));
            }));
            for (int i = 0; i < _questionsOnly.Count; i++)
            {
                var (ql, q) = _questionsOnly[i]; int n = i;
                _steps.Add(new("q:" + q.Id, () => ShowMc(q, ql, $"{(_examKey == null ? "Question" : "Exam question")} {n + 1} of {_questionsOnly.Count} · Chapter {ql.Pack.Chapter}", scored: true, key: null)));
            }
            _steps.Add(new("finish", Finish));
            return;
        }

        // A lesson: skip whatever was already finished (here or on another device).
        _stepsDone = Course.StepsDone(_lesson.Index);
        bool resuming = _stepsDone.Count > 0;
        bool readingDone = _stepsDone.Contains("reading");
        var l = _lesson.Lesson;
        var hidden = Course.Hidden();
        _steps.Add(new("intro", resuming ? WelcomeBack : Intro));
        if (!readingDone)
        {
            var warm = l.Warmup.Where(q => !hidden.Contains(q.Id) && !_stepsDone.Contains("warm:" + q.Id)).ToList();
            for (int i = 0; i < warm.Count; i++) { var q = warm[i]; int n = i; _steps.Add(new("warm:" + q.Id, () => ShowMc(q, _lesson, $"Warm-up {n + 1} of {warm.Count}", scored: false, key: "warm:" + q.Id))); }
            _steps.Add(new("reading", Reading));
        }
        var review = Course.DueReview(Course.Config.ReviewCap).Where(x => !_stepsDone.Contains("review:" + x.Q.Id)).ToList();
        for (int i = 0; i < review.Count; i++)
        {
            var (rl, rq) = review[i]; int n = i; string key = "review:" + rq.Id;
            _steps.Add(new(key, () => { if (rq.Kind == "card") ShowCard(rq, rl, $"Review {n + 1} of {review.Count}", key); else ShowMc(rq, rl, $"Review {n + 1} of {review.Count}", scored: true, key: key); }));
        }
        var practice = l.Practice.Where(q => !hidden.Contains(q.Id) && !_stepsDone.Contains("practice:" + q.Id)).ToList();
        for (int i = 0; i < practice.Count; i++) { var q = practice[i]; int n = i; _steps.Add(new("practice:" + q.Id, () => ShowMc(q, _lesson, $"Practice {n + 1} of {practice.Count}", scored: true, key: "practice:" + q.Id))); }
        var cards = l.Cards.Where(q => !hidden.Contains(q.Id) && !_stepsDone.Contains("card:" + q.Id)).ToList();
        for (int i = 0; i < cards.Count; i++) { var q = cards[i]; int n = i; _steps.Add(new("card:" + q.Id, () => ShowCard(q, _lesson, $"Memory card {n + 1} of {cards.Count}"))); }
        if (l.EndsChapter || l.Quiz.Count > 0)
        {
            var quiz = l.Quiz.Where(q => !hidden.Contains(q.Id) && !_stepsDone.Contains("quiz:" + q.Id)).ToList();
            for (int i = 0; i < quiz.Count; i++)
            {
                var q = quiz[i]; string label = $"Chapter quiz {i + 1} of {quiz.Count}"; string key = "quiz:" + q.Id;
                _steps.Add(new(key, () => { if (q.Kind == "order") ShowOrder(q, label, key); else ShowMc(q, _lesson, label, scored: true, key: key); }));
            }
        }
        _steps.Add(new("finish", Finish));
    }

    /// <summary>Saves that a step is finished, so the lesson picks up after it next time (on any device).</summary>
    private void MarkDone(string? key)
    {
        if (key == null || !TrackProgress || _done) return;
        if (_stepsDone.Add(key)) Course.MarkStep(_lesson.Index, key);
    }

    private void Next()
    {
        _step++;
        // The evening card review ends when its time is up (after the card on screen).
        if (_cardsOnly != null && DateTime.Now >= _deadline && _step < _steps.Count - 1) _step = _steps.Count - 1;
        if (_step >= _steps.Count) return;
        _keys = null;
        RestorePage();
        _page.Children.Clear();
        _scroll.ScrollToTop();
        _steps[_step].Show();
    }

    /// <summary>Puts the normal scrolling page back after the reading (which fills the window with its own scroll bar).</summary>
    private void RestorePage()
    {
        if (_scroll.Content != _page) _scroll.Content = _page;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    // ---------- Building blocks ----------

    private static TextBlock Text(string text, double size = 16, bool bold = false, string? brush = null, double top = 0)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, top, 0, 0) };
        ScriptureLinks.SetText(t, text);
        if (brush != null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static Button Primary(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 22, 10, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.SetResourceReference(StyleProperty, "PrimaryButton");
        b.Click += (_, _) => click();
        return b;
    }

    private static Button Link(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 18, 0) };
        b.SetResourceReference(StyleProperty, "LinkButton");
        b.Click += (_, _) => click();
        return b;
    }

    private void SetStep(string text) => _stepLabel.Text = text;

    /// <summary>"Show where this is in the text", Hide and Flag, under an answer.</summary>
    private WrapPanel AnswerLinks(PackQuestion q, Course.LessonRef lesson)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var r in q.Refs.Take(4))
        {
            var rr = r;
            row.Children.Add(Link(q.Refs.Count > 1 && r.Label != null ? $"Show where: {r.Label}" : "Show where this is in the text", () => ShowWhere(lesson, rr)));
        }
        Button? hide = null;
        hide = Link("Hide", () =>
        {
            Course.Add(new TrainingEvent { Kind = "hide", Question = q.Id, Concept = q.Concept });
            hide!.Content = "Hidden"; hide.IsEnabled = false;
        });
        hide.ToolTip = "Never show this question again (restore it from the course page)";
        row.Children.Add(hide);
        Button? flag = null;
        flag = Link("Flag", () =>
        {
            Course.Add(new TrainingEvent { Kind = "flag", Question = q.Id, Concept = q.Concept });
            flag!.Content = "Flagged"; flag.IsEnabled = false;
        });
        flag.ToolTip = "Mark this question as badly written, to fix in its chapter pack";
        row.Children.Add(flag);
        return row;
    }

    // ---------- Intro ----------

    private void Intro()
    {
        SetStep("Start");
        var l = _lesson.Lesson;
        _page.Children.Add(Text($"Chapter {_lesson.Pack.Chapter}: {_lesson.Pack.Title}", 14, brush: "Muted"));
        _page.Children.Add(Text(l.Title, 28, bold: true, top: 6));
        if (l.BigQuestions.Count > 0)
        {
            _page.Children.Add(Text("Questions to keep in mind", 15, bold: true, top: 24));
            foreach (var bq in l.BigQuestions) _page.Children.Add(Text("•  " + bq, 16, top: 8));
        }
        _page.Children.Add(Text("First a few quick guesses (not scored), then the reading, then practice and memory cards." +
                                (_required ? "" : " You can stop at any point with Finish later; the lesson picks up where you left off."), 14, brush: "Muted", top: 22));
        _page.Children.Add(Primary("Begin", Next));
    }

    /// <summary>Picking up a lesson started earlier (today, or on another device).</summary>
    private void WelcomeBack()
    {
        SetStep("Start");
        var l = _lesson.Lesson;
        int left = _steps.Count(s => s.Key is not ("intro" or "finish"));
        _page.Children.Add(Text($"Chapter {_lesson.Pack.Chapter}: {_lesson.Pack.Title}", 14, brush: "Muted"));
        _page.Children.Add(Text(l.Title, 28, bold: true, top: 6));
        _page.Children.Add(Text("Welcome back. This lesson picks up where you left off.", 17, top: 18));
        string next = _steps.Count > 1 ? _steps[1].Key : "finish";
        string where = next == "reading" ? "the reading" : next.StartsWith("warm:") ? "the warm-up" : next.StartsWith("review:") ? "review"
                     : next.StartsWith("practice:") ? "practice" : next.StartsWith("card:") ? "the memory cards" : next.StartsWith("quiz:") ? "the chapter quiz" : "the end";
        _page.Children.Add(Text(left == 0 ? "Everything's done; one click finishes it." : $"Next up: {where} ({left} step{(left == 1 ? "" : "s")} left).", 16, brush: "Muted", top: 8));
        if (_required) _page.Children.Add(Text("This is today's last lesson time, so the rest of the lesson is finished now.", 14, brush: "Muted", top: 8));
        var row = new WrapPanel();
        row.Children.Add(Primary("Continue", Next));
        if (_stepsDone.Contains("reading"))
        {
            var again = Link("Read the passage again", () =>
            {
                var (start, end) = Course.LessonRange(_lesson);
                OpenReadingLayer($"Chapter {_lesson.Pack.Chapter}: {_lesson.Pack.Title}", start, end, -1, null, "Back to the lesson");
            });
            again.Margin = new Thickness(6, 22, 0, 0);
            again.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(again);
        }
        _page.Children.Add(row);
    }

    // ---------- The reading ----------

    private void Reading()
    {
        SetStep("Reading");
        var (start, end) = Course.LessonRange(_lesson);
        if (start < 0)
        {
            _page.Children.Add(Text("Reading", 24, bold: true));
            _page.Children.Add(Text("Focus couldn't find this lesson in your book file. Read it in your book, then continue.", 16, top: 16));
            _page.Children.Add(Primary("I've finished reading", () => { MarkDone("reading"); Next(); }));
            return;
        }

        // The reading fills the window and scrolls on its own (scroll bar and mouse wheel).
        var grid = new Grid { MaxWidth = 900, Margin = new Thickness(28, 8, 28, 12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var mode = new CheckBox { Content = "Page view", IsChecked = Course.Config.PageView, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), ToolTip = "Turn pages like a book instead of scrolling" };
        tools.Children.Add(mode);
        var holder = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true };
        var map = new Dictionary<Paragraph, int>();
        void Build()
        {
            map.Clear();
            var viewer = BuildReading(start, end, Course.Config.PageView, map, -1, out _);
            AttachSelectionPopup(viewer, map, _lesson.Pack.Chapter);
            holder.Child = viewer;
            viewer.Loaded += (_, _) => viewer.Focus();
        }
        tools.Children.Add(AppearanceButton(Build));
        DockPanel.SetDock(tools, Dock.Right);
        head.Children.Add(tools);
        var titles = new StackPanel();
        titles.Children.Add(Text("Reading", 24, bold: true));
        string parts = _lesson.Lesson.Parts.Count > 0 ? "Parts: " + string.Join(" · ", _lesson.Lesson.Parts.Select(p => p.Title)) + ".  " : "";
        titles.Children.Add(Text(parts + "Select text to highlight it or add a note.", 13, brush: "Muted", top: 4));
        head.Children.Add(titles);
        Grid.SetRow(head, 0);
        grid.Children.Add(head);
        Grid.SetRow(holder, 1);
        grid.Children.Add(holder);
        var done = Primary("I've finished reading", () => { MarkDone("reading"); Next(); });
        done.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(done, 2);
        grid.Children.Add(done);

        Build();
        mode.Checked += (_, _) => { Course.Config.PageView = true; Course.SaveConfig(); Build(); };
        mode.Unchecked += (_, _) => { Course.Config.PageView = false; Course.SaveConfig(); Build(); };
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; // the reading itself scrolls
        _scroll.Content = grid;
    }

    private static readonly Dictionary<string, Color> HighlightColors = new()
    {
        ["yellow"] = Color.FromArgb(0x70, 0xF5, 0xD0, 0x4A), ["green"] = Color.FromArgb(0x60, 0x7C, 0xC8, 0x7A), ["blue"] = Color.FromArgb(0x60, 0x6A, 0xA8, 0xE8)
    };

    // ----- How the reading looks -----

    /// <summary>A reading background: its page color, text color and the color of shaded sidebars.</summary>
    public sealed record Look(string Key, string Name, Color Bg, Color Fg, Color Side);

    public static readonly Look[] Looks =
    {
        new("theme", "Focus theme", Colors.Transparent, Colors.Transparent, Colors.Transparent),
        new("paper", "Paper", Color.FromRgb(0xFB, 0xF7, 0xEE), Color.FromRgb(0x2B, 0x26, 0x20), Color.FromRgb(0xF0, 0xE8, 0xD6)),
        new("sepia", "Sepia", Color.FromRgb(0xF2, 0xE6, 0xCC), Color.FromRgb(0x4A, 0x3B, 0x2A), Color.FromRgb(0xE6, 0xD6, 0xB2)),
        new("white", "White", Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x1F, 0x1F, 0x1F), Color.FromRgb(0xF1, 0xF1, 0xF1)),
        new("sage", "Sage", Color.FromRgb(0xE6, 0xEE, 0xE2), Color.FromRgb(0x22, 0x30, 0x1F), Color.FromRgb(0xD7, 0xE3, 0xD0)),
        new("gray", "Gray", Color.FromRgb(0xE4, 0xE5, 0xE8), Color.FromRgb(0x20, 0x21, 0x24), Color.FromRgb(0xD6, 0xD8, 0xDC)),
        new("slate", "Slate", Color.FromRgb(0x2B, 0x30, 0x36), Color.FromRgb(0xDA, 0xDD, 0xE2), Color.FromRgb(0x36, 0x3C, 0x44)),
        new("night", "Night", Color.FromRgb(0x14, 0x14, 0x14), Color.FromRgb(0xCF, 0xC8, 0xBA), Color.FromRgb(0x22, 0x22, 0x22))
    };

    public static readonly string[] FavoriteFonts =
        { "Georgia", "Cambria", "Constantia", "Palatino Linotype", "Book Antiqua", "Garamond", "Sitka Text", "Charter", "Times New Roman", "Segoe UI", "Calibri", "Aptos", "Verdana" };

    private static Look CurrentLook => Looks.FirstOrDefault(l => l.Key == Course.Config.ReadingBg) ?? Looks[0];

    /// <summary>
    /// The book's paragraphs as a document, in your font and background, with headings, italics, shaded sidebars,
    /// pictures and your highlights. Page view turns pages like a book; otherwise it scrolls with its own scroll bar.
    /// <paramref name="mark"/> is a paragraph to bring into view (returned as <paramref name="target"/>).
    /// </summary>
    public static FrameworkElement BuildReading(int start, int end, bool pageView, Dictionary<Paragraph, int>? map, int mark, out Paragraph? target)
    {
        target = null;
        var cfg = Course.Config;
        var look = CurrentLook;
        bool themed = look.Key == "theme";
        double size = Math.Clamp(cfg.ReadingSize, 11, 34);
        var docu = new FlowDocument
        {
            PagePadding = new Thickness(40, 28, 40, 48), FontSize = size, LineHeight = Math.Round(size * 1.65),
            TextAlignment = TextAlignment.Left, ColumnWidth = 2000, IsOptimalParagraphEnabled = false
        };
        try { docu.FontFamily = new FontFamily((cfg.ReadingFont.Length > 0 ? cfg.ReadingFont + ", " : "") + "Georgia, Cambria, Segoe UI"); }
        catch { docu.FontFamily = new FontFamily("Georgia, Cambria, Segoe UI"); }
        if (themed) docu.SetResourceReference(TextElement.ForegroundProperty, "Text");
        else { docu.Foreground = new SolidColorBrush(look.Fg); docu.Background = new SolidColorBrush(look.Bg); }

        var highlights = Course.Highlights().Where(h => h.Para >= start && h.Para < end).ToLookup(h => h.Para);
        Section? sidebar = null;
        for (int i = start; i < end && i < Course.Book.Count; i++)
        {
            var bp = Course.Book[i];
            Block block;
            if (bp.Image != null)
            {
                var path = System.IO.Path.Combine(Course.MediaDir, bp.Image);
                if (!System.IO.File.Exists(path)) continue;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(path); bmp.EndInit();
                block = new BlockUIContainer(new Image { Source = bmp, MaxWidth = 700, MaxHeight = 520, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left }) { Margin = new Thickness(0, 8, 0, 14) };
            }
            else
            {
                var p = new Paragraph { Margin = new Thickness(0, bp.Heading > 0 ? 18 : 0, 0, 12) };
                if (bp.Heading > 0)
                {
                    p.FontSize = size + (bp.Heading == 1 ? 9 : bp.Heading == 2 ? 4 : 1);
                    p.LineHeight = Math.Round(p.FontSize * 1.3);
                    p.FontWeight = FontWeights.SemiBold;
                    p.FontFamily = new FontFamily("Segoe UI");
                }
                foreach (var r in bp.Runs)
                {
                    Inline run = new Run(r.Text);
                    if (r.Super) { run.BaselineAlignment = BaselineAlignment.Superscript; run.FontSize = Math.Round(size * 0.65); }
                    if (r.Italic) run = new Italic(run);
                    if (r.Bold) run = new Bold(run);
                    p.Inlines.Add(run);
                }
                if (i == mark) target = p;
                foreach (var h in highlights[i]) ApplyHighlight(p, h.Quote, h.Color, h.Note);
                map?.Add(p, i);
                block = p;
            }
            if (bp.Sidebar)
            {
                if (sidebar == null)
                {
                    sidebar = new Section { Padding = new Thickness(16, 12, 16, 4), Margin = new Thickness(0, 6, 0, 16), BorderThickness = new Thickness(3, 0, 0, 0) };
                    if (themed) sidebar.SetResourceReference(TextElement.BackgroundProperty, "Panel2");
                    else sidebar.Background = new SolidColorBrush(look.Side);
                    sidebar.SetResourceReference(Block.BorderBrushProperty, "FocusBrush");
                    docu.Blocks.Add(sidebar);
                }
                if (block is Paragraph sp && bp.Heading == 0) sp.FontSize = Math.Max(11, size - 1);
                sidebar.Blocks.Add(block);
            }
            else { sidebar = null; docu.Blocks.Add(block); }
        }
        FrameworkElement viewer = pageView
            ? KindlePages(new FlowDocumentPageViewer { Document = docu })
            : new FlowDocumentScrollViewer { Document = docu, VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsToolBarVisible = false, IsSelectionEnabled = true };
        if (viewer is Control c)
        {
            if (themed) c.SetResourceReference(Control.BackgroundProperty, "Panel");
            else c.Background = new SolidColorBrush(look.Bg);
        }
        return viewer;
    }

    /// <summary>
    /// Page view like a Kindle: click or tap the left side of the page for the previous page, the right side for the next
    /// (the middle is for reading and selecting text). The arrow keys, Page Up/Down and the mouse wheel turn pages too.
    /// </summary>
    private static FlowDocumentPageViewer KindlePages(FlowDocumentPageViewer pages)
    {
        const double edge = 0.25; // the outer quarter on each side turns the page
        Point down = default;
        bool pressed = false;
        pages.ToolTip = "Click or tap the left or right side of the page to turn it (or use the arrow keys)";
        pages.PreviewMouseLeftButtonDown += (_, e) => { down = e.GetPosition(pages); pressed = true; };
        pages.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            var up = e.GetPosition(pages);
            if (Math.Abs(up.X - down.X) > 6 || Math.Abs(up.Y - down.Y) > 6) return;   // a drag selects text instead
            if (pages.Selection != null && !pages.Selection.IsEmpty) return;
            if (up.Y > pages.ActualHeight - 48) return;                               // the page bar at the bottom
            double w = Math.Max(1, pages.ActualWidth);
            if (up.X < w * edge) { if (pages.CanGoToPreviousPage) pages.PreviousPage(); e.Handled = true; }
            else if (up.X > w * (1 - edge)) { if (pages.CanGoToNextPage) pages.NextPage(); e.Handled = true; }
        };
        pages.PreviewMouseMove += (_, e) =>
        {
            var at = e.GetPosition(pages);
            double w = Math.Max(1, pages.ActualWidth);
            bool side = at.Y < pages.ActualHeight - 48 && (at.X < w * edge || at.X > w * (1 - edge));
            pages.Cursor = side ? Cursors.Hand : null;
        };
        pages.PreviewKeyDown += (_, e) =>
        {
            if ((e.Key is Key.Right or Key.Space) && pages.CanGoToNextPage) { pages.NextPage(); e.Handled = true; }
            else if (e.Key == Key.Left && pages.CanGoToPreviousPage) { pages.PreviousPage(); e.Handled = true; }
        };
        pages.PreviewMouseWheel += (_, e) =>
        {
            if (e.Delta < 0 && pages.CanGoToNextPage) pages.NextPage();
            else if (e.Delta > 0 && pages.CanGoToPreviousPage) pages.PreviousPage();
            e.Handled = true;
        };
        return pages;
    }

    /// <summary>The "Aa" button above the reading: font, size and background, changed on the spot.</summary>
    private Button AppearanceButton(Action rebuild)
    {
        var button = new Button { Content = "Aa", FontFamily = new FontFamily("Georgia"), FontSize = 16, Padding = new Thickness(12, 3, 12, 5), ToolTip = "Font, size and background of the reading" };
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 14, 18, 16), Margin = new Thickness(8), BorderThickness = new Thickness(1), Width = 330,
                                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.3 } };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.SetResourceReference(TextElement.ForegroundProperty, "Text");
        card.Child = AppearancePanel(rebuild);
        popup.Child = card;
        button.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
        return button;
    }

    /// <summary>Font, size and background controls (also on the Training settings page). <paramref name="changed"/> runs after each change.</summary>
    public static FrameworkElement AppearancePanel(Action changed)
    {
        var cfg = Course.Config;
        var panel = new StackPanel();
        TextBlock Label(string t, double top) { var l = new TextBlock { Text = t, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 6) }; return l; }

        // Font: your favorites first, then every font on this PC.
        panel.Children.Add(Label("Font", 0));
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).Where(n => !n.Contains('#')).Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        var favorites = FavoriteFonts.Where(f => installed.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        var fonts = new ComboBox { MaxDropDownHeight = 360, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        ComboBoxItem Item(string name) => new() { Content = name, Tag = name, FontFamily = new FontFamily(name), FontSize = 14 };
        foreach (var f in favorites) fonts.Items.Add(Item(f));
        if (!favorites.Contains(cfg.ReadingFont, StringComparer.OrdinalIgnoreCase) && cfg.ReadingFont.Length > 0) fonts.Items.Insert(0, Item(cfg.ReadingFont));
        fonts.Items.Add(new Separator());
        foreach (var f in installed.Where(f => !favorites.Contains(f, StringComparer.OrdinalIgnoreCase))) fonts.Items.Add(Item(f));
        fonts.SelectedItem = fonts.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, cfg.ReadingFont, StringComparison.OrdinalIgnoreCase));
        fonts.SelectionChanged += (_, _) =>
        {
            if (fonts.SelectedItem is not ComboBoxItem it || it.Tag is not string name || name == cfg.ReadingFont) return;
            cfg.ReadingFont = name;
            Course.SaveConfig();
            changed();
        };
        panel.Children.Add(fonts);

        // Size
        panel.Children.Add(Label("Size", 14));
        var sizeRow = new DockPanel();
        var sizeText = new TextBlock { Text = $"{cfg.ReadingSize:0}", Width = 30, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(sizeText, Dock.Right);
        sizeRow.Children.Add(sizeText);
        var slider = new Slider { Minimum = 12, Maximum = 30, Value = Math.Clamp(cfg.ReadingSize, 12, 30), TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 2, VerticalAlignment = VerticalAlignment.Center };
        sizeRow.Children.Add(slider);
        var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            if (Math.Abs(cfg.ReadingSize - slider.Value) < 0.1) return;
            cfg.ReadingSize = slider.Value;
            Course.SaveConfig();
            changed();
        };
        slider.ValueChanged += (_, _) => { sizeText.Text = $"{slider.Value:0}"; settle.Stop(); settle.Start(); };
        panel.Children.Add(sizeRow);

        // Background
        panel.Children.Add(Label("Background", 14));
        var swatches = new WrapPanel();
        var rings = new List<(Border Ring, string Key)>();
        foreach (var look in Looks)
        {
            var dot = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1) };
            if (look.Key == "theme") { dot.SetResourceReference(Border.BackgroundProperty, "Bg"); dot.Child = new TextBlock { Text = "F", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
            else { dot.Background = new SolidColorBrush(look.Bg); dot.Child = new TextBlock { Text = "A", FontFamily = new FontFamily("Georgia"), FontSize = 13, Foreground = new SolidColorBrush(look.Fg), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
            dot.BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80));
            var ring = new Border { Padding = new Thickness(2), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(2), Child = dot, Cursor = Cursors.Hand, ToolTip = look.Name };
            string key = look.Key;
            ring.MouseLeftButtonUp += (_, _) =>
            {
                if (cfg.ReadingBg == key) return;
                cfg.ReadingBg = key;
                Course.SaveConfig();
                foreach (var (r, k) in rings) r.SetResourceReference(Border.BorderBrushProperty, k == key ? "FocusBrush" : "Transparent");
                changed();
            };
            ring.SetResourceReference(Border.BorderBrushProperty, look.Key == cfg.ReadingBg ? "FocusBrush" : "Transparent");
            rings.Add((ring, look.Key));
            swatches.Children.Add(ring);
        }
        panel.Children.Add(swatches);
        var hint = new TextBlock { Text = "F follows Focus's light or dark theme.", FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(hint);
        return panel;
    }

    // ----- Highlights and notes: a small pop-up next to selected text -----

    /// <summary>Colors the first place the quote appears in the paragraph.</summary>
    public static void ApplyHighlight(Paragraph p, string quote, string color, string note)
    {
        try
        {
            var range = FindText(p, quote, sentence: false, exact: true);
            range?.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(HighlightColors.TryGetValue(color, out var c) ? c : HighlightColors["yellow"]));
        }
        catch (Exception ex) { Log.Error("ApplyHighlight", ex); }
    }

    private static TextSelection? SelectionOf(FrameworkElement? viewer) => viewer switch
    {
        FlowDocumentScrollViewer v => v.Selection,
        FlowDocumentPageViewer v => v.Selection,
        _ => null
    };

    /// <summary>When you select text in the reading, a pop-up offers highlight colors and a note (and Remove, on a highlight).</summary>
    private void AttachSelectionPopup(FrameworkElement viewer, Dictionary<Paragraph, int> map, int chapter)
    {
        var popup = new Popup { Placement = PlacementMode.MousePoint, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, HorizontalOffset = 8, VerticalOffset = 14 };
        viewer.PreviewMouseLeftButtonUp += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => OfferFor(viewer, map, chapter, popup)), DispatcherPriority.Input);
    }

    private void OfferFor(FrameworkElement viewer, Dictionary<Paragraph, int> map, int chapter, Popup popup)
    {
        var sel = SelectionOf(viewer);
        if (sel == null || sel.IsEmpty) return;
        var para = sel.Start.Paragraph;
        if (para == null || !map.TryGetValue(para, out int index)) return;
        var endPtr = sel.End.CompareTo(para.ContentEnd) > 0 ? para.ContentEnd : sel.End;
        string quote = new TextRange(sel.Start, endPtr).Text.Trim();
        if (quote.Length == 0) return;
        var existing = Course.Highlights().FirstOrDefault(h => h.Para == index && h.Chapter == chapter && (h.Quote.Contains(quote, StringComparison.Ordinal) || quote.Contains(h.Quote, StringComparison.Ordinal)));

        string color = existing?.Color is { Length: > 0 } ec ? ec : "yellow";
        var body = new StackPanel();
        var swatchRow = new StackPanel { Orientation = Orientation.Horizontal };
        var noteArea = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        var noteBox = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 220, Width = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = existing?.Note ?? "" };
        var rings = new List<(Border Ring, string Name)>();

        void Save(string c, string note)
        {
            if (existing != null)
            {
                // Changing an existing highlight: replace it with the new color or note.
                Course.Add(new TrainingEvent { Kind = "unhighlight", Question = existing.Id });
                ClearHighlight(para, existing.Quote);
            }
            string q = existing != null && existing.Quote.Contains(quote, StringComparison.Ordinal) ? existing.Quote : quote;
            Course.AddHighlight(chapter, index, q, c, note);
            ApplyHighlight(para, q, c, note);
            sel.Select(sel.End, sel.End);
            popup.IsOpen = false;
        }

        foreach (var name in new[] { "yellow", "green", "blue" })
        {
            var dot = new Ellipse { Width = 24, Height = 24, Fill = new SolidColorBrush(Color.FromRgb(HighlightColors[name].R, HighlightColors[name].G, HighlightColors[name].B)) };
            var ring = new Border { Child = dot, Padding = new Thickness(2), CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand,
                                    ToolTip = noteArea.Visibility == Visibility.Visible ? "Color for this note" : $"Highlight {name}" };
            ring.SetResourceReference(Border.BorderBrushProperty, existing != null && name == color ? "FocusBrush" : "Transparent");
            string n = name;
            ring.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                color = n;
                if (noteArea.Visibility == Visibility.Visible) { foreach (var (r, rn) in rings) r.SetResourceReference(Border.BorderBrushProperty, rn == n ? "FocusBrush" : "Transparent"); return; }
                Save(n, existing?.Note ?? "");
            };
            rings.Add((ring, name));
            swatchRow.Children.Add(ring);
        }
        var noteBtn = new Button { Content = existing?.Note.Length > 0 ? "✎  Edit note" : "✎  Note", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center };
        noteBtn.Click += (_, _) =>
        {
            noteArea.Visibility = Visibility.Visible;
            noteBtn.Visibility = Visibility.Collapsed;
            foreach (var (r, rn) in rings) r.SetResourceReference(Border.BorderBrushProperty, rn == color ? "FocusBrush" : "Transparent");
            noteBox.Focus();
        };
        swatchRow.Children.Add(noteBtn);
        if (existing != null)
        {
            var remove = new Button { Content = "Remove", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Remove this highlight (and its note)" };
            remove.Click += (_, _) =>
            {
                Course.Add(new TrainingEvent { Kind = "unhighlight", Question = existing.Id });
                ClearHighlight(para, existing.Quote);
                popup.IsOpen = false;
            };
            swatchRow.Children.Add(remove);
        }
        body.Children.Add(swatchRow);

        var quoteText = new TextBlock { Text = "“" + (quote.Length > 110 ? quote[..110] + "…" : quote) + "”", TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic, FontSize = 12.5, Width = 300, Margin = new Thickness(0, 0, 0, 6) };
        quoteText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        noteArea.Children.Add(quoteText);
        noteArea.Children.Add(noteBox);
        var noteButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => popup.IsOpen = false;
        var saveNote = new Button { Content = "Save note", Padding = new Thickness(12, 4, 12, 4) };
        saveNote.SetResourceReference(StyleProperty, "PrimaryButton");
        saveNote.Click += (_, _) => Save(color, noteBox.Text.Trim());
        noteButtons.Children.Add(cancel);
        noteButtons.Children.Add(saveNote);
        noteArea.Children.Add(noteButtons);
        noteBox.KeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Save(color, noteBox.Text.Trim()); } };
        body.Children.Add(noteArea);

        var card = new Border { Child = body, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(8), BorderThickness = new Thickness(1),
                                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.3 } };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.SetResourceReference(TextElement.ForegroundProperty, "Text");
        popup.Child = card;
        popup.IsOpen = true;
    }

    private static void ClearHighlight(Paragraph p, string quote)
    {
        try { FindText(p, quote, sentence: false, exact: true)?.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent); }
        catch (Exception ex) { Log.Error("ClearHighlight", ex); }
    }

    /// <summary>
    /// The place in a paragraph where the text appears: exactly, or compared loosely (quotes, dashes, spaces, capitals);
    /// optionally widened to the whole sentence around it. Null if it isn't there.
    /// </summary>
    public static TextRange? FindText(Paragraph p, string text, bool sentence, bool exact = false)
    {
        var raw = new StringBuilder();
        var positions = new List<TextPointer>();
        for (var tp = p.ContentStart; tp != null && tp.CompareTo(p.ContentEnd) < 0; tp = tp.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (tp.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
            string run = tp.GetTextInRun(LogicalDirection.Forward);
            for (int k = 0; k < run.Length; k++) { raw.Append(run[k]); positions.Add(tp.GetPositionAtOffset(k)!); }
        }
        string want = text.Trim();
        if (want.Length == 0 || raw.Length == 0) return null;
        int s, e;
        int at = raw.ToString().IndexOf(want, StringComparison.Ordinal);
        if (at >= 0) { s = at; e = at + want.Length; }
        else if (exact) return null;
        else
        {
            // Loose match: the same rules as finding passages in the book (BookReader.Normalize).
            var norm = new StringBuilder();
            var back = new List<int>();
            bool space = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = char.ToLowerInvariant(raw[i]) switch
                {
                    '‘' or '’' or '‛' or '′' => '\'',
                    '“' or '”' or '„' or '″' => '"',
                    '–' or '—' or '‑' or '‒' => '-',
                    '\u00AD' => '\0',
                    var other => other
                };
                if (c == '\0') continue;
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && norm.Length > 0) { norm.Append(' '); back.Add(i); }
                space = false;
                norm.Append(c); back.Add(i);
            }
            string w = BookReader.Normalize(want);
            int n = norm.ToString().IndexOf(w, StringComparison.Ordinal);
            if (n < 0 || w.Length == 0) return null;
            s = back[n]; e = back[n + w.Length - 1] + 1;
        }
        if (sentence)
        {
            static bool Ender(char c) => c is '.' or '?' or '!';
            static bool Closer(char c) => c is '"' or '”' or '’' or '\'' or ')' or ']';
            // Back to the start of the sentence.
            while (s > 0)
            {
                int k = s - 1;
                if (char.IsWhiteSpace(raw[k]))
                {
                    int j = k - 1;
                    while (j >= 0 && Closer(raw[j])) j--;
                    if (j >= 0 && Ender(raw[j])) break;
                }
                s--;
            }
            while (s < raw.Length && char.IsWhiteSpace(raw[s])) s++;
            // On to its end (with any closing quote or bracket).
            while (e < raw.Length && !(Ender(raw[e - 1]) && char.IsWhiteSpace(raw[e]))) { if (Closer(raw[e]) && e > 0 && Ender(raw[e - 1])) { e++; break; } e++; }
            while (e < raw.Length && Closer(raw[e])) e++;
        }
        if (s >= positions.Count) return null;
        var endPtr = e < positions.Count ? positions[e] : p.ContentEnd;
        return new TextRange(positions[s], endPtr);
    }

    /// <summary>Flashes a passage a few times, then leaves it softly marked, so the eye lands on it.</summary>
    public static void Flash(TextRange range)
    {
        var on = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xC8, 0x3A));
        var off = Brushes.Transparent;
        var rest = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xC8, 0x3A));
        int ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        timer.Tick += (_, _) =>
        {
            ticks++;
            try { range.ApplyPropertyValue(TextElement.BackgroundProperty, ticks >= 8 ? rest : ticks % 2 == 1 ? on : off); }
            catch (Exception ex) { Log.Error("Flash", ex); timer.Stop(); return; }
            if (ticks >= 8) timer.Stop();
        };
        range.ApplyPropertyValue(TextElement.BackgroundProperty, on);
        timer.Start();
    }

    /// <summary>A window showing part of the book, scrolled to one paragraph, with the quote (if given) flashing (from the notes list).</summary>
    public static void OpenPassage(Window? owner, int start, int end, int para, string title, string? quote = null)
    {
        var viewer = BuildReading(start, end, false, null, para, out var target);
        var holder = new Border { Child = viewer, MaxWidth = 900, Margin = new Thickness(24, 16, 24, 20), CornerRadius = new CornerRadius(10), ClipToBounds = true };
        var w = new Window { Title = title, Width = 980, Height = 800, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = holder };
        w.SetResourceReference(BackgroundProperty, "Bg");
        w.SetResourceReference(ForegroundProperty, "Text");
        if (target != null)
            w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(new Action(() =>
            {
                target.BringIntoView();
                var range = quote != null ? FindText(target, quote, sentence: false) : null;
                Flash(range ?? new TextRange(target.ContentStart, target.ContentEnd));
            }), DispatcherPriority.ContextIdle);
        w.Show();
    }

    /// <summary>Opens the lesson's reading over the question, scrolled to the answer, which flashes a few times.</summary>
    private void ShowWhere(Course.LessonRef lesson, PackRef r)
    {
        int at = Course.Locate(lesson, r);
        var (start, end) = Course.LessonRange(lesson);
        if (at >= 0 && (start < 0 || at < start || at >= end))
        {
            var (cs, ce) = Course.ChapterRange(lesson.Pack); // the passage is elsewhere in the chapter
            start = cs; end = ce;
        }
        string caption = at < 0 ? "That passage wasn't found in your book file." : $"Chapter {lesson.Pack.Chapter}: {lesson.Pack.Title}";
        OpenReadingLayer(caption, start, end, at, r.Find, "Back to the question");
    }

    /// <summary>The reading on top of the lesson (Show where, or reading again), with its own scroll bar.</summary>
    private void OpenReadingLayer(string caption, int start, int end, int at, string? find, string backText)
    {
        var keys = _keys;
        _keys = null;
        _readingLayer.Children.Clear();
        var panel = new DockPanel();
        panel.SetResourceReference(Panel.BackgroundProperty, "Bg");
        var bar = new DockPanel { Margin = new Thickness(24, 14, 24, 10) };
        var back = new Button { Content = backText, Padding = new Thickness(16, 6, 16, 6) };
        back.SetResourceReference(StyleProperty, "PrimaryButton");
        back.Click += (_, _) => { _readingLayer.Visibility = Visibility.Collapsed; _readingLayer.Children.Clear(); _keys = keys; };
        DockPanel.SetDock(back, Dock.Right);
        bar.Children.Add(back);
        bar.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        if (start >= 0)
        {
            var map = new Dictionary<Paragraph, int>();
            var viewer = BuildReading(start, end, false, map, at, out var target);
            AttachSelectionPopup(viewer, map, _lesson.Pack.Chapter);
            var holder = new Border { Child = viewer, MaxWidth = 900, Margin = new Thickness(28, 4, 28, 20), CornerRadius = new CornerRadius(10), ClipToBounds = true };
            panel.Children.Add(holder);
            if (target != null)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    target.BringIntoView();
                    var range = find != null ? FindText(target, find, sentence: true) : null;
                    Flash(range ?? new TextRange(target.ContentStart, target.ContentEnd));
                }), DispatcherPriority.ContextIdle);
        }
        _readingLayer.Children.Add(panel);
        _readingLayer.Visibility = Visibility.Visible;
    }

    // ---------- Multiple choice ----------

    private void ShowMc(PackQuestion q, Course.LessonRef lesson, string label, bool scored, string? key)
    {
        SetStep(label);
        _page.Children.Add(Text(label, 13, brush: "Muted"));
        _page.Children.Add(Text(q.Prompt, 21, bold: true, top: 8));
        int chosen = -1;
        bool answered = false;
        var options = new List<Border>();
        var choices = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var sureRow = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        var feedback = new StackPanel();

        void Mark(int i)
        {
            if (answered) return;
            chosen = i;
            for (int k = 0; k < options.Count; k++) options[k].SetResourceReference(Border.BorderBrushProperty, k == i ? "FocusBrush" : "Line");
            sureRow.Visibility = scored ? Visibility.Visible : Visibility.Collapsed;
            if (!scored) Answer(false);
        }

        void Answer(bool sure)
        {
            if (answered || chosen < 0) return;
            answered = true;
            sureRow.Visibility = Visibility.Collapsed;
            bool right = chosen == q.Answer;
            for (int k = 0; k < options.Count; k++)
            {
                if (k == q.Answer) { options[k].SetResourceReference(Border.BorderBrushProperty, "FocusBrush"); options[k].BorderThickness = new Thickness(3); }
                else if (k == chosen) { options[k].SetResourceReference(Border.BorderBrushProperty, "PriorityHigh"); options[k].BorderThickness = new Thickness(3); }
            }
            if (scored)
            {
                _asked++; if (right && sure) _right++;
                Course.Add(new TrainingEvent { Kind = "answer", Question = q.Id, Concept = q.Concept, Correct = right, Sure = sure });
                string verdict = right ? (sure ? "Right." : "Right, but you were guessing, so it comes back soon.")
                                       : (sure ? "Not quite, and you were sure: this one comes back first." : "Not quite.");
                feedback.Children.Add(Text((right ? "✓ " : "✗ ") + verdict, 17, bold: true, brush: right ? "FocusBrush" : "PriorityHigh", top: 16));
            }
            else feedback.Children.Add(Text($"The answer: {q.Options[q.Answer]}. Watch for it in the reading.", 16, bold: true, top: 16));
            MarkDone(key);
            if (q.Why.Length > 0) feedback.Children.Add(Text(q.Why, 16, top: 8));
            if (scored) feedback.Children.Add(AnswerLinks(q, lesson));
            feedback.Children.Add(Primary("Next", Next));
        }

        for (int i = 0; i < q.Options.Count; i++)
        {
            int idx = i;
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 17 };
            ScriptureLinks.SetText(t, $"{(char)('A' + i)}.  {q.Options[i]}");
            var b = new Border { Child = t, Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), Cursor = Cursors.Hand };
            b.SetResourceReference(Border.BackgroundProperty, "Panel");
            b.SetResourceReference(Border.BorderBrushProperty, "Line");
            b.MouseLeftButtonUp += (_, _) => Mark(idx);
            b.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && scored) { Mark(idx); Answer(true); } }; // double-click: I'm sure
            options.Add(b);
            choices.Children.Add(b);
        }
        var sure = new Button { Content = "Sure", Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 0, 10, 0), ToolTip = "Double-clicking an answer does the same" };
        sure.SetResourceReference(StyleProperty, "PrimaryButton");
        sure.Click += (_, _) => Answer(true);
        var guess = new Button { Content = "Guessing", Padding = new Thickness(22, 8, 22, 8) };
        guess.Click += (_, _) => Answer(false);
        sureRow.Children.Add(sure);
        sureRow.Children.Add(guess);

        _page.Children.Add(choices);
        _page.Children.Add(sureRow);
        _page.Children.Add(feedback);
        if (scored) _page.Children.Add(Text("Pick an answer, then Sure or Guessing (or double-click the answer if you're sure).", 13, brush: "Muted", top: 12));
    }

    // ---------- Memory cards ----------

    private static readonly Color Red = Color.FromRgb(0xF4, 0x43, 0x36);
    private static readonly Color Green = Color.FromRgb(0x4C, 0xAF, 0x50);

    /// <summary>A round grading button like the BMA app's: a colored circle with a soft glow and a white mark.</summary>
    private static FrameworkElement GradeButton(Color color, string geometry, string label, string tip, Action click)
    {
        var mark = new Path { Data = Geometry.Parse(geometry), Stroke = Brushes.White, StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                              StrokeLineJoin = PenLineJoin.Round, Width = 26, Height = 20, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var circle = new Border { Width = 66, Height = 66, CornerRadius = new CornerRadius(33), Background = new SolidColorBrush(color), Child = mark,
                                  Effect = new DropShadowEffect { Color = color, BlurRadius = 18, ShadowDepth = 3, Opacity = 0.55 } };
        var text = new TextBlock { Text = label, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var stack = new StackPanel { Margin = new Thickness(22, 0, 22, 0), Cursor = Cursors.Hand, ToolTip = tip, Background = Brushes.Transparent };
        stack.Children.Add(circle);
        stack.Children.Add(text);
        stack.MouseEnter += (_, _) => circle.Opacity = 0.88;
        stack.MouseLeave += (_, _) => circle.Opacity = 1;
        stack.MouseLeftButtonUp += (_, _) => click();
        return stack;
    }

    private const string CrossGeometry = "M 6,3 L 20,17 M 20,3 L 6,17";
    private const string CheckGeometry = "M 5,10 L 11,16 L 21,4";
    private const string DoubleCheckGeometry = "M 1,11 L 6,16 L 15,5 M 10,15 L 11,16 L 20,5";

    /// <summary>
    /// A memory card: click (or Space) to flip, then grade it. ✕ brings it back later in this lesson, ✓✓ (know it well)
    /// keeps it away for a long time, ✓ (know it fairly well) brings it back after a while. Keys 1, 2, 3 grade too.
    /// </summary>
    private void ShowCard(PackQuestion q, Course.LessonRef lesson, string label, string? key = null)
    {
        key ??= "card:" + q.Id;
        string stepKey = key;
        SetStep(_cardsOnly != null && _deadline != DateTime.MaxValue ? $"{label} · {Math.Max(0, (int)Math.Ceiling((_deadline - DateTime.Now).TotalMinutes))} min left" : label);
        _page.Children.Add(Text(label, 13, brush: "Muted"));
        bool flipped = false;
        var face = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 22, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ScriptureLinks.SetText(face, q.Front);
        var side = new TextBlock { Text = "Question · click to flip", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        side.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var inner = new DockPanel();
        DockPanel.SetDock(side, Dock.Top);
        inner.Children.Add(side);
        inner.Children.Add(face);
        var card = new Border { Child = inner, MinHeight = 240, Padding = new Thickness(36, 22, 36, 30), Margin = new Thickness(0, 12, 0, 0), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2), Cursor = Cursors.Hand };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        var grades = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0), Visibility = Visibility.Hidden };
        var links = AnswerLinks(q, lesson);
        links.Visibility = Visibility.Collapsed;
        links.HorizontalAlignment = HorizontalAlignment.Center;
        bool answered = false;

        void Flip()
        {
            flipped = !flipped;
            ScriptureLinks.SetText(face, flipped ? q.Back : q.Front);
            side.Text = flipped ? "Answer · click to flip back" : "Question · click to flip";
            if (flipped && !answered) { grades.Visibility = Visibility.Visible; links.Visibility = Visibility.Visible; }
        }
        card.MouseLeftButtonUp += (_, _) => Flip();

        void Grade(int g)
        {
            if (answered || !flipped) return;
            answered = true;
            _asked++; if (g > 0) _right++;
            Course.Add(new TrainingEvent { Kind = "answer", Question = q.Id, Concept = q.Concept, Correct = g > 0, Sure = g > 0, Grade = g });
            if (g == 0)
                _steps.Insert(RequeueIndex(), new Step(stepKey, () => ShowCard(q, lesson, _cardsOnly != null ? "Card (again)" : "Memory card (again)", stepKey)));
            else MarkDone(stepKey);
            Next();
        }
        grades.Children.Add(GradeButton(Red, CrossGeometry, "Again", "Don't know it yet: it comes back later in this lesson (key 1)", () => Grade(0)));
        grades.Children.Add(GradeButton(Green, DoubleCheckGeometry, "Know it well", "Know it really well: it won't come back for a long time (key 2)", () => Grade(2)));
        grades.Children.Add(GradeButton(Green, CheckGeometry, "Know it", "Know it fairly well: it comes back after a while (key 3)", () => Grade(1)));
        _keys = k =>
        {
            if (k == Key.Space || k == Key.Enter) Flip();
            else if (k is Key.D1 or Key.NumPad1) Grade(0);
            else if (k is Key.D2 or Key.NumPad2) Grade(2);
            else if (k is Key.D3 or Key.NumPad3) Grade(1);
        };
        _page.Children.Add(card);
        _page.Children.Add(grades);
        _page.Children.Add(links);
        var help = Text("Click the card (or press Space) to flip it. ✕ again in this lesson · ✓✓ know it well · ✓ know it.", 13, brush: "Muted", top: 18);
        help.TextAlignment = TextAlignment.Center;
        _page.Children.Add(help);
    }

    /// <summary>Where a card marked ✕ goes: after the other cards, before the chapter quiz and the end.</summary>
    private int RequeueIndex()
    {
        for (int i = _step + 1; i < _steps.Count; i++)
            if (_steps[i].Key == "finish" || _steps[i].Key.StartsWith("quiz:", StringComparison.Ordinal)) return i;
        return _steps.Count;
    }

    // ---------- Putting things in order ----------

    private void ShowOrder(PackQuestion q, string label, string key)
    {
        SetStep(label);
        _page.Children.Add(Text(label, 13, brush: "Muted"));
        _page.Children.Add(Text(q.Prompt, 21, bold: true, top: 8));
        var picked = new List<int>();
        var pool = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        var chosenList = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var feedback = new StackPanel();
        var shuffled = Enumerable.Range(0, q.Items.Count).OrderBy(_ => Guid.NewGuid()).ToList();
        var check = new Button { Content = "Check", Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 18, 10, 0), IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left };
        check.SetResourceReference(StyleProperty, "PrimaryButton");
        void Redraw()
        {
            pool.Children.Clear();
            foreach (int i in shuffled.Where(i => !picked.Contains(i)))
            {
                int idx = i;
                var b = new Button { Content = new TextBlock { Text = q.Items[i], TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 8) };
                b.Click += (_, _) => { picked.Add(idx); Redraw(); };
                pool.Children.Add(b);
            }
            chosenList.Children.Clear();
            for (int n = 0; n < picked.Count; n++)
                chosenList.Children.Add(Text($"{n + 1}.  {q.Items[picked[n]]}", 17, top: 4));
            check.IsEnabled = picked.Count == q.Items.Count;
        }
        check.Click += (_, _) =>
        {
            check.IsEnabled = false;
            pool.Visibility = Visibility.Collapsed;
            bool right = picked.SequenceEqual(Enumerable.Range(0, q.Items.Count));
            _asked++; if (right) _right++;
            Course.Add(new TrainingEvent { Kind = "answer", Question = q.Id, Concept = q.Concept, Correct = right, Sure = true });
            MarkDone(key);
            feedback.Children.Add(Text(right ? "✓ Right order." : "✗ The right order:", 17, bold: true, brush: right ? "FocusBrush" : "PriorityHigh", top: 16));
            if (!right) for (int n = 0; n < q.Items.Count; n++) feedback.Children.Add(Text($"{n + 1}.  {q.Items[n]}", 16, top: 4));
            if (q.Why.Length > 0) feedback.Children.Add(Text(q.Why, 16, top: 10));
            feedback.Children.Add(AnswerLinks(q, _lesson));
            feedback.Children.Add(Primary("Next", Next));
        };
        var reset = Link("Start over", () => { if (pool.Visibility == Visibility.Visible) { picked.Clear(); Redraw(); } });
        reset.Margin = new Thickness(0, 10, 0, 0);
        Redraw();
        _page.Children.Add(pool);
        _page.Children.Add(chosenList);
        _page.Children.Add(reset);
        _page.Children.Add(check);
        _page.Children.Add(feedback);
    }

    // ---------- Done ----------

    private void Finish()
    {
        SetStep("Done");
        _done = true;
        _closeButton.IsEnabled = true;
        _closeButton.Content = "Close";
        if (_cardsOnly != null)
        {
            Course.Add(new TrainingEvent { Kind = "cardReviewDone", Lesson = -1 });
            _page.Children.Add(Text("Card review finished", 28, bold: true));
            _page.Children.Add(Text(_asked == 0 ? "Nothing graded this time." : $"{_right} of {_asked} known.", 18, top: 10));
            _page.Children.Add(Text("Cards you marked ✕ come back tomorrow; ✓ after a while; ✓✓ after a long while.", 16, top: 12));
            _page.Children.Add(Primary("Close", Close));
            return;
        }
        if (_questionsOnly != null)
        {
            bool passed = _asked > 0 && _right * 10 >= _asked * 7;
            if (_examKey != null)
            {
                Course.Add(new TrainingEvent { Kind = "examDone", Note = _examKey, Correct = passed, Lesson = -1 });
                Course.Add(new TrainingEvent { Kind = "lessonDone", Lesson = -1, Note = "exam:" + _examKey });
            }
            else Course.Add(new TrainingEvent { Kind = "allBooksDone" });
            _page.Children.Add(Text(_examKey == null ? "Round finished" : passed ? "Passed" : "Not passed yet", 28, bold: true));
            _page.Children.Add(Text($"{_right} of {_asked} right and sure.", 18, top: 10));
            if (_examKey != null)
                _page.Children.Add(Text(passed ? (_examKey == "final" ? "You've finished the book. Its questions now join All-books practice." : "On to the next part of the book.")
                                               : "The questions you missed come back in review. The exam comes back as tomorrow's lesson.", 16, top: 10));
            _page.Children.Add(Primary("Close", Close));
            return;
        }
        Course.Add(new TrainingEvent { Kind = "lessonDone", Lesson = _lesson.Index });
        _page.Children.Add(Text("Lesson finished", 28, bold: true));
        _page.Children.Add(Text(_asked == 0 ? "Nicely done." : $"{_right} of {_asked} right and sure (this sitting).", 18, top: 10));
        var next = Course.NextLesson();
        _page.Children.Add(Text("Questions you missed or guessed come back tomorrow; ones you knew come back after a longer gap. " +
                                (Course.Config.CardReviewOn ? "Your hardest cards come back for a few minutes this evening." : ""), 16, top: 14));
        _page.Children.Add(Text(next == null ? "That's every imported lesson. Import the next chapter pack on the course page." : $"Next time: {next.Lesson.Title}.", 16, brush: "Muted", top: 10));
        _page.Children.Add(Primary("Close", Close));
    }
}
