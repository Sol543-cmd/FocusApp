using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// How calendar events look in the Calendar, the Day plan and the dashboard: each calendar in its own color (a solid
/// stripe and a clear tint, readable in light and dark mode), the time under the title when there's room, and a Join
/// button on online meetings (Teams, Zoom, Meet, Webex). Clicking an event shows its details: time, place, calendar,
/// Join, and Add to my day.
/// </summary>
public static class CalendarLook
{
    public static Color ColorOf(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Color.FromRgb(0x3B, 0x82, 0xD6); }
    }

    public static Brush Tint(string hex, double alpha)
    {
        var c = ColorOf(hex);
        var b = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>A small "Join" button that opens the meeting link (and doesn't start a drag or a click on the card).</summary>
    public static Border JoinPill(string url, string name, double font = 11)
    {
        var text = new TextBlock { Text = "Join", FontSize = font, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        var pill = new Border
        {
            Child = text, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1, 8, 2), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 2, 2, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x46, 0x4E, 0xB8)), ToolTip = $"Join {name}"
        };
        if (name == "Teams") pill.Background = new SolidColorBrush(Color.FromRgb(0x50, 0x59, 0xC9)); // Teams purple
        pill.MouseLeftButtonDown += (_, e) => e.Handled = true;
        pill.MouseLeftButtonUp += (_, e) => { e.Handled = true; CalendarService.Join(url); };
        return pill;
    }

    /// <summary>A calendar event as a card: its calendar's color, the title (bold), the time and place when there's room.</summary>
    public static Border EventCard(CalendarEvent ev, double width, double height, double font, Action? addToDay)
    {
        string hex = CalendarService.ColorOf(ev);
        var stack = new StackPanel();
        var title = new TextBlock
        {
            Text = ev.Busy ? ev.Title : ev.Title + " (free)", FontSize = font, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Visibility = height < 13 ? Visibility.Collapsed : Visibility.Visible
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        stack.Children.Add(title);
        if (height >= font * 2.9)
        {
            var when = new TextBlock
            {
                Text = $"{ev.Start:t} – {ev.End:t}" + (ev.Location.Length > 0 && !ev.Location.StartsWith("http") ? " · " + ev.Location : ""),
                FontSize = font - 1, TextTrimming = TextTrimming.CharacterEllipsis
            };
            when.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            when.Opacity = 0.8;
            stack.Children.Add(when);
        }
        var layers = new Grid();
        layers.Children.Add(stack);
        if (ev.JoinUrl.Length > 0 && height >= 16 && width >= 90)
        {
            var pill = JoinPill(ev.JoinUrl, ev.JoinName, font - 1.5);
            layers.Children.Add(pill);
            stack.Margin = new Thickness(0, 0, 44, 0);
        }
        var card = new Border
        {
            Width = Math.Max(4, width), Height = height, CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(4, 0, 0, 0), Padding = new Thickness(6, height < 22 ? 0 : 3, 4, 0),
            Child = layers, ClipToBounds = true, Cursor = Cursors.Hand,
            Background = Tint(hex, ev.Busy ? 0.34 : 0.14), BorderBrush = new SolidColorBrush(ColorOf(hex)),
            ToolTip = Details(ev)
        };
        // With an owner that can add it to the day, the owner shows the menu (with these details); otherwise the details here.
        card.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (addToDay != null) addToDay(); else ShowDetails(ev, card); };
        return card;
    }

    public static string Details(CalendarEvent ev)
    {
        string when = ev.AllDay ? $"{ev.Start:dddd, MMMM d} (all day)" : $"{ev.Start:dddd, MMMM d}, {ev.Start:t} – {ev.End:t}";
        string cal = CalendarService.SourceOf(ev)?.Name ?? ev.Source;
        return $"{ev.Title}\n{when}" + (ev.Location.Length > 0 ? $"\n{ev.Location}" : "") + (cal.Length > 0 ? $"\nCalendar: {cal}" : "") +
               (ev.Busy ? "" : "\nShown as free");
    }

    /// <summary>The event's details under it, with Join (online meetings) and Add to my day.</summary>
    public static void ShowDetails(CalendarEvent ev, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        AddDetails(menu, ev);
        if (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
        menu.IsOpen = true;
    }

    /// <summary>The event's details, then Join and Copy meeting link (online meetings), then a separator.</summary>
    public static void AddDetails(ContextMenu menu, CalendarEvent ev)
    {
        var head = new TextBlock { Text = Details(ev), TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Margin = new Thickness(0, 2, 0, 4) };
        menu.Items.Add(new MenuItem { Header = head, IsEnabled = false, StaysOpenOnClick = true });
        menu.Items.Add(new Separator());
        if (ev.JoinUrl.Length > 0)
        {
            var join = new MenuItem { Header = $"Join {ev.JoinName} meeting", FontWeight = FontWeights.SemiBold };
            join.Click += (_, _) => CalendarService.Join(ev.JoinUrl);
            menu.Items.Add(join);
            var copy = new MenuItem { Header = "Copy meeting link" };
            copy.Click += (_, _) => { try { Clipboard.SetText(ev.JoinUrl); } catch { } };
            menu.Items.Add(copy);
            menu.Items.Add(new Separator());
        }
    }
}
