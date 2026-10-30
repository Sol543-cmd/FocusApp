using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace FocusApp;

/// <summary>One unfinished task in the end-of-day review, and where you chose to send it.</summary>
public class ReviewChoice
{
    public TaskBase Task { get; init; } = null!;
    public string Where { get; set; } = "tomorrow"; // tomorrow, day, bin, drop, resolve
    public DateTime? Day { get; set; }
}

/// <summary>End-of-day review: decide where each unfinished task goes.</summary>
public partial class ReviewWindow : Window
{
    private readonly List<(ReviewChoice Choice, RadioButton Tomorrow, RadioButton Day, TextBox DayBox, RadioButton Bin, RadioButton Drop, RadioButton Resolve)> _rows = new();

    public List<ReviewChoice>? Result { get; private set; }

    public ReviewWindow(IEnumerable<TaskBase> unfinished, string summary)
    {
        InitializeComponent();
        SummaryText.Text = summary;
        int n = 0;
        foreach (var task in unfinished)
        {
            n++;
            var choice = new ReviewChoice { Task = task };
            string group = "review" + n;
            var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8) };
            card.SetResourceReference(Border.BackgroundProperty, "Panel2");
            var stack = new StackPanel();
            var title = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15 };
            ScriptureLinks.Fill(title, task.Text); // Scripture references open in Logos
            stack.Children.Add(title);
            if (task.HasDetails)
            {
                var details = new TextBlock { Text = task.Details, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                details.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                stack.Children.Add(details);
            }
            var options = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            RadioButton Option(string text, bool isChecked)
            {
                var r = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked, Margin = new Thickness(0, 0, 4, 0) };
                r.SetResourceReference(StyleProperty, "NavButton");
                options.Children.Add(r);
                return r;
            }
            var resolve = Option("Resolve", false);
            resolve.ToolTip = "Done today, just not ticked off: it goes to Resolved";
            var tomorrow = Option("Tomorrow", true);
            var day = Option("Another day", false);
            var dayBox = new TextBox { Width = 110, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Like: monday, next week, 10/20" };
            dayBox.GotFocus += (_, _) => day.IsChecked = true;
            DatePopup.Attach(dayBox, () => day.IsChecked = true);
            options.Children.Add(dayBox);
            var bin = Option("Task list", false);
            var drop = Option("Drop it", false);
            stack.Children.Add(options);
            card.Child = stack;
            Rows.Children.Add(card);
            _rows.Add((choice, tomorrow, day, dayBox, bin, drop, resolve));
        }
        EmptyText.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (choice, tomorrow, day, dayBox, bin, drop, resolve) in _rows)
        {
            if (day.IsChecked == true)
            {
                var date = TaskParser.ParseDate(dayBox.Text);
                if (date == null || date <= DateTime.Today)
                {
                    MessageBox.Show(this, $"Pick a future day for \"{choice.Task.Text}\", like monday or 10/20.", "Review",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    dayBox.Focus();
                    return;
                }
                choice.Where = "day";
                choice.Day = date;
            }
            else choice.Where = resolve.IsChecked == true ? "resolve" : bin.IsChecked == true ? "bin" : drop.IsChecked == true ? "drop" : "tomorrow";
        }
        var result = new List<ReviewChoice>();
        foreach (var row in _rows) result.Add(row.Choice);
        Result = result;
        DialogResult = true;
    }

    private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
