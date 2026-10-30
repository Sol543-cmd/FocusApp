using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>
/// Greek practice: match each Greek word to its meaning, press Go, and any you missed come back
/// (shuffled) until they're all right. Shown on the screen your mouse is on; other screens are dimmed.
/// </summary>
public partial class VocabWindow : Window
{
    private static readonly List<VocabWindow> Open = new();
    public static bool IsShowing => Open.Count > 0;
    /// <summary>When the round on screen opened (for the end-of-day and midnight resets).</summary>
    public static DateTime OpenedAt { get; private set; } = DateTime.MinValue;

    // Soft colors that tell matched pairs apart (number badges carry the meaning too).
    private static readonly Color[] PairColors =
        { Color.FromRgb(0x24, 0x6F, 0xE0), Color.FromRgb(0x88, 0x4D, 0xFF), Color.FromRgb(0xEB, 0x89, 0x09), Color.FromRgb(0x14, 0xAA, 0xF5),
          Color.FromRgb(0xB8, 0x25, 0x6F), Color.FromRgb(0x05, 0x85, 0x27) };

    private readonly System.Drawing.Rectangle _screen;
    private readonly Action<bool>? _finished;          // true = done, false = later
    private List<VocabCard> _round = new();             // the words still being matched
    private readonly HashSet<string> _missed = new();   // missed at least once this time
    private readonly HashSet<string> _seen = new();     // every word in this round
    private readonly Dictionary<VocabCard, VocabCard> _pairs = new();   // Greek -> chosen meaning
    private List<VocabCard> _meanings = new();
    private VocabCard? _pickedGreek;
    private int _tries;
    private bool _checking;

    private readonly Func<int, List<VocabCard>>? _nextRound; // reviewing any time: makes the next round
    private readonly IPracticeDeck _deck;                    // the words, or the alphabet
    private int _size;
    private int _roundsDone, _wordsDone, _rightFirst;

    public static void ShowOnAllScreens(List<VocabCard> words, Action<bool> finished, Func<int, List<VocabCard>>? nextRound = null, int startSize = 4,
                                        IPracticeDeck? deck = null)
    {
        if (IsShowing || words.Count == 0) return;
        OpenedAt = DateTime.Now;
        var screen = WorkingScreen();
        VocabWindow? w = null;
        try
        {
            w = new VocabWindow(screen.Bounds, words, finished, nextRound, startSize, deck ?? WordsDeck.Instance);
            Open.Add(w);
            w.Show();
            w.Activate();
        }
        catch (Exception ex)
        {
            // Never leave anything half-open on screen.
            Log.Error("VocabWindow", ex);
            CloseAll();
            try { w?.Close(); } catch { }
        }
    }

    /// <summary>The screen you're working on: where the active window is, otherwise where the mouse is.</summary>
    private static System.Windows.Forms.Screen WorkingScreen()
    {
        try
        {
            var active = GetForegroundWindow();
            if (active != IntPtr.Zero) return System.Windows.Forms.Screen.FromHandle(active);
        }
        catch { }
        var screens = System.Windows.Forms.Screen.AllScreens;
        return screens.FirstOrDefault(sc => sc.Bounds.Contains(System.Windows.Forms.Control.MousePosition))
               ?? System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>The practice day ended while a round was left open: it closes as if you'd chosen Later.</summary>
    public static void CloseForToday()
    {
        var first = Open.FirstOrDefault();
        if (first != null) first.Finish(done: false); else CloseAll();
    }

    private static void CloseAll()
    {
        foreach (var w in Open.ToList()) { try { w.Close(); } catch { } }
        Open.Clear();
    }

    private VocabWindow(System.Drawing.Rectangle screen, List<VocabCard> words, Action<bool>? finished, Func<int, List<VocabCard>>? nextRound, int startSize,
                        IPracticeDeck deck)
    {
        InitializeComponent();
        _deck = deck;
        TitleText.Text = deck.Title;
        HintText.Text = deck.Hint;
        _screen = screen;
        _finished = finished;
        _nextRound = nextRound;
        _size = Math.Clamp(startSize, 3, 10);
        if (nextRound != null)
        {
            TitleText.Text = deck is Alphabet ? "Alphabet review" : "Greek review";
            LaterButton.Content = "Done";
            LaterButton.ToolTip = "Stop reviewing";
            BuildSizeChoices();
        }
        SourceInitialized += (_, _) =>
        {
            FitToScreen();
            // Nearly the whole screen, but the grid stays in a centered column about 1,200 wide so answers sit near their questions.
            Card.Width = Math.Max(600, Math.Min(1200, Width - 80));
            TilesScroll.MaxHeight = Math.Max(300, Height - 300);
        };
        _round = words.ToList();
        foreach (var c in _round) _seen.Add(c.Id); // these are the words this round will record
        Build();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && ContinuePanel.Visibility == Visibility.Visible) { Next_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.Enter && GoButton.IsEnabled) { Go_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.Escape) { if (_nextRound != null) Done_Click(this, new RoutedEventArgs()); else Finish(done: false); e.Handled = true; }
        };
    }

    /// <summary>Covers the whole screen, in real pixels (works with any display scaling).</summary>
    private void FitToScreen()
    {
        var source = PresentationSource.FromVisual(this);
        double sx = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1, sy = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
        Left = _screen.Left * sx; Top = _screen.Top * sy; Width = _screen.Width * sx; Height = _screen.Height * sy;
    }

    private void Build()
    {
        _pairs.Clear();
        _pickedGreek = null;
        var rnd = new Random();
        // The round's meanings, plus any extras (with no match) from Settings, all shuffled together.
        _meanings = _round.Concat(_deck.Extras(_round, rnd)).OrderBy(_ => rnd.Next()).ToList();
        if (_meanings.Count > _round.Count && !HintText.Text.Contains("match anything"))
            HintText.Text += _meanings.Count - _round.Count == 1 ? " One answer doesn't match anything." : $" {_meanings.Count - _round.Count} answers don't match anything.";
        Redraw();
    }

    private readonly List<Button> _leftTiles = new(), _rightTiles = new();

    private static string Letter(int i) => i < 26 ? ((char)('A' + i)).ToString() : (i + 1).ToString();

    /// <summary>A leading section number like "3.2", "§12" or "[4]" at the start of a question: shown as a small tag.</summary>
    private static readonly System.Text.RegularExpressions.Regex SectionPrefix =
        new(@"^\s*(?:\[(?<s>[^\]]{1,12})\]|(?<s>§\s*\d+(?:\.\d+)*)|(?<s>\d+(?:\.\d+)+))[\s.:)\-–]*\s+(?<rest>\S.*)$",
            System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>One tile: its number or letter on the left, the text (with any section tag), and room on the right for its partner's label.</summary>
    private Button MakeTile(string text, bool left, VocabCard card, string label)
    {
        bool compact = _round.Count > 6; // bigger rounds get slimmer tiles so all ten fit
        var b = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                             Margin = new Thickness(0, 0, 0, compact ? 8 : 12),
                             Padding = compact ? new Thickness(14, 9, 14, 9) : new Thickness(16, 14, 16, 14),
                             Cursor = Cursors.Hand, Tag = card, BorderThickness = new Thickness(1.5) };
        // The same text size on both sides (the alphabet's letters stay large).
        double size = left && _deck.LargeLeft ? (compact ? 24 : 28) : (compact ? 16 : 18);

        var row = new DockPanel { LastChildFill = true };
        var number = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 12, 0),
                                  VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1.2) };
        number.SetResourceReference(Border.BorderBrushProperty, "Muted");
        var numberText = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                                         HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        numberText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        number.Child = numberText;
        DockPanel.SetDock(number, Dock.Left);
        row.Children.Add(number);

        // Its partner's label once matched (filled in by Mark).
        var partner = new Border { MinWidth = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0),
                                   VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden, Tag = "partner" };
        DockPanel.SetDock(partner, Dock.Right);
        row.Children.Add(partner);

        var body = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var match = SectionPrefix.Match(text);
        if (match.Success)
        {
            var tag = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
            tag.SetResourceReference(Border.BackgroundProperty, "Line");
            var tagText = new TextBlock { Text = match.Groups["s"].Value.Trim(), FontSize = 11.5, FontWeight = FontWeights.SemiBold };
            tagText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            tag.Child = tagText;
            body.Children.Add(tag);
            text = match.Groups["rest"].Value;
        }
        var words = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (_deck is Concepts) ScriptureLinks.Fill(words, text); else words.Text = text; // references in your concepts open in Logos
        if (left) words.FontFamily = new FontFamily("Segoe UI, Palatino Linotype, Times New Roman");
        body.Children.Add(words);
        row.Children.Add(body);
        b.Content = row;
        b.SizeChanged += (_, _) => words.MaxWidth = Math.Max(80, b.ActualWidth - 120); // wrap inside the tile
        b.SetResourceReference(StyleProperty, "Tile");
        b.SetResourceReference(BackgroundProperty, "Panel2");
        b.SetResourceReference(BorderBrushProperty, "Panel2");
        b.Click += (_, _) => TileClicked(card, left);
        return b;
    }

    private void Redraw()
    {
        PairGrid.Children.Clear();
        PairGrid.RowDefinitions.Clear();
        _leftTiles.Clear();
        _rightTiles.Clear();
        var order = _round.ToList();
        for (int i = 0; i < order.Count; i++)
        {
            var card = order[i];
            var tile = MakeTile(card.Greek, true, card, (i + 1).ToString());
            if (_pairs.TryGetValue(card, out var chosen)) Mark(tile, i, Letter(_meanings.IndexOf(chosen)));
            else if (ReferenceEquals(card, _pickedGreek))
            {
                tile.BorderThickness = new Thickness(2.5);
                tile.SetResourceReference(BorderBrushProperty, "FocusBrush");
                tile.SetResourceReference(BackgroundProperty, "FocusTint");
            }
            _leftTiles.Add(tile);
        }
        for (int i = 0; i < _meanings.Count; i++)
        {
            var meaning = _meanings[i];
            var tile = MakeTile(meaning.English, false, meaning, Letter(i));
            var owner = _pairs.FirstOrDefault(p => ReferenceEquals(p.Value, meaning)).Key;
            if (owner != null) Mark(tile, order.IndexOf(owner), (order.IndexOf(owner) + 1).ToString());
            _rightTiles.Add(tile);
        }
        int rows = Math.Max(_leftTiles.Count, _rightTiles.Count);
        for (int r = 0; r < rows; r++)
        {
            PairGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (r < _leftTiles.Count) { Grid.SetRow(_leftTiles[r], r); Grid.SetColumn(_leftTiles[r], 0); PairGrid.Children.Add(_leftTiles[r]); }
            if (r < _rightTiles.Count) { Grid.SetRow(_rightTiles[r], r); Grid.SetColumn(_rightTiles[r], 2); PairGrid.Children.Add(_rightTiles[r]); }
        }
        GoButton.IsEnabled = _pairs.Count == _round.Count && !_checking;
        GoButton.Content = _tries == 0 ? "Go" : "Check again";
    }

    /// <summary>Colors a matched tile and labels it with its partner (a letter on the left, a number on the right).</summary>
    private static void Mark(Button tile, int pairIndex, string partnerLabel)
    {
        var color = PairColors[Math.Max(0, pairIndex) % PairColors.Length];
        tile.BorderBrush = new SolidColorBrush(color);
        tile.Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
        if (tile.Content is DockPanel row && row.Children.OfType<Border>().FirstOrDefault(x => Equals(x.Tag, "partner")) is Border partner)
        {
            partner.Background = new SolidColorBrush(color);
            partner.Child = new TextBlock { Text = "→ " + partnerLabel, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            partner.Visibility = Visibility.Visible;
        }
    }

    private void TileClicked(VocabCard card, bool greek)
    {
        if (_checking) return;
        ResultText.Text = "";
        if (greek)
        {
            if (_pairs.Remove(card)) { _pickedGreek = card; Redraw(); return; } // undo a pair and pick it again
            _pickedGreek = card;
        }
        else
        {
            var owner = _pairs.FirstOrDefault(p => ReferenceEquals(p.Value, card)).Key;
            if (owner != null)
            {
                _pairs.Remove(owner); // undo that pair...
                if (_pickedGreek == null) { Redraw(); return; }
            }                         // ...and, with a Greek word picked, give the meaning to it
            if (_pickedGreek == null) { ResultText.Text = "Pick a Greek word first."; return; }
            _pairs.Remove(_pickedGreek);
            _pairs[_pickedGreek] = card;
            _pickedGreek = null;
        }
        Redraw();
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        if (_pairs.Count != _round.Count || _checking) return;
        _tries++;
        var wrong = _round.Where(c => !ReferenceEquals(_pairs[c], c)).ToList();
        foreach (var c in wrong) _missed.Add(c.Id);

        // Show right in green and wrong in red for a moment.
        _checking = true;
        GoButton.IsEnabled = false;
        foreach (Button tile in _leftTiles)
        {
            if (tile.Tag is not VocabCard c) continue;
            bool ok = ReferenceEquals(_pairs[c], c);
            tile.SetResourceReference(BorderBrushProperty, ok ? "BreakBrush" : "PriorityHigh");
        }
        foreach (Button tile in _rightTiles)
        {
            if (tile.Tag is not VocabCard meaning) continue;
            var owner = _pairs.FirstOrDefault(p => ReferenceEquals(p.Value, meaning)).Key;
            if (owner == null) continue; // an extra nobody chose: nothing to mark
            tile.SetResourceReference(BorderBrushProperty, ReferenceEquals(owner, meaning) ? "BreakBrush" : "PriorityHigh");
        }

        if (wrong.Count == 0)
        {
            ResultText.Text = _missed.Count == 0 ? "All right on the first try. Excellent!" : "All matched. The ones you missed will come up again soon.";
            ResultText.SetResourceReference(TextBlock.ForegroundProperty, "BreakBrush");
            Later(1600, () => Finish(done: true));
        }
        else
        {
            ResultText.Text = wrong.Count == 1 ? "One to fix. Try it again." : $"{wrong.Count} to fix. Try those again.";
            ResultText.SetResourceReference(TextBlock.ForegroundProperty, "PriorityHigh");
            Later(1500, () =>
            {
                _checking = false;
                _round = wrong; // only the missed ones come back
                HintText.Text = "Match the ones you missed.";
                Build();
            });
        }
    }

    private void Finish(bool done)
    {
        if (done)
        {
            // First-try results move words up; any word missed this time drops back.
            foreach (var card in _deck.Cards.Where(c => _seen.Contains(c.Id)).ToList()) _deck.Record(card, !_missed.Contains(card.Id));
            _deck.Save();
            if (_nextRound != null)
            {
                // Reviewing any time: show how it's going and offer another round.
                _roundsDone++;
                _wordsDone += _seen.Count;
                _rightFirst += _seen.Count(id => !_missed.Contains(id));
                SessionText.Text = $"{_roundsDone} round{(_roundsDone == 1 ? "" : "s")}, {_rightFirst} of {_wordsDone} right on the first try.";
                SessionText.Visibility = Visibility.Visible;
                GoRow.Visibility = Visibility.Collapsed;
                ContinuePanel.Visibility = Visibility.Visible;
                NextButton.Focus();
                return;
            }
        }
        var finished = _finished;
        CloseAll();
        finished?.Invoke(done);
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Finish(done: false);

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        var finished = _finished;
        CloseAll();
        finished?.Invoke(true);
    }

    /// <summary>Another round (reviewing any time), with the size you picked.</summary>
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        var words = _nextRound?.Invoke(_size) ?? new List<VocabCard>();
        if (words.Count < 2) { ResultText.Text = "No more words to review right now."; return; }
        _round = words;
        _seen.Clear();
        _missed.Clear();
        foreach (var c in _round) _seen.Add(c.Id);
        _tries = 0;
        _checking = false;
        ResultText.Text = "";
        HintText.Text = _deck.Hint;
        ContinuePanel.Visibility = Visibility.Collapsed;
        GoRow.Visibility = Visibility.Visible;
        Build();
    }

    private void BuildSizeChoices()
    {
        SizeChoices.Children.Clear();
        foreach (int n in new[] { 3, 4, 5, 6, 8, 10 })
        {
            var b = new RadioButton { Content = n.ToString(), GroupName = "vocabsize", IsChecked = n == _size, Margin = new Thickness(0, 0, 4, 0) };
            b.SetResourceReference(StyleProperty, "NavButton");
            b.Checked += (_, _) => _size = n;
            SizeChoices.Children.Add(b);
        }
    }

    private void Later(int ms, Action action)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); action(); };
        t.Start();
    }
}
