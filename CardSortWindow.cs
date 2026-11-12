using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// The card sort: once, go through the memory cards from lessons you've done and choose for each: Keep (a core card, kept
/// for the long term), Quiz only (out of the daily review; it still comes up when you review its lesson) or Retire (gone).
/// Each card starts on a suggestion: dates are on the timeline already (Retire), short names and numbers become Quiz only.
/// </summary>
public class CardSortWindow : Window
{
    private readonly List<(string Book, PackQuestion Card, Func<string> Choice)> _rows = new();

    public CardSortWindow()
    {
        Title = "Focus · Sort your cards";
        Width = 940; Height = 760; MinWidth = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        FontSize = 14;

        var list = new StackPanel { Margin = new Thickness(28, 6, 28, 20) };
        var was = Course.Current.Id;
        int total = 0;
        foreach (var book in Course.Config.Courses.ToList())
        {
            try
            {
                Course.UseBookQuietly(book.Id);
                var sorts = Course.CardSorts();
                var hidden = Course.Hidden();
                var done = Course.DoneLessons();
                var cards = Course.Lessons().Where(l => done.Contains(l.Index)).SelectMany(l => l.Lesson.Cards)
                                  .Where(c => !sorts.ContainsKey(c.Id) && !hidden.Contains(c.Id)).ToList();
                if (cards.Count == 0) continue;
                var head = new TextBlock { Text = $"{book.Title} · {cards.Count} cards", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) };
                list.Children.Add(head);
                foreach (var c in cards) { list.Children.Add(Row(book.Id, c)); total++; }
            }
            catch (Exception ex) { Log.Error("Card sort " + book.Id, ex); }
        }
        Course.UseBookQuietly(was);

        var top = new StackPanel { Margin = new Thickness(28, 22, 28, 6) };
        top.Children.Add(new TextBlock { Text = "Sort your cards", FontSize = 24, FontWeight = FontWeights.SemiBold });
        var intro = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
                                    Text = total == 0 ? "Nothing to sort: every card from your lessons has a place." :
                                           "Keep only what you'd want to know a year from now. Quiz only takes a card out of the daily review (it still comes up when you review its lesson). Retire removes it. Each card starts on a suggestion; change any you disagree with, then Save." };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        top.Children.Add(intro);
        DockPanel.SetDock(top, Dock.Top);

        var footer = new DockPanel { Margin = new Thickness(28, 10, 28, 16) };
        var save = new Button { Content = total == 0 ? "Close" : $"Save {total} choices", Padding = new Thickness(20, 8, 20, 8) };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) => { Save(); Course.Config.CardSortDone = true; Course.SaveConfig(); Close(); };
        var later = new Button { Content = "Later", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 10, 0), Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible };
        later.Click += (_, _) => Close();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(later); right.Children.Add(save);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);
        var counts = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = Summary() };
        counts.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        footer.Children.Add(counts);
        _summary = counts;
        DockPanel.SetDock(footer, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private readonly TextBlock? _summary;

    private string Summary()
    {
        var c = _rows.GroupBy(r => r.Choice()).ToDictionary(g => g.Key, g => g.Count());
        return $"Keep {c.GetValueOrDefault("keep")} · Quiz only {c.GetValueOrDefault("quiz")} · Retire {c.GetValueOrDefault("retire")}";
    }

    private Border Row(string bookId, PackQuestion card)
    {
        string choice = Course.SuggestSort(card);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var all = new List<(string Key, Button B)>();
        void Paint()
        {
            foreach (var (k, b) in all)
            {
                if (k == choice) b.SetResourceReference(StyleProperty, "PrimaryButton");
                else b.ClearValue(StyleProperty);
            }
            if (_summary != null) _summary.Text = Summary();
        }
        foreach (var (key, label) in new[] { ("keep", "Keep"), ("quiz", "Quiz only"), ("retire", "Retire") })
        {
            var b = new Button { Content = label, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0), MinWidth = 70 };
            b.Click += (_, _) => { choice = key; Paint(); };
            all.Add((key, b));
            buttons.Children.Add(b);
        }
        Paint();
        _rows.Add((bookId, card, () => choice));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = card.Front.Length > 0 ? card.Front : card.Prompt, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        var back = new TextBlock { Text = card.Back, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 2, 0, 0) };
        back.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(back);
        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        var border = new Border { Child = row, Padding = new Thickness(0, 10, 0, 10), BorderThickness = new Thickness(0, 0, 0, 1) };
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        return border;
    }

    private void Save()
    {
        Course.AddMany(_rows.Select(r => new TrainingEvent { Kind = "cardSort", Question = r.Card.Id, Note = r.Choice(), Lesson = -1, CourseId = r.Book }).ToList());
    }
}
