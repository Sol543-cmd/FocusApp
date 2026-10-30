using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// Find words in the built-in list (or your bank) by Greek or English; add them to your review bank as
/// they are, or edit the Greek or meaning first. "My words" shows your bank with how well you know each word.
/// </summary>
public partial class VocabManagerWindow : Window
{
    private VocabCard? _editingCard;          // editing a word already in your bank
    private readonly HashSet<string> _ticked = new();   // ticked rows: "id:<card id>" or "src:<built-in Greek>"
    private string? _topKey;                             // the first result, for Enter
    private string _topText = "";
    private string _editingSource = "";       // or a built-in word being edited before adding

    public VocabManagerWindow()
    {
        InitializeComponent();
        foreach (var (box, hint) in new[] { (SearchBox, SearchHint), (GreekBox, GreekHint), (EnglishBox, EnglishHint) })
            box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EnglishBox.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveWord_Click(this, new RoutedEventArgs()); e.Handled = true; } };
        Loaded += (_, _) => { SearchBox.Focus(); Refresh(); };
    }

    private void Tab_Checked(object sender, RoutedEventArgs e) { if (Results != null) { _ticked.Clear(); Refresh(); } }

    /// <summary>Enter adds the top match and clears the box, ready for the next word.</summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || MineTab.IsChecked == true) return;
        e.Handled = true;
        if (_topKey == null) { CountText.Text = SearchBox.Text.Trim().Length == 0 ? "Type a word first." : "Nothing to add: the top match is already in My words."; return; }
        AddKey(_topKey);
        Vocab.Save();
        string added = _topText;
        SearchBox.Text = "";
        CountText.Text = $"Added {added}. Type the next word.";
        SearchBox.Focus();
    }

    /// <summary>Adds one row's word to your review (a saved word keeps its progress).</summary>
    private void AddKey(string key)
    {
        if (key.StartsWith("id:") && Vocab.Find(key[3..]) is VocabCard saved) { saved.InReview = true; if (!saved.Retired && saved.Due > DateTime.Now.AddDays(1)) saved.Due = DateTime.Now; }
        else if (key.StartsWith("src:"))
        {
            string src = key[4..];
            string lemma = Vocab.LemmaKey(src);
            if (Vocab.Bank.Any(c => Vocab.LemmaKey(c.Greek) == lemma || (c.Source.Length > 0 && Vocab.LemmaKey(c.Source) == lemma))) return;
            if (Vocab.BuiltInFor(src) is Vocab.BuiltInWord w) Vocab.Bank.Add(new VocabCard { Greek = w.Greek, English = w.English, Source = w.Greek, Due = DateTime.Now });
        }
    }

    private void BulkAdd_Click(object sender, RoutedEventArgs e)
    {
        foreach (var key in _ticked.ToList()) AddKey(key);
        Vocab.Save();
        CountText.Text = $"Added {_ticked.Count} word{(_ticked.Count == 1 ? "" : "s")}.";
        _ticked.Clear();
        Refresh(keepCount: true);
    }

    private void BulkRemove_Click(object sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var key in _ticked.Where(k => k.StartsWith("id:")).ToList())
            if (Vocab.Find(key[3..]) is VocabCard c && c.InReview) { c.InReview = false; n++; }
        Vocab.Save();
        _ticked.Clear();
        Refresh();
        CountText.Text = $"Removed {n} word{(n == 1 ? "" : "s")} from your review (they're back in Find words).";
    }

    private void BulkClear_Click(object sender, RoutedEventArgs e) { _ticked.Clear(); Refresh(); }

    private void UpdateBulkBar()
    {
        BulkBar.Visibility = _ticked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BulkText.Text = $"{_ticked.Count} selected";
        bool mine = MineTab.IsChecked == true;
        BulkAddBtn.Visibility = mine ? Visibility.Collapsed : Visibility.Visible;
        BulkRemoveBtn.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Refresh() => Refresh(keepCount: false);

    private void Refresh(bool keepCount)
    {
        string keptCount = CountText.Text;
        Results.Children.Clear();
        _topKey = null;
        UpdateBulkBar();
        string q = SearchBox.Text.Trim();
        if (MineTab.IsChecked == true)
        {
            var mine = Vocab.Active
                .Where(c => q.Length == 0 || Vocab.Plain(c.Greek).Contains(Vocab.Plain(q)) || c.English.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(c => c.Retired).ThenBy(c => c.Level).ThenBy(c => c.Greek).ToList();
            var active = Vocab.Active.ToList();
            CountText.Text = $"{active.Count} words in your review: {active.Count(c => !c.Retired)} being learned, {active.Count(c => c.Retired)} retired.";
            foreach (var c in mine) Results.Children.Add(Row(c.Greek, c.English, c, c.Source));
            if (mine.Count == 0) Results.Children.Add(Empty(q.Length == 0 ? "No words yet. Find some in Find words, or add your own below." : "No matches in your words."));
            return;
        }
        if (q.Length == 0)
        {
            CountText.Text = $"All {Vocab.BuiltIn.Count:N0} words of the Greek New Testament, most frequent first. Type to search; here are the first 40.";
            if (keepCount) CountText.Text = keptCount;
            foreach (var w in Vocab.BuiltIn.Take(40))
            {
                string g = w.Greek, e = w.English;
                var mineCopy = Vocab.Bank.FirstOrDefault(c => Vocab.LemmaKey(c.Greek) == w.Key || (c.Source.Length > 0 && Vocab.LemmaKey(c.Source) == w.Key));
                Results.Children.Add(Row(g, e, mineCopy?.InReview == true ? mineCopy : null, g, mineCopy?.InReview == false ? mineCopy : null));
            }
            return;
        }
        var found = Vocab.Search(q);
        CountText.Text = found.Count == 0 ? "No matches. You can add it as your own word below." : $"{found.Count} match{(found.Count == 1 ? "" : "es")}";
        foreach (var r in found) Results.Children.Add(Row(r.Greek, r.English, r.InBank, r.Source, r.Saved));
        var top = found.FirstOrDefault();
        if (top != null && top.InBank == null) { _topKey = top.Saved != null ? "id:" + top.Saved.Id : "src:" + top.Source; _topText = top.Greek; }
        if (keepCount) CountText.Text = keptCount;
    }

    private FrameworkElement Empty(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(4, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    /// <summary>One word: Greek and meaning, with Add / Edit (or, if it's yours, its level, Edit and Remove).</summary>
    private FrameworkElement Row(string greek, string english, VocabCard? inBank, string source, VocabCard? saved = null)
    {
        var dock = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Right);
        Button Small(string text, Action click, bool primary = false)
        {
            var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
            if (primary) b.SetResourceReference(StyleProperty, "PrimaryButton");
            b.Click += (_, _) => click();
            return b;
        }
        if (inBank == null && saved != null)
        {
            // A word you removed earlier: adding it back keeps its progress.
            string id = saved.Id;
            var note = new TextBlock { Text = saved.Right + saved.Wrong > 0 ? saved.LevelText : "", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            buttons.Children.Add(note);
            buttons.Children.Add(Small("Edit", () => { if (Vocab.Find(id) is VocabCard c) StartEdit(c, c.Greek, c.English, c.Source, addBack: true); }));
            buttons.Children.Add(Small("Add", () => { Vocab.AddBack(id); Refresh(); }, primary: true));
        }
        else if (inBank == null)
        {
            buttons.Children.Add(Small("Edit", () => StartEdit(null, greek, english, source)));
            buttons.Children.Add(Small("Add", () => { Vocab.Add(greek, english, source); Refresh(); }, primary: true));
        }
        else
        {
            var level = new TextBlock { Text = inBank.LevelText, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            level.SetResourceReference(TextBlock.ForegroundProperty, inBank.Retired ? "BreakBrush" : "Muted");
            buttons.Children.Add(level);
            // Work by ID, so it still works if your words were refreshed from OneDrive meanwhile.
            string id = inBank.Id;
            buttons.Children.Add(Small("Edit", () => { if (Vocab.Find(id) is VocabCard c) StartEdit(c, c.Greek, c.English, c.Source); }));
            buttons.Children.Add(Small("Remove", () => { Vocab.RemoveFromReview(id); Refresh(); }));
        }
        dock.Children.Add(buttons);
        string key = inBank != null ? "id:" + inBank.Id : saved != null ? "id:" + saved.Id : "src:" + source;
        var tick = new CheckBox { IsChecked = _ticked.Contains(key), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                                  ToolTip = "Select (to add or remove several at once)" };
        tick.SetResourceReference(StyleProperty, "TaskCheck");
        tick.Click += (_, _) => { if (tick.IsChecked == true) _ticked.Add(key); else _ticked.Remove(key); UpdateBulkBar(); };
        DockPanel.SetDock(tick, Dock.Left);
        dock.Children.Add(tick);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var head = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        head.Inlines.Add(new System.Windows.Documents.Run(greek));
        if (Vocab.BuiltInFor(source.Length > 0 ? source : greek) is Vocab.BuiltInWord bw && bw.Count > 0)
        {
            var times = new System.Windows.Documents.Run($"   {bw.Count:N0}×") { FontSize = 12, FontWeight = FontWeights.Normal };
            times.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
            head.Inlines.Add(times);
        }
        text.Children.Add(head);
        var meaning = new TextBlock { Text = english, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
        meaning.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(meaning);
        // The lexicon's fuller definition, for a more rounded picture of the word.
        if (Vocab.BuiltInFor(source.Length > 0 ? source : greek) is Vocab.BuiltInWord full && full.Full.Length > 0 &&
            !full.Full.TrimEnd('.').Equals(english, StringComparison.CurrentCultureIgnoreCase))
        {
            var more = new TextBlock { Text = full.Full, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), FontStyle = FontStyles.Italic };
            more.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            text.Children.Add(more);
        }
        dock.Children.Add(text);
        var row = new Border { Child = dock, Padding = new Thickness(10, 9, 6, 9), BorderThickness = new Thickness(0, 0, 0, 1) };
        row.SetResourceReference(Border.BorderBrushProperty, "Line");
        return row;
    }

    private bool _editAddsBack;

    private void StartEdit(VocabCard? card, string greek, string english, string source, bool addBack = false)
    {
        _editAddsBack = addBack;
        _editingCard = card;
        _editingSource = source;
        GreekBox.Text = greek;
        EnglishBox.Text = english;
        EditTitle.Text = card != null ? "Edit word" : "Edit, then add to your review bank";
        SaveWordBtn.Content = card != null ? "Save" : "Add";
        CancelEditBtn.Visibility = Visibility.Visible;
        EditNote.Text = card != null ? "Your progress on this word is kept." : "The edited word joins your review bank.";
        if (addBack) { EditTitle.Text = "Edit, then add back to your review"; SaveWordBtn.Content = "Add"; }
        EnglishBox.Focus();
        EnglishBox.SelectAll();
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => ResetEdit();

    private void ResetEdit()
    {
        _editingCard = null;
        _editAddsBack = false;
        _editingSource = "";
        GreekBox.Text = EnglishBox.Text = "";
        EditTitle.Text = "Add your own word";
        SaveWordBtn.Content = "Add";
        CancelEditBtn.Visibility = Visibility.Collapsed;
        EditNote.Text = "Your own words and edited words join your review bank straight away.";
    }

    private void SaveWord_Click(object sender, RoutedEventArgs e)
    {
        string g = GreekBox.Text.Trim(), en = EnglishBox.Text.Trim();
        if (g.Length == 0 || en.Length == 0) { EditNote.Text = "Enter both the Greek word and its meaning."; return; }
        if (_editingCard != null)
        {
            // Look the word up again by ID (your words may have been refreshed from OneDrive meanwhile).
            if (Vocab.Find(_editingCard.Id) is VocabCard current)
            {
                current.Greek = g;
                current.English = en;
                if (_editAddsBack) current.InReview = true;
                Vocab.Save();
            }
        }
        else
        {
            if (Vocab.Bank.FirstOrDefault(c => Vocab.Plain(c.Greek) == Vocab.Plain(g)) is VocabCard existing)
            {
                if (existing.InReview) { EditNote.Text = $"{g} is already in My words. Find it there to edit it."; return; }
                // Saved from before: bring it back with your new meaning (and its progress).
                existing.English = en;
                Vocab.AddBack(existing.Id);
                ResetEdit();
                Refresh();
                return;
            }
            Vocab.Add(g, en, _editingSource);
        }
        ResetEdit();
        Refresh();
    }
}
