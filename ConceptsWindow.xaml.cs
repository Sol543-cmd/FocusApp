using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace FocusApp;

/// <summary>Entering and looking after your Concepts: questions and answers from your reading.</summary>
public partial class ConceptsWindow : Window
{
    private string? _editingId;
    private readonly HashSet<string> _ticked = new();

    public ConceptsWindow()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => { Refresh(); QuestionBox.Focus(); };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        Results.Children.Clear();
        string q = SearchBox.Text.Trim();
        var cards = Concepts.Cards
            .Where(c => q.Length == 0 || c.Greek.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                        c.English.Contains(q, StringComparison.CurrentCultureIgnoreCase) || c.Source.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(c => c.Source).ThenBy(c => c.Added).ToList();
        CountText.Text = Concepts.Cards.Count == 0 ? "No questions yet. Add your first one below." :
            q.Length == 0 ? $"{Concepts.Cards.Count} question{(Concepts.Cards.Count == 1 ? "" : "s")}." : $"{cards.Count} of {Concepts.Cards.Count} match.";
        foreach (var c in cards) Results.Children.Add(Row(c));
        _ticked.RemoveWhere(id => Concepts.Cards.All(c => c.Id != id));
        UpdateRemoveButton();
    }

    private FrameworkElement Row(VocabCard c)
    {
        string id = c.Id;
        var dock = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Right);
        var edit = new Button { Content = "Edit", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
        edit.Click += (_, _) => StartEdit(id);
        var remove = new Button { Content = "Remove", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
        remove.Click += (_, _) => { Concepts.Remove(id); if (_editingId == id) ResetEdit(); Refresh(); };
        buttons.Children.Add(edit);
        buttons.Children.Add(remove);
        dock.Children.Add(buttons);
        var tick = new CheckBox { IsChecked = _ticked.Contains(id), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        tick.SetResourceReference(StyleProperty, "TaskCheck");
        tick.Click += (_, _) => { if (tick.IsChecked == true) _ticked.Add(id); else _ticked.Remove(id); UpdateRemoveButton(); };
        DockPanel.SetDock(tick, Dock.Left);
        dock.Children.Add(tick);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = c.Greek, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var answer = new TextBlock { Text = c.English, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        answer.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(answer);
        if (c.Source.Length > 0 || c.Right + c.Wrong > 0)
        {
            var meta = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0),
                Text = string.Join("   ", new[] { c.Source, c.Right + c.Wrong > 0 ? $"{c.Right} right, {c.Wrong} missed" : "" }.Where(s => s.Length > 0)) };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            text.Children.Add(meta);
        }
        dock.Children.Add(text);
        var row = new Border { Child = dock, Padding = new Thickness(8, 9, 6, 9), BorderThickness = new Thickness(0, 0, 0, 1) };
        row.SetResourceReference(Border.BorderBrushProperty, "Line");
        return row;
    }

    private void UpdateRemoveButton()
    {
        RemoveSelectedBtn.Visibility = _ticked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveSelectedBtn.Content = $"Remove {_ticked.Count} selected";
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Remove {_ticked.Count} question{(_ticked.Count == 1 ? "" : "s")}?", "Concepts", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Concepts.Cards.RemoveAll(c => _ticked.Contains(c.Id));
        Concepts.SaveNow();
        _ticked.Clear();
        ResetEdit();
        Refresh();
    }

    private void StartEdit(string id)
    {
        if (Concepts.Cards.FirstOrDefault(c => c.Id == id) is not VocabCard c) return;
        ShowSingle();
        _editingId = id;
        QuestionBox.Text = c.Greek;
        AnswerBox.Text = c.English;
        ChapterBox.Text = c.Source;
        EditTitle.Text = "Edit question";
        SaveBtn.Content = "Save";
        CancelEditBtn.Visibility = Visibility.Visible;
        NoteText.Text = "Your progress on this question is kept.";
        QuestionBox.Focus();
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => ResetEdit();

    private void ResetEdit()
    {
        _editingId = null;
        QuestionBox.Text = AnswerBox.Text = "";
        EditTitle.Text = "Add a question";
        SaveBtn.Content = "Add";
        CancelEditBtn.Visibility = Visibility.Collapsed;
        NoteText.Text = "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string q = QuestionBox.Text.Trim(), a = AnswerBox.Text.Trim(), ch = ChapterBox.Text.Trim();
        if (q.Length == 0 || a.Length == 0) { NoteText.Text = "Enter both the question and its answer."; return; }
        if (_editingId != null && Concepts.Cards.FirstOrDefault(c => c.Id == _editingId) is VocabCard card)
        {
            card.Greek = q; card.English = a; card.Source = ch;
            Concepts.SaveNow();
        }
        else Concepts.Add(q, a, ch);
        string chapterKept = ch;
        ResetEdit();
        ChapterBox.Text = chapterKept; // keep the chapter for the next one
        NoteText.Text = "Added. Type the next question.";
        Refresh();
        QuestionBox.Focus();
    }

    // ---------- Paste a list ----------

    private void PasteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (PasteEntry.Visibility == Visibility.Visible) ShowSingle();
        else
        {
            PasteEntry.Visibility = Visibility.Visible;
            SingleEntry.Visibility = Visibility.Collapsed;
            PasteToggle.Content = "Add one at a time";
            EditTitle.Text = "Paste a list";
            NoteText.Text = "";
            PasteBox.Focus();
        }
    }

    private void ShowSingle()
    {
        PasteEntry.Visibility = Visibility.Collapsed;
        SingleEntry.Visibility = Visibility.Visible;
        PasteToggle.Content = "Paste a list";
        EditTitle.Text = _editingId == null ? "Add a question" : "Edit question";
    }

    /// <summary>Each line: question, then = or a tab, then the answer. Lines that don't fit are left in the box.</summary>
    private void PasteAdd_Click(object sender, RoutedEventArgs e)
    {
        var (pairs, left) = Concepts.ParseList(PasteBox.Text);
        string ch = PasteChapterBox.Text.Trim();
        foreach (var (q, a) in pairs) Concepts.Cards.Add(new VocabCard { Greek = q, English = a, Source = ch, Due = DateTime.Now });
        if (pairs.Count > 0) Concepts.SaveNow();
        PasteBox.Text = string.Join(Environment.NewLine, left);
        NoteText.Text = (pairs.Count == 1 ? "Added 1 question." : $"Added {pairs.Count} questions.") +
                        (left.Count > 0 ? $" {left.Count} line{(left.Count == 1 ? "" : "s")} didn't have an = or a tab, so they're still in the box." : "");
        Refresh();
    }
}
