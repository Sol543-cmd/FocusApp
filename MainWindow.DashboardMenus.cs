using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FocusApp;

/// <summary>
/// The dashboard's menus: a Tasks title menu (Session, Today, All, Undated) and a small Training menu that opens practice over everything.
/// </summary>
public partial class MainWindow
{
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
