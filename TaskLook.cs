using System;
using System.Windows;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// How tasks look in the Tasks tab, the dashboard, the Day plan and sessions (not the floating timer):
///   default: soft gray cards;  compact: the same cards, slimmer and closer together;
///   paper: no cards, just circles on thin ruled lines, like a checklist on paper (on your theme's background).
/// The printed and exported lists follow the same choice.
/// </summary>
public static class TaskLook
{
    public static string Current { get; private set; } = "default";

    public static void Apply(string? look)
    {
        Current = look is "compact" or "paper" ? look : "default";
        var res = Application.Current.Resources;
        Brush Theme(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
        switch (Current)
        {
            case "compact":
                res["TaskRowBg"] = Theme("Panel2");
                res["TaskRowLine"] = Brushes.Transparent;
                res["TaskRowCorner"] = new CornerRadius(6);
                res["TaskRowPadding"] = new Thickness(8, 3, 4, 3);
                res["TaskRowMargin"] = new Thickness(0, 0, 0, 2);
                res["TaskRowLineThickness"] = new Thickness(0);
                break;
            case "paper":
                res["TaskRowBg"] = Brushes.Transparent;
                res["TaskRowLine"] = Theme("Line");
                res["TaskRowCorner"] = new CornerRadius(0);
                res["TaskRowPadding"] = new Thickness(4, 8, 4, 8);
                res["TaskRowMargin"] = new Thickness(0);
                res["TaskRowLineThickness"] = new Thickness(0, 0, 0, 1);
                break;
            default:
                res["TaskRowBg"] = Theme("Panel2");
                res["TaskRowLine"] = Brushes.Transparent;
                res["TaskRowCorner"] = new CornerRadius(8);
                res["TaskRowPadding"] = new Thickness(10, 8, 6, 8);
                res["TaskRowMargin"] = new Thickness(0, 0, 0, 6);
                res["TaskRowLineThickness"] = new Thickness(0);
                break;
        }
    }
}
