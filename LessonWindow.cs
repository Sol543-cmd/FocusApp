using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// A book-course lesson, full screen: Warm-up (unscored guesses), the Reading (whole, no timer),
/// Review (due questions from earlier lessons), Practice (multiple choice, Sure or Guessing),
/// Memory cards (click to flip), the Chapter quiz (last lesson of a chapter), then Done.
/// Every answer has "Show where this is in the text", Hide and Flag.
/// In required mode (the day's last time) it can't be closed until finished; quitting Focus is the only way out.
/// </summary>
public class LessonWindow : Window
{
    private readonly Course.LessonRef _lesson;
    private readonly bool _required;
    private readonly Action<bool> _finished;
    private readonly List<Action> _steps = new();
    private int _step = -1;
    private bool _done;
    private int _right, _asked;

    private readonly Grid _root = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _page = new() { MaxWidth = 760, Margin = new Thickness(32, 28, 32, 40) };
    private readonly TextBlock _stepLabel = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _readingLayer = new() { Visibility = Visibility.Collapsed };

    private readonly string? _examKey;
    private readonly List<(Course.LessonRef Lesson, PackQuestion Q)>? _questionsOnly;
    private readonly string _title;

    /// <summary>An exam (examKey "unit1"... or "final") or an All-books practice round (examKey null): multiple choice only.</summary>
    public LessonWindow(string title, List<(Course.LessonRef Lesson, PackQuestion Q)> questions, string? examKey, Action<bool> finished, bool required = false)
        : this(questions.Count > 0 ? questions[0].Lesson : Course.Lessons().First(), required, finished, title, questions, examKey) { }

    public LessonWindow(Course.LessonRef lesson, bool required, Action<bool> finished)
        : this(lesson, required, finished, null, null, null) { }

    private LessonWindow(Course.LessonRef lesson, bool required, Action<bool> finished, string? title,
                         List<(Course.LessonRef Lesson, PackQuestion Q)>? questions, string? examKey)
    {
        _lesson = lesson;
        _questionsOnly = questions;
        _examKey = examKey;
        _title = title ?? lesson.Lesson.Title;
        _required = required;
        _finished = finished;
        Title = "Focus · " + _title;
        WindowState = WindowState.Maximized;
        Width = 1100; Height = 800;
        Topmost = required;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        FontSize = 16;

        var top = new DockPanel { Margin = new Thickness(24, 14, 24, 10) };
        var close = new Button { Content = required ? "Required today" : "Close", Padding = new Thickness(14, 5, 14, 5), IsEnabled = !required,
                                 ToolTip = required ? "Today's last lesson time: finish the lesson to close it (or quit Focus, which moves it to tomorrow)." : "Close; the lesson waits for the next time" };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        top.Children.Add(close);
        var title = new TextBlock { Text = _title, FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        top.Children.Add(title);
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
        Closed += (_, _) => _finished(_done);
        Loaded += (_, _) => Next();
    }

    // ---------- Steps ----------

    private void BuildSteps()
    {
        if (_questionsOnly != null)
        {
            _steps.Add(() =>
            {
                SetStep("Start");
                _page.Children.Add(Text(_title, 28, bold: true));
                _page.Children.Add(Text(_examKey == null ? $"{_questionsOnly.Count} questions from the books you've finished. Each names its chapter."
                                                         : $"{_questionsOnly.Count} questions, mostly ones you haven't seen. Pass with 70%.", 16, top: 10));
                _page.Children.Add(Primary("Begin", Next));
            });
            for (int i = 0; i < _questionsOnly.Count; i++)
            {
                var (ql, q) = _questionsOnly[i]; int n = i;
                _steps.Add(() => ShowMc(q, ql, $"{(_examKey == null ? "Question" : "Exam question")} {n + 1} of {_questionsOnly.Count} · Chapter {ql.Pack.Chapter}", scored: true));
            }
            _steps.Add(Finish);
            return;
        }
        var l = _lesson.Lesson;
        var hidden = Course.Hidden();
        _steps.Add(Intro);
        var warm = l.Warmup.Where(q => !hidden.Contains(q.Id)).ToList();
        for (int i = 0; i < warm.Count; i++) { var q = warm[i]; int n = i; _steps.Add(() => ShowMc(q, _lesson, $"Warm-up {n + 1} of {warm.Count}", scored: false)); }
        _steps.Add(Reading);
        var review = Course.DueReview(Course.Config.ReviewCap);
        for (int i = 0; i < review.Count; i++)
        {
            var (rl, rq) = review[i]; int n = i;
            _steps.Add(() => { if (rq.Kind == "card") ShowCard(rq, rl, $"Review {n + 1} of {review.Count}", requeue: false); else ShowMc(rq, rl, $"Review {n + 1} of {review.Count}", scored: true); });
        }
        var practice = l.Practice.Where(q => !hidden.Contains(q.Id)).ToList();
        for (int i = 0; i < practice.Count; i++) { var q = practice[i]; int n = i; _steps.Add(() => ShowMc(q, _lesson, $"Practice {n + 1} of {practice.Count}", scored: true)); }
        var cards = l.Cards.Where(q => !hidden.Contains(q.Id)).ToList();
        for (int i = 0; i < cards.Count; i++) { var q = cards[i]; int n = i; _steps.Add(() => ShowCard(q, _lesson, $"Memory card {n + 1} of {cards.Count}", requeue: true)); }
        if (l.EndsChapter || l.Quiz.Count > 0)
        {
            var quiz = l.Quiz.Where(q => !hidden.Contains(q.Id)).ToList();
            for (int i = 0; i < quiz.Count; i++)
            {
                var q = quiz[i]; string label = $"Chapter quiz {i + 1} of {quiz.Count}";
                _steps.Add(() => { if (q.Kind == "order") ShowOrder(q, label); else ShowMc(q, _lesson, label, scored: true); });
            }
        }
        _steps.Add(Finish);
    }

    private void Next()
    {
        _step++;
        if (_step >= _steps.Count) return;
        _page.Children.Clear();
        _scroll.ScrollToTop();
        _steps[_step]();
    }

    // ---------- Building blocks ----------

    private TextBlock Text(string text, double size = 16, bool bold = false, string? brush = null, double top = 0)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, top, 0, 0) };
        ScriptureLinks.SetText(t, text);
        if (brush != null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private Button Primary(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 22, 10, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.SetResourceReference(StyleProperty, "PrimaryButton");
        b.Click += (_, _) => click();
        return b;
    }

    private Button Link(string text, Action click)
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

    // ---------- Intro and reading ----------

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
        _page.Children.Add(Text("First a few quick guesses (not scored), then the reading, then practice and memory cards.", 14, brush: "Muted", top: 22));
        _page.Children.Add(Primary("Begin", Next));
    }

    private void Reading()
    {
        SetStep("Reading");
        var (start, end) = Course.LessonRange(_lesson);
        var head = new DockPanel();
        var mode = new CheckBox { Content = "Page view", IsChecked = Course.Config.PageView, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Turn pages like a book instead of scrolling" };
        DockPanel.SetDock(mode, Dock.Right);
        head.Children.Add(mode);
        head.Children.Add(Text("Reading", 24, bold: true));
        _page.Children.Add(head);
        if (_lesson.Lesson.Parts.Count > 0)
            _page.Children.Add(Text("Parts: " + string.Join(" · ", _lesson.Lesson.Parts.Select(p => p.Title)), 14, brush: "Muted", top: 6));
        if (start < 0)
        {
            _page.Children.Add(Text("Focus couldn't find this lesson in your book file. Read it in your book, then continue.", 16, top: 16));
            _page.Children.Add(Primary("I've finished reading", Next));
            return;
        }
        var map = new Dictionary<Paragraph, int>();
        FrameworkElement? viewer = null;
        var holder = new ContentControl { Margin = new Thickness(0, 14, 0, 0) };
        void Build()
        {
            map.Clear();
            viewer = BuildReading(start, end, -1, Course.Config.PageView, ActualHeight - 260, map, out _);
            holder.Content = viewer;
        }
        _page.Children.Add(HighlightBar(() => viewer, map, _lesson.Pack.Chapter));
        _page.Children.Add(holder);
        Build();
        mode.Checked += (_, _) => { Course.Config.PageView = true; Course.SaveConfig(); Build(); };
        mode.Unchecked += (_, _) => { Course.Config.PageView = false; Course.SaveConfig(); Build(); };
        _page.Children.Add(Primary("I've finished reading", Next));
    }

    private static readonly Dictionary<string, Color> HighlightColors = new()
    {
        ["yellow"] = Color.FromArgb(0x70, 0xF5, 0xD0, 0x4A), ["green"] = Color.FromArgb(0x60, 0x7C, 0xC8, 0x7A), ["blue"] = Color.FromArgb(0x60, 0x6A, 0xA8, 0xE8)
    };

    /// <summary>
    /// The book's paragraphs as a document, with headings, italics, shaded sidebars, pictures and your highlights;
    /// optionally one paragraph marked (Show where). Page view turns pages like a book; otherwise it scrolls.
    /// </summary>
    public static FrameworkElement BuildReading(int start, int end, int highlight, bool pageView, double pageHeight,
                                                Dictionary<Paragraph, int>? map, out Block? target)
    {
        target = null;
        var docu = new FlowDocument { PagePadding = new Thickness(pageView ? 24 : 0), FontSize = 17, LineHeight = 28, TextAlignment = TextAlignment.Left, ColumnWidth = 900 };
        docu.SetResourceReference(TextElement.ForegroundProperty, "Text");
        docu.FontFamily = new FontFamily("Georgia, Cambria, Segoe UI");
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
                if (bp.Heading > 0) { p.FontSize = bp.Heading == 1 ? 26 : bp.Heading == 2 ? 21 : 18; p.FontWeight = FontWeights.SemiBold; p.FontFamily = new FontFamily("Segoe UI"); }
                foreach (var r in bp.Runs)
                {
                    Inline run = new Run(r.Text);
                    if (r.Super) { run.BaselineAlignment = BaselineAlignment.Superscript; run.FontSize = 11; }
                    if (r.Italic) run = new Italic(run);
                    if (r.Bold) run = new Bold(run);
                    p.Inlines.Add(run);
                }
                if (i == highlight) { p.SetResourceReference(TextElement.BackgroundProperty, "FocusTint"); target = p; }
                foreach (var h in highlights[i]) ApplyHighlight(p, h.Quote, h.Color, h.Note);
                map?.Add(p, i);
                block = p;
            }
            if (bp.Sidebar)
            {
                if (sidebar == null)
                {
                    sidebar = new Section { Padding = new Thickness(16, 12, 16, 4), Margin = new Thickness(0, 6, 0, 16), BorderThickness = new Thickness(3, 0, 0, 0) };
                    sidebar.SetResourceReference(TextElement.BackgroundProperty, "Panel2");
                    sidebar.SetResourceReference(Block.BorderBrushProperty, "FocusBrush");
                    docu.Blocks.Add(sidebar);
                }
                if (block is Paragraph sp) sp.FontSize = Math.Min(sp.FontSize, 16);
                sidebar.Blocks.Add(block);
            }
            else { sidebar = null; docu.Blocks.Add(block); }
        }
        if (pageView)
            return new FlowDocumentPageViewer { Document = docu, Height = Math.Max(420, pageHeight), ToolTip = "Turn pages with the arrows below, or Page Up and Page Down" };
        return new FlowDocumentScrollViewer { Document = docu, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, IsToolBarVisible = false, IsSelectionEnabled = true };
    }

    /// <summary>Colors the first place the quote appears in the paragraph.</summary>
    public static void ApplyHighlight(Paragraph p, string quote, string color, string note)
    {
        try
        {
            var text = new System.Text.StringBuilder();
            var positions = new List<TextPointer>();
            for (var tp = p.ContentStart; tp != null && tp.CompareTo(p.ContentEnd) < 0; tp = tp.GetNextContextPosition(LogicalDirection.Forward))
            {
                if (tp.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
                string run = tp.GetTextInRun(LogicalDirection.Forward);
                for (int k = 0; k < run.Length; k++) { text.Append(run[k]); positions.Add(tp.GetPositionAtOffset(k)!); }
            }
            int at = text.ToString().IndexOf(quote.Trim(), StringComparison.Ordinal);
            if (at < 0 || quote.Trim().Length == 0) return;
            int endIdx = at + quote.Trim().Length;
            var endPtr = endIdx < positions.Count ? positions[endIdx] : p.ContentEnd;
            var range = new TextRange(positions[at], endPtr);
            range.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(HighlightColors.TryGetValue(color, out var c) ? c : HighlightColors["yellow"]));
        }
        catch (Exception ex) { Log.Error("ApplyHighlight", ex); }
    }

    /// <summary>The highlight bar above the reading: select text, then pick a color or add a note.</summary>
    private FrameworkElement HighlightBar(Func<FrameworkElement?> viewer, Dictionary<Paragraph, int> map, int chapter)
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var hint = new TextBlock { Text = "Select text, then:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontSize = 13 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        bar.Children.Add(hint);
        void Mark(string color, bool withNote)
        {
            var sel = viewer() switch { FlowDocumentScrollViewer v => v.Selection, FlowDocumentPageViewer v => v.Selection, _ => null };
            if (sel == null || sel.IsEmpty) { hint.Text = "Select some text first, then:"; return; }
            var para = sel.Start.Paragraph;
            if (para == null || !map.TryGetValue(para, out int index)) { hint.Text = "Select text within one paragraph, then:"; return; }
            var end = sel.End.CompareTo(para.ContentEnd) > 0 ? para.ContentEnd : sel.End;
            string quote = new TextRange(sel.Start, end).Text.Trim();
            if (quote.Length == 0) return;
            string note = "";
            if (withNote)
            {
                var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, Margin = new Thickness(0, 10, 0, 10) };
                var ok = new Button { Content = "Save note", IsDefault = true, Padding = new Thickness(16, 5, 16, 5), HorizontalAlignment = HorizontalAlignment.Right };
                var panel = new StackPanel { Margin = new Thickness(18) };
                panel.Children.Add(new TextBlock { Text = "“" + (quote.Length > 120 ? quote[..120] + "…" : quote) + "”", TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic });
                panel.Children.Add(box); panel.Children.Add(ok);
                var dlg = new Window { Title = "Note", Width = 460, SizeToContent = SizeToContent.Height, Content = panel, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = Topmost };
                dlg.SetResourceReference(BackgroundProperty, "Panel");
                dlg.SetResourceReference(ForegroundProperty, "Text");
                ok.Click += (_, _) => dlg.DialogResult = true;
                dlg.Loaded += (_, _) => box.Focus();
                if (dlg.ShowDialog() != true) return;
                note = box.Text.Trim();
            }
            Course.AddHighlight(chapter, index, quote, color, note);
            ApplyHighlight(para, quote, color, note);
            sel.Select(sel.End, sel.End);
            hint.Text = withNote ? "Highlight and note saved. Select more text, then:" : "Highlighted. Select more text, then:";
        }
        foreach (var (name, label) in new[] { ("yellow", "Yellow"), ("green", "Green"), ("blue", "Blue") })
        {
            var b = new Button { Content = label, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0), Background = new SolidColorBrush(HighlightColors[name]) };
            string n = name;
            b.Click += (_, _) => Mark(n, false);
            bar.Children.Add(b);
        }
        var noteBtn = new Button { Content = "Highlight + note…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
        noteBtn.Click += (_, _) => Mark("yellow", true);
        bar.Children.Add(noteBtn);
        return bar;
    }

    /// <summary>A window showing part of the book with one paragraph marked (from the notes list).</summary>
    public static void OpenPassage(Window? owner, int start, int end, int para, string title)
    {
        var host = new StackPanel { MaxWidth = 760, Margin = new Thickness(32, 20, 32, 40) };
        var viewer = BuildReading(start, end, para, false, 0, null, out var target);
        host.Children.Add(viewer);
        var w = new Window { Title = title, Width = 900, Height = 760, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                             Content = new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        w.SetResourceReference(BackgroundProperty, "Bg");
        w.SetResourceReference(ForegroundProperty, "Text");
        if (target != null) w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(new Action(() => target.BringIntoView()), System.Windows.Threading.DispatcherPriority.ContextIdle);
        w.Show();
    }

    /// <summary>Opens the lesson's reading over the question, scrolled to the answer with it highlighted.</summary>
    private void ShowWhere(Course.LessonRef lesson, PackRef r)
    {
        int at = Course.Locate(lesson, r);
        var (start, end) = Course.LessonRange(lesson);
        _readingLayer.Children.Clear();
        var panel = new DockPanel();
        panel.SetResourceReference(Panel.BackgroundProperty, "Bg");
        var bar = new DockPanel { Margin = new Thickness(24, 14, 24, 10) };
        var back = new Button { Content = "Back to the question", Padding = new Thickness(16, 6, 16, 6) };
        back.SetResourceReference(StyleProperty, "PrimaryButton");
        back.Click += (_, _) => _readingLayer.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(back, Dock.Right);
        bar.Children.Add(back);
        bar.Children.Add(new TextBlock { Text = at < 0 ? "That passage wasn't found in your book file." : $"Chapter {lesson.Pack.Chapter}: {lesson.Pack.Title}", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        if (start >= 0)
        {
            var host = new StackPanel { MaxWidth = 760, Margin = new Thickness(32, 8, 32, 40) };
            var viewer = BuildReading(start, end, at, false, 0, null, out var target);
            host.Children.Add(viewer);
            var sv = new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(sv);
            if (target != null) Dispatcher.BeginInvoke(new Action(() => target.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        _readingLayer.Children.Add(panel);
        _readingLayer.Visibility = Visibility.Visible;
    }

    // ---------- Multiple choice ----------

    private void ShowMc(PackQuestion q, Course.LessonRef lesson, string label, bool scored)
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

    private readonly HashSet<string> _requeued = new();

    private void ShowCard(PackQuestion q, Course.LessonRef lesson, string label, bool requeue)
    {
        SetStep(label);
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
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        var extra = new StackPanel();
        bool answered = false;
        card.MouseLeftButtonUp += (_, _) =>
        {
            flipped = !flipped;
            ScriptureLinks.SetText(face, flipped ? q.Back : q.Front);
            side.Text = flipped ? "Answer · click to flip back" : "Question · click to flip";
            if (flipped && !answered) buttons.Visibility = Visibility.Visible;
        };
        void Grade(bool gotIt)
        {
            if (answered) return;
            answered = true;
            buttons.Visibility = Visibility.Collapsed;
            _asked++; if (gotIt) _right++;
            Course.Add(new TrainingEvent { Kind = "answer", Question = q.Id, Concept = q.Concept, Correct = gotIt, Sure = gotIt });
            if (!gotIt && requeue && _requeued.Add(q.Id))
                _steps.Insert(Math.Max(_step + 1, _steps.Count - 1 - QuizSteps()), () => ShowCard(q, lesson, "Memory card (again)", requeue: false));
            extra.Children.Add(Text(gotIt ? "✓ Got it." : "It comes back later in this lesson.", 16, bold: true, brush: gotIt ? "FocusBrush" : "PriorityHigh", top: 14));
            extra.Children.Add(AnswerLinks(q, lesson));
            extra.Children.Add(Primary("Next", Next));
        }
        var got = new Button { Content = "Got it", Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 18, 10, 0) };
        got.SetResourceReference(StyleProperty, "PrimaryButton");
        got.Click += (_, _) => Grade(true);
        var missed = new Button { Content = "Missed it", Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 18, 0, 0) };
        missed.Click += (_, _) => Grade(false);
        buttons.Children.Add(got);
        buttons.Children.Add(missed);
        _page.Children.Add(card);
        _page.Children.Add(buttons);
        _page.Children.Add(extra);
    }

    private int QuizSteps()
    {
        var hidden = Course.Hidden();
        return _lesson.Lesson.Quiz.Count(q => !hidden.Contains(q.Id));
    }

    // ---------- Putting things in order ----------

    private void ShowOrder(PackQuestion q, string label)
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
        _page.Children.Add(Text(_asked == 0 ? "Nicely done." : $"{_right} of {_asked} right and sure.", 18, top: 10));
        int due = Course.DueReview(100).Count;
        var next = Course.NextLesson();
        _page.Children.Add(Text("Questions you missed or guessed come back tomorrow; ones you knew come back after a longer gap.", 16, top: 14));
        _page.Children.Add(Text(next == null ? "That's every imported lesson. Import the next chapter pack on the course page." : $"Next time: {next.Lesson.Title}.", 16, brush: "Muted", top: 10));
        _page.Children.Add(Primary("Close", Close));
    }
}
