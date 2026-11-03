using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace FocusApp;

/// <summary>
/// Settings, Greek practice, Question packs: import a lesson's pack, and choose which lesson the quiz's Learn mode uses
/// (the most recent unless you pick another).
/// </summary>
public sealed class QuestionPacksWindow : Window
{
    private readonly StackPanel _list = new();
    private readonly Func<string> _get;
    private readonly Action<string> _set;

    public QuestionPacksWindow(Func<string> getLesson, Action<string> setLesson)
    {
        _get = getLesson; _set = setLesson;
        Title = "Question packs"; Width = 520; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        var page = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        page.Children.Add(new TextBlock { Text = "Question packs", FontSize = 22, FontWeight = FontWeights.SemiBold });
        var note = new TextBlock { Text = "One pack per Greek lesson. Learn mode uses the lesson you pick here; Review mixes them all.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        page.Children.Add(note);
        var import = new Button { Content = "Import question pack…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 0, 14) };
        import.Click += (_, _) => Import();
        page.Children.Add(import);
        page.Children.Add(_list);
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Fill();
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Question packs (*.quizpack)|*.quizpack|All files|*.*", Title = "Import question packs", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames)
        {
            try { ConceptQuiz.Import(f); }
            catch (Exception ex) { MessageBox.Show(this, $"{System.IO.Path.GetFileName(f)}: {ex.Message}", "Question packs"); }
        }
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        var all = ConceptQuiz.All();
        if (all.Count == 0) { _list.Children.Add(new TextBlock { Text = "No packs yet.", Margin = new Thickness(0, 4, 0, 0) }); return; }
        var latest = new RadioButton { Content = $"Most recent (Lesson {all[^1].Lesson})", GroupName = "lesson", IsChecked = _get().Length == 0 || all.All(p => p.Lesson != _get()), Margin = new Thickness(0, 4, 0, 6) };
        latest.Checked += (_, _) => _set("");
        _list.Children.Add(latest);
        foreach (var p in all)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var remove = new Button { Content = "Remove", Padding = new Thickness(10, 2, 10, 2) };
            string lesson = p.Lesson;
            remove.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Remove Lesson {lesson}'s questions?", "Question packs", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                ConceptQuiz.Remove(lesson);
                if (_get() == lesson) _set("");
                Fill();
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var pick = new RadioButton { Content = $"Lesson {p.Lesson}" + (p.Title.Length > 0 ? $": {p.Title}" : "") + $" · {p.Questions.Count} questions", GroupName = "lesson", IsChecked = _get() == p.Lesson, VerticalAlignment = VerticalAlignment.Center };
            pick.Checked += (_, _) => _set(lesson);
            row.Children.Add(pick);
            _list.Children.Add(row);
        }
    }
}
