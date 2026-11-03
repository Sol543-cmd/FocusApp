using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FocusApp;

/// <summary>
/// The dashboard's simpler top: a slim thermometer under the session heading (in place of the ring and its numbers),
/// a Tasks title menu (Session, Today, All, Undated) and a small Training menu that opens practice over everything.
/// </summary>
public partial class MainWindow
{
    /// <summary>Fills the thermometer: done (0 to 1), the session's start and end, and the time left beside the heading.</summary>
    private void SetThermometer(double done, DateTime? start, DateTime? end, TimeSpan? left)
    {
        if (DashThermoTrack == null) return;
        done = Math.Clamp(done, 0, 1);
        double width = DashThermoTrack.ActualWidth;
        DashThermoFill.Width = Math.Max(0, width * done);
        DashThermoKnob.Margin = new Thickness(Math.Max(0, width * done - 8), 0, 0, 0);
        DashThermoKnob.Visibility = start == null ? Visibility.Collapsed : Visibility.Visible;
        DashThermoStart.Text = start is DateTime s ? s.ToString("t") : "";
        DashThermoEnd.Text = end is DateTime e ? "ends " + e.ToString("t") : "";
        DashTimeLeft.Text = left is TimeSpan l ? (l.TotalHours >= 1 ? l.ToString(@"h\:mm\:ss") : l.ToString(@"m\:ss")) : "";
    }

    private void DashTasksMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = DashTasksMenuBtn, Placement = PlacementMode.Bottom };
        foreach (var (label, radio) in new[] { ("Session", DashTasksSession), ("Today", DashTasksToday), ("All", DashTasksAll), ("Undated", DashTasksUndated) })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = radio.IsChecked == true };
            var r = radio;
            item.Click += (_, _) => r.IsChecked = true; // the list follows, exactly as the old tabs did
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void DashTrainingMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = DashTrainingBtn, Placement = PlacementMode.Bottom };
        void Add(string label, Action run)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        Add("Greek Concepts", () => StartConceptQuiz(fromMenu: true));
        Add("Greek Vocab", () => DashGreek_Click(this, new RoutedEventArgs()));
        Add("Greek Alphabet", () => DashAlpha_Click(this, new RoutedEventArgs()));
        Add("Timeline", StartTimelinePractice);
        Add("Book Concepts", StartAllBooks);
        menu.IsOpen = true;
    }
}
