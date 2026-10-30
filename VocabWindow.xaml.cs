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
        SourceInitialized += (_, _) => { FitToScreen(); TilesScroll.MaxHeight = Math.Max(300, Height - 330); };
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

    private Button MakeTile(string text, bool greek, VocabCard card)
    {
        bool compact = _round.Count > 6; // bigger rounds get slimmer tiles so all ten fit
        var b = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, compact ? 6 : 10),
                             Padding = compact ? new Thickness(14, 7, 14, 7) : new Thickness(16, 12, 16, 12),
                             Cursor = Cursors.Hand, Tag = card, BorderThickness = new Thickness(1.5),
                             FontSize = greek ? (_deck.LargeLeft ? (compact ? 24 : 28) : (compact ? 18 : 21)) : (compact ? 14 : 15) };
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (greek) label.FontFamily = new FontFamily("Segoe UI, Palatino Linotype, Times New Roman");
        b.Content = label;
        b.SetResourceReference(StyleProperty, "Tile");
        b.SetResourceReference(BackgroundProperty, "Panel2");
        b.SetResourceReference(BorderBrushProperty, "Panel2");
        b.Click += (_, _) => TileClicked(card, greek);
        return b;
    }

    private void Redraw()
    {
        GreekColumn.Children.Clear();
        EnglishColumn.Children.Clear();
        var order = _round.ToList();
        foreach (var card in order)
        {
            var tile = MakeTile(card.Greek, true, card);
            if (_pairs.TryGetValue(card, out _)) Mark(tile, order.IndexOf(card));
            else if (ReferenceEquals(card, _pickedGreek))
            {
                tile.BorderThickness = new Thickness(2.5);
                tile.SetResourceReference(BorderBrushProperty, "FocusBrush");
                tile.SetResourceReference(BackgroundProperty, "FocusTint");
                if (tile.Content is TextBlock picked) picked.FontWeight = FontWeights.SemiBold;
            }
            GreekColumn.Children.Add(tile);
        }
        foreach (var meaning in _meanings)
        {
            var tile = MakeTile(meaning.English, false, meaning);
            var owner = _pairs.FirstOrDefault(p => ReferenceEquals(p.Value, meaning)).Key;
            if (owner != null) Mark(tile, order.IndexOf(owner));
            EnglishColumn.Children.Add(tile);
        }
        GoButton.IsEnabled = _pairs.Count == _round.Count && !_checking;
        GoButton.Content = _tries == 0 ? "Go" : "Check again";
    }

    /// <summary>Colors a matched tile and adds a small number so you can see which go together.</summary>
    private static void Mark(Button tile, int pairIndex)
    {
        var color = PairColors[pairIndex % PairColors.Length];
        tile.BorderBrush = new SolidColorBrush(color);
        tile.Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
        if (tile.Content is TextBlock text)
        {
            var row = new DockPanel();
            var badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 10, 0),
                                     Child = new TextBlock { Text = (pairIndex + 1).ToString(), Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold,
                                                             HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            DockPanel.SetDock(badge, Dock.Left);
            tile.Content = null;
            row.Children.Add(badge);
            row.Children.Add(text);
            tile.Content = row;
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
        foreach (Button tile in GreekColumn.Children)
        {
            if (tile.Tag is not VocabCard c) continue;
            bool ok = ReferenceEquals(_pairs[c], c);
            tile.SetResourceReference(BorderBrushProperty, ok ? "BreakBrush" : "PriorityHigh");
        }
        foreach (Button tile in EnglishColumn.Children)
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
