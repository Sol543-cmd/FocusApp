using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// Training off for a day: from the Day plan's Training button (any day, so you can set it ahead) or the Training page.
/// Off: no lessons, no required last lesson time, no Greek, Books, timeline or flashcard pop-ups, and nothing counts as
/// missed. Books off: no lesson, practice as usual. It syncs with the course settings, so it's off on every computer.
/// </summary>
public partial class MainWindow
{
    private static string DayOffLabel(string mode) => mode switch { "off" => "Training off", "books" => "Books off", _ => "Training" };

    /// <summary>The Day plan's Training button: its label shows the day's setting.</summary>
    private void PaintPlanTrainingBtn()
    {
        if (PlanTrainingBtn == null) return;
        string mode = Course.DayOff(_planDate);
        PlanTrainingBtn.Content = DayOffLabel(mode) + " ⌄";
        if (mode.Length > 0) PlanTrainingBtn.SetResourceReference(ForegroundProperty, "BreakBrush");
        else PlanTrainingBtn.ClearValue(ForegroundProperty);
    }

    private void PlanTraining_Click(object sender, RoutedEventArgs e) => DayOffMenu(PlanTrainingBtn, _planDate);

    /// <summary>Training on, off, or books off for a day, or off for a few days in a row.</summary>
    private void DayOffMenu(FrameworkElement target, DateTime day)
    {
        string mode = Course.DayOff(day);
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string header, string tip, string value)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = mode == value, ToolTip = tip };
            item.Click += (_, _) => SetDaysOff(day, day, value);
            menu.Items.Add(item);
        }
        Item("Training on", "Lessons and practice as usual", "");
        Item("Training off for this day", "No lessons, no practice pop-ups, nothing missed", "off");
        Item("Books off, practice on", "Skip the lesson; flashcards, Greek and timeline still come up", "books");
        menu.Items.Add(new Separator());
        var several = new MenuItem { Header = "Training off for…" };
        foreach (var (label, days) in new[] { ("2 days", 2), ("3 days", 3), ("A week", 7), ("2 weeks", 14) })
        {
            var item = new MenuItem { Header = $"{label} (through {day.AddDays(days - 1):ddd MMM d})" };
            item.Click += (_, _) => SetDaysOff(day, day.AddDays(days - 1), "off");
            several.Items.Add(item);
        }
        menu.Items.Add(several);
        menu.IsOpen = true;
    }

    private void SetDaysOff(DateTime from, DateTime to, string mode)
    {
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1)) Course.SetDayOff(d, mode);
        PaintPlanTrainingBtn();
        try { RecalcTimes(); RefreshDashboard(full: true); } catch (Exception ex) { Log.Error("Day off: refresh", ex); }
        if (NavTraining.IsChecked == true) RefreshTraining();
        string when = from == to ? (from == DateTime.Today ? "today" : from.ToString("ddd MMM d")) : $"{from:ddd MMM d} – {to:ddd MMM d}";
        _toast.ShowMessage(mode switch { "off" => $"Training off {when}", "books" => $"Books off {when}", _ => $"Training on {when}" }, "");
    }

    /// <summary>The Training page's line: a banner while today is off (with Turn back on), or a quiet "Take today off".</summary>
    private FrameworkElement DayOffBar()
    {
        string mode = Course.DayOff(DateTime.Today);
        if (mode.Length == 0)
        {
            var link = new Button { Content = "Take today off…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -6, 0, 12), ToolTip = "Turn training off for today (or a few days)" };
            link.SetResourceReference(StyleProperty, "LinkButton");
            link.Click += (_, _) => DayOffMenu(link, DateTime.Today);
            return link;
        }
        var dock = new DockPanel();
        var back = new Button { Content = "Turn back on" };
        back.SetResourceReference(StyleProperty, "LinkButton");
        back.Click += (_, _) => SetDaysOff(DateTime.Today, DateTime.Today, "");
        DockPanel.SetDock(back, Dock.Right);
        dock.Children.Add(back);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 14,
            Text = mode == "off" ? "Training is off today. No lessons or practice will pop up, and nothing counts as missed."
                                 : "Books are off today. No lesson; flashcards, Greek and timeline still come up." };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        dock.Children.Add(text);
        var bar = new Border { Child = dock, Padding = new Thickness(16, 10, 12, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 16) };
        bar.SetResourceReference(Border.BorderBrushProperty, "BreakBrush");
        bar.SetResourceReference(Border.BackgroundProperty, "Panel");
        return bar;
    }
}
