using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace FocusApp;

/// <summary>A Greek lesson's text, opened at the section a quiz question comes from (that section highlighted).</summary>
public sealed class QuizLessonWindow : Window
{
    public QuizLessonWindow(QuizPack pack, string sectionId)
    {
        Title = $"Lesson {pack.Lesson}" + (pack.Title.Length > 0 ? $": {pack.Title}" : "");
        Width = 760; Height = 640; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        var page = new StackPanel { Margin = new Thickness(36, 28, 36, 40), MaxWidth = 680 };
        page.Children.Add(new TextBlock { Text = Title, FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        FrameworkElement? target = null;
        foreach (var s in pack.Sections)
        {
            var block = new StackPanel();
            if (s.Heading.Length > 0) block.Children.Add(new TextBlock { Text = s.Heading, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            foreach (var p in s.Paragraphs) block.Children.Add(new TextBlock { Text = p, FontSize = 15.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), LineHeight = 24 });
            var box = new Border { Child = block, Padding = new Thickness(14, 10, 14, 4), CornerRadius = new CornerRadius(10), Margin = new Thickness(-14, 18, -14, 0) };
            if (s.Id == sectionId) { box.SetResourceReference(Border.BackgroundProperty, "FocusTint"); target = box; }
            page.Children.Add(box);
        }
        var scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = scroll;
        Loaded += (_, _) => target?.BringIntoView(new Rect(0, 0, 10, 200));
    }
}
