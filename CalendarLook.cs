using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>An all-day event as a slim bar in its calendar's color (click for details).</summary>
    public static Border AllDayChip(CalendarEvent ev, double width, double height)
    {
        string hex = CalendarService.ColorOf(ev);
        var text = new TextBlock { Text = ev.Title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var chip = new Border
        {
            Width = Math.Max(8, width), Height = height, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(6, 0, 4, 0), Child = text, ClipToBounds = true, Cursor = Cursors.Hand,
            Background = Tint(hex, 0.30), BorderBrush = new SolidColorBrush(ColorOf(hex)), ToolTip = Details(ev)
        };
        chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowDetails(ev, chip); };
        return chip;
    }

    /// <summary>The day's tasks as one soft chip ("8 pending tasks"), like Google Calendar's; click to see them.</summary>
    public static Border TasksChip(int count, double width, double height, Action<FrameworkElement> open)
    {
        var icon = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var circle = new System.Windows.Shapes.Ellipse { Width = 16, Height = 16 };
        circle.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FocusBrush");
        var tick = new System.Windows.Shapes.Path { Data = Geometry.Parse("M4.5,8.3 L7,10.6 L11.6,5.6"), Stroke = Brushes.White, StrokeThickness = 1.7,
                                                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        icon.Children.Add(circle); icon.Children.Add(tick);
        var text = new TextBlock { Text = count == 1 ? "1 pending task" : $"{count} pending tasks", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon); row.Children.Add(text);
        var chip = new Border { Width = Math.Max(30, width), Height = height, CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 0, 6, 0), Child = row,
                                Cursor = Cursors.Hand, ClipToBounds = true, ToolTip = "This day's tasks (click to see them)" };
        chip.SetResourceReference(Border.BackgroundProperty, "Panel2");
        chip.MouseEnter += (_, _) => chip.Opacity = 0.85;
        chip.MouseLeave += (_, _) => chip.Opacity = 1;
        chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; open(chip); };
        return chip;
    }

    /// <summary>
    /// Lays overlapping events side by side, the way Outlook does: events that overlap (directly or through others) form a
    /// group; each takes the first free column in its group, and the group's columns share the width.
    /// </summary>
    public static List<(T Item, int Col, int Cols, int Group)> SideBySide<T>(IEnumerable<T> items, Func<T, double> start, Func<T, double> end)
    {
        var sorted = items.OrderBy(start).ThenByDescending(i => end(i) - start(i)).ToList();
        var result = new List<(T Item, int Col, int Cols, int Group)>();
        var group = new List<(T Item, int Col)>();
        var colEnds = new List<double>();
        double groupEnd = double.MinValue;
        int groupId = 0;
        void Close()
        {
            foreach (var (item, col) in group) result.Add((item, col, Math.Max(1, colEnds.Count), groupId));
            group.Clear(); colEnds.Clear(); groupId++;
        }
        foreach (var item in sorted)
        {
            double s = start(item), e = Math.Max(end(item), s + 1);
            if (group.Count > 0 && s >= groupEnd) Close();
            int col = colEnds.FindIndex(c => c <= s);
            if (col < 0) { col = colEnds.Count; colEnds.Add(e); } else colEnds[col] = e;
            group.Add((item, col));
            groupEnd = group.Count == 1 ? e : Math.Max(groupEnd, e);
        }
        if (group.Count > 0) Close();
        return result;
    }

    public static string Details(CalendarEvent ev)
    {
        string when = !ev.AllDay ? $"{ev.Start:dddd, MMMM d}, {ev.Start:t} – {ev.End:t}"
            : (ev.End.Date - ev.Start.Date).Days > 1 ? $"{ev.Start:dddd, MMMM d} – {ev.End.Date.AddDays(-1):dddd, MMMM d} (all day)"
            : $"{ev.Start:dddd, MMMM d} (all day)";
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
