using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// The Greek concept quiz (like Biblical Mastery Academy's): Learn asks one lesson's questions, Review asks 12 mixed
/// from every lesson. The mode is chosen on the quiz at the start of the round (Learn by default). Questions may have
/// several right answers; after Check, each shows a short explanation and a link to that spot in the lesson.
/// </summary>
public sealed class ConceptQuizWindow : Window
{
    private static readonly SolidColorBrush Good = new(Color.FromRgb(0x5C, 0xC8, 0x8A)), Bad = new(Color.FromRgb(0xE0, 0x70, 0x5F));
    private readonly System.Drawing.Rectangle _screen;
    private readonly QuizPack? _learnPack;
    private readonly Action<bool> _finished;
    private readonly StackPanel _page = new() { MaxWidth = 760, Margin = new Thickness(24, 10, 24, 40) };
    private readonly Button _learnBtn = new() { Content = "Learn", Padding = new Thickness(16, 6, 16, 6) };
    private readonly Button _reviewBtn = new() { Content = "Review", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private List<(QuizPack Pack, QuizQuestion Q)> _round = new();
    private bool _review, _started, _done;
    private int _index, _right;

    public ConceptQuizWindow(System.Drawing.Rectangle screen, QuizPack? learnPack, bool canReview, string? laterText, Action<bool> finished)
    {
        _screen = screen; _learnPack = learnPack; _finished = finished;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false; Title = "Concept quiz";
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");

        var bar = new DockPanel { Margin = new Thickness(28, 20, 28, 6) };
        var close = new Button { Content = laterText ?? "Close", Padding = new Thickness(18, 8, 18, 8), FontSize = 14 };
        close.Click += (_, _) => { _done = true; Close(); };
        DockPanel.SetDock(close, Dock.Right);
        bar.Children.Add(close);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        modes.Children.Add(_learnBtn); modes.Children.Add(_reviewBtn);
        DockPanel.SetDock(modes, Dock.Right);
        bar.Children.Add(modes);
        bar.Children.Add(_title);
        _learnBtn.IsEnabled = learnPack != null;
        _reviewBtn.IsEnabled = canReview;
        _learnBtn.Click += (_, _) => Begin(review: false);
        _reviewBtn.Click += (_, _) => Begin(review: true);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        SourceInitialized += (_, _) => FitToScreen();
        Loaded += (_, _) => { FitToScreen(); Activate(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _done = true; Close(); } };
        Closed += (_, _) => _finished(_round.Count > 0 && _index >= _round.Count);
        Begin(review: learnPack == null);
    }

    private void FitToScreen()
    {
        var source = PresentationSource.FromVisual(this);
        double sx = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1, sy = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
        Left = _screen.Left * sx; Top = _screen.Top * sy; Width = _screen.Width * sx; Height = _screen.Height * sy;
    }

    /// <summary>Starts the round in Learn or Review. The mode can be changed until the first answer is checked.</summary>
    private void Begin(bool review)
    {
        if (_started || _done) return;
        _review = review || _learnPack == null;
        _round = _review ? ConceptQuiz.ReviewRound(12) : ConceptQuiz.LearnRound(_learnPack!);
        _index = 0; _right = 0;
        _title.Text = _review ? "Concept Quiz · Review" : $"Concept Quiz · L{_learnPack!.Lesson}";
        foreach (var (b, on) in new[] { (_learnBtn, !_review), (_reviewBtn, _review) })
        {
            if (on) b.SetResourceReference(StyleProperty, "PrimaryButton"); else b.ClearValue(StyleProperty);
        }
        ShowQuestion();
    }

    private TextBlock T(string text, double size, bool bold = false, string? brush = null, double top = 0)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        if (brush != null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private void ShowQuestion()
    {
        _page.Children.Clear();
        if (_round.Count == 0) { _page.Children.Add(T("No questions yet: import a question pack in Settings, Greek practice.", 18, top: 30)); return; }
        if (_index >= _round.Count) { ShowEnd(); return; }
        var (pack, q) = _round[_index];

        // Progress: 1 / 12, and a bar
        var prog = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var mode = T(_review ? "Review mode" : "Learn mode", 13, brush: "FocusBrush"); DockPanel.SetDock(mode, Dock.Right);
        prog.Children.Add(mode);
        prog.Children.Add(T($"Progress: {_index + 1} / {_round.Count}", 14));
        _page.Children.Add(prog);
        var track = new Grid { Height = 5, Margin = new Thickness(0, 8, 0, 0) };
        var back = new Border { CornerRadius = new CornerRadius(3) }; back.SetResourceReference(Border.BackgroundProperty, "Panel3");
        var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 712.0 * (_index + 1) / _round.Count };
        fill.SetResourceReference(Border.BackgroundProperty, "FocusBrush");
        track.Children.Add(back); track.Children.Add(fill);
        _page.Children.Add(track);

        // The question card
        var card = new StackPanel();
        card.Children.Add(T($"Question {_index + 1}" + (_review ? $" · L{pack.Lesson}" : ""), 16, bold: true, brush: "FocusBrush"));
        card.Children.Add(T(q.Prompt, 21, top: 10));
        var cardBox = new Border { Child = card, Padding = new Thickness(26, 22, 26, 22), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 22, 0, 0) };
        cardBox.SetResourceReference(Border.BackgroundProperty, "Panel");
        _page.Children.Add(cardBox);

        _page.Children.Add(T("Select the correct answer(s):", 15, bold: true, top: 22));
        var picked = new HashSet<int>();
        var rows = new List<Border>();
        for (int i = 0; i < q.Options.Count; i++)
        {
            int idx = i;
            var mark = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            mark.SetResourceReference(Border.BorderBrushProperty, "Muted");
            var line = new DockPanel();
            DockPanel.SetDock(mark, Dock.Left);
            line.Children.Add(mark);
            line.Children.Add(T(q.Options[i], 17));
            var row = new Border { Child = line, Padding = new Thickness(18, 14, 18, 14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            row.SetResourceReference(Border.BorderBrushProperty, "Line");
            row.MouseLeftButtonUp += (_, _) =>
            {
                if (row.Tag as string == "checked") return;
                if (!picked.Remove(idx)) picked.Add(idx);
                bool on = picked.Contains(idx);
                if (on) { mark.SetResourceReference(Border.BackgroundProperty, "FocusBrush"); mark.SetResourceReference(Border.BorderBrushProperty, "FocusBrush"); row.SetResourceReference(Border.BorderBrushProperty, "FocusBrush"); }
                else { mark.Background = Brushes.Transparent; mark.SetResourceReference(Border.BorderBrushProperty, "Muted"); row.SetResourceReference(Border.BorderBrushProperty, "Line"); }
            };
            rows.Add(row);
            _page.Children.Add(row);
        }

        var after = new StackPanel();
        var check = new Button { Content = "Check", Padding = new Thickness(30, 11, 30, 11), FontSize = 15, Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsDefault = true };
        check.SetResourceReference(StyleProperty, "PrimaryButton");
        check.Click += (_, _) =>
        {
            if (picked.Count == 0) return;
            _started = true;                    // the mode is set for this round now
            _learnBtn.IsEnabled = _reviewBtn.IsEnabled = false;
            bool right = picked.SetEquals(q.Correct);
            if (right) _right++;
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Tag = "checked"; rows[i].Cursor = Cursors.Arrow;
                bool should = q.Correct.Contains(i), chose = picked.Contains(i);
                if (should) { rows[i].BorderBrush = Good; rows[i].BorderThickness = new Thickness(2); }
                else if (chose) { rows[i].BorderBrush = Bad; rows[i].BorderThickness = new Thickness(2); }
            }
            check.Visibility = Visibility.Collapsed;
            var verdict = T(right ? "Correct" : "Not quite", 18, bold: true, top: 22);
            verdict.Foreground = right ? Good : Bad;
            after.Children.Add(verdict);
            if (!right) after.Children.Add(T("The right answer" + (q.Correct.Count > 1 ? "s are" : " is") + " outlined in green.", 14, brush: "Muted", top: 4));
            if (q.Explanation.Length > 0) after.Children.Add(T(q.Explanation, 16, top: 10));
            var section = pack.Sections.FirstOrDefault(s => s.Id == q.Section);
            var where = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            where.Children.Add(T($"L{pack.Lesson}" + (section != null ? $", {section.Heading}" : ""), 14, brush: "Muted"));
            if (section != null)
            {
                var link = new TextBlock { Text = "Open in the lesson", FontSize = 14, Margin = new Thickness(14, 0, 0, 0), Cursor = Cursors.Hand, TextDecorations = TextDecorations.Underline };
                link.SetResourceReference(TextBlock.ForegroundProperty, "FocusBrush");
                link.MouseLeftButtonUp += (_, _) => new QuizLessonWindow(pack, section.Id) { Owner = this }.Show();
                where.Children.Add(link);
            }
            after.Children.Add(where);
            var next = new Button { Content = _index + 1 < _round.Count ? "Next" : "Finish", Padding = new Thickness(30, 11, 30, 11), FontSize = 15, Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsDefault = true };
            next.SetResourceReference(StyleProperty, "PrimaryButton");
            next.Click += (_, _) => { _index++; ShowQuestion(); };
            after.Children.Add(next);
            next.Focus();
        };
        _page.Children.Add(check);
        _page.Children.Add(after);
    }

    private void ShowEnd()
    {
        _page.Children.Add(T("Round finished", 28, bold: true, top: 30));
        _page.Children.Add(T($"{_right} of {_round.Count} right.", 18, top: 10));
        _page.Children.Add(T(_review ? "Review mixes every lesson so far." : $"Learn mode: Lesson {_learnPack?.Lesson}. Take it a few times this week.", 15, brush: "Muted", top: 8));
        var close = new Button { Content = "Close", Padding = new Thickness(30, 11, 30, 11), FontSize = 15, Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsDefault = true };
        close.SetResourceReference(StyleProperty, "PrimaryButton");
        close.Click += (_, _) => Close();
        _page.Children.Add(close);
    }
}
