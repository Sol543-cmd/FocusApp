using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>
/// Training settings (the gear on the Training page), laid out like Focus's Settings: a page list on the left, small-capital
/// section headings with one row per setting on the right, and "Changes save as you make them." with Done at the bottom.
/// Every Training setting lives here, Greek practice included.
/// </summary>
public partial class MainWindow
{
    private string _trainingSettingsPage = "plan";
    private bool _greekHooked;
    private readonly DispatcherTimer _greekSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private static readonly (string Key, string Name)[] TrainingSettingsPages =
    {
        ("plan", "Weekly plan"), ("lessons", "Lessons"), ("reading", "Reading"), ("practice", "Practice"), ("greek", "Greek practice"), ("weekends", "Weekends")
    };

    private void BuildTrainingSettings()
    {
        var root = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // ----- The page list -----
        var navBox = new StackPanel();
        var heading = new TextBlock { Text = "Training settings", Margin = new Thickness(4, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(StyleProperty, "Heading");
        navBox.Children.Add(heading);
        var sub = new TextBlock { Text = "For every book", Margin = new Thickness(4, 0, 0, 14) };
        sub.SetResourceReference(StyleProperty, "Caption");
        navBox.Children.Add(sub);
        foreach (var (key, name) in TrainingSettingsPages)
        {
            var rb = new RadioButton { Content = name, Tag = key, GroupName = "trainingsettings", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                                       Margin = new Thickness(0, 0, 0, 2), IsChecked = key == _trainingSettingsPage };
            rb.SetResourceReference(StyleProperty, "NavButton");
            rb.Checked += (_, _) => { if (_trainingSettingsPage == key) return; FlushGreekSettings(); _trainingSettingsPage = key; RefreshTraining(); };
            navBox.Children.Add(rb);
        }
        var nav = new Border { Child = navBox, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 18, 14, 14), VerticalAlignment = VerticalAlignment.Stretch };
        nav.SetResourceReference(Border.BackgroundProperty, "Panel");
        root.Children.Add(nav);

        // ----- The page, with Done at the bottom -----
        var main = new DockPanel();
        var footer = new DockPanel();
        var done = new Button { Content = "Done", Padding = new Thickness(22, 7, 22, 7), ToolTip = "Back to Training" };
        done.SetResourceReference(StyleProperty, "PrimaryButton");
        done.Click += (_, _) => { FlushGreekSettings(); _trainingView = "library"; RefreshTraining(); };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        var saves = new TextBlock { Text = "Changes save as you make them.", VerticalAlignment = VerticalAlignment.Center };
        saves.SetResourceReference(StyleProperty, "Caption");
        footer.Children.Add(saves);
        var footerBorder = new Border { Child = footer, Padding = new Thickness(22, 12, 18, 12), BorderThickness = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(0, 0, 10, 10) };
        footerBorder.SetResourceReference(Border.BorderBrushProperty, "Line");
        footerBorder.SetResourceReference(Border.BackgroundProperty, "Panel");
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        main.Children.Add(footerBorder);

        var page = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Width = 600 };
        var pageTitle = new TextBlock { Text = TrainingSettingsPages.First(p => p.Key == _trainingSettingsPage).Name, Margin = new Thickness(0, 0, 0, 6) };
        pageTitle.SetResourceReference(StyleProperty, "Heading");
        if (_trainingSettingsPage != "greek") page.Children.Add(pageTitle);
        switch (_trainingSettingsPage)
        {
            case "plan": PlanPage(page); break;
            case "lessons": LessonsPage(page); break;
            case "reading": ReadingPage(page); break;
            case "practice": PracticePage(page); break;
            case "greek": GreekPage(page); break;
            default: WeekendsPage(page); break;
        }
        var body = new Border { Child = page, CornerRadius = new CornerRadius(10, 10, 0, 0), Padding = new Thickness(28), VerticalAlignment = VerticalAlignment.Top };
        body.SetResourceReference(Border.BackgroundProperty, "Panel");
        main.Children.Add(body);
        Grid.SetColumn(main, 2);
        root.Children.Add(main);
        TrainingHost.Children.Add(root);
    }

    // ---------- Building blocks (the same look as Focus's Settings) ----------

    private static TextBlock Section(string text)
    {
        var t = new TextBlock { Text = text.ToUpperInvariant() };
        t.SetResourceReference(StyleProperty, "SettingsSection");
        return t;
    }

    private static TextBlock Note(string text, double top = 0)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        t.SetResourceReference(StyleProperty, "Caption");
        return t;
    }

    /// <summary>A row: the setting's name and description on the left, its control on the right, a thin line under it.</summary>
    private static Border SetRow(string name, string? description, UIElement? control)
    {
        var g = new DockPanel();
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(fe, Dock.Right);
            g.Children.Add(fe);
        }
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (description != null) left.Children.Add(Note(description, 3));
        g.Children.Add(left);
        var b = new Border { Child = g };
        b.SetResourceReference(StyleProperty, "SettingRow");
        return b;
    }

    /// <summary>A switch row (the same check switch Focus's Settings use).</summary>
    private static Border SwitchRow(string name, string? description, bool on, Action<bool> changed)
    {
        var cb = new CheckBox { IsChecked = on, ToolTip = name };
        cb.Click += (_, _) => changed(cb.IsChecked == true);
        return SetRow(name, description, cb);
    }

    private static TextBox NumberField(int value, int min, int max, Action<int> save)
    {
        var tb = new TextBox { Width = 64, Text = value.ToString(CultureInfo.InvariantCulture), HorizontalContentAlignment = HorizontalAlignment.Center };
        tb.LostFocus += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int n) && n >= min && n <= max) save(n);
            else tb.Text = value.ToString(CultureInfo.InvariantCulture);
        };
        return tb;
    }

    // ---------- Pages ----------

    private void PlanPage(StackPanel page)
    {
        var cfg = Course.Config;
        page.Children.Add(Section("Books and days"));
        page.Children.Add(Note("Which book you study each day. A day can have one book; to do two lessons on a day, or to move a lesson, change that week under Lessons this week on the Training page. The order sets what comes next in each track."));
        var books = cfg.Courses.ToList();
        bool unplanned = books.All(c => c.Days.Count == 0);
        if (books.Count == 0) page.Children.Add(Note("No books yet: add one on the Training page.", 10));
        foreach (var c in books)
        {
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            var role = new ComboBox { Width = 104, ItemsSource = Course.Roles, SelectedItem = Course.RoleOf(c), Margin = new Thickness(0, 0, 10, 0) };
            role.SelectionChanged += (_, _) => { c.Role = role.SelectedItem as string ?? "Track A"; Course.SaveConfig(); };
            controls.Children.Add(role);
            string cid = c.Id;
            void Move(int by)
            {
                int at = cfg.Courses.FindIndex(x => x.Id == cid), to = Math.Clamp(at + by, 0, cfg.Courses.Count - 1);
                if (at < 0 || to == at) return;
                var item = cfg.Courses[at]; cfg.Courses.RemoveAt(at); cfg.Courses.Insert(to, item);
                Course.SaveConfig(); RefreshTraining();
            }
            var up = new Button { Content = "↑", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Earlier in its track", IsEnabled = books.IndexOf(c) > 0 };
            var down = new Button { Content = "↓", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Later in its track", IsEnabled = books.IndexOf(c) < books.Count - 1 };
            up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
            controls.Children.Add(up); controls.Children.Add(down);
            var row = SetRow(c.Title, null, controls);
            // The days, as small toggle chips under the title.
            var days = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            string[] names = { "Mon", "Tue", "Wed", "Thu", "Fri" };
            for (int d = 1; d <= 5; d++)
            {
                int dd = d;
                bool on = unplanned ? c.Id == Course.Current.Id : c.Days.Contains(d);
                var chip = new Button { Content = names[d - 1], Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0), Cursor = System.Windows.Input.Cursors.Hand,
                                        ToolTip = on ? "Planned: click to free this day" : "Click to study this book on this day" };
                if (on) chip.SetResourceReference(StyleProperty, "PrimaryButton");
                chip.Click += (_, _) =>
                {
                    // The first change turns the default (your book on every weekday) into a real plan.
                    if (cfg.Courses.All(x => x.Days.Count == 0) && cfg.Courses.FirstOrDefault(x => x.Id == Course.Current.Id) is CourseInfo cur) cur.Days = new List<int> { 1, 2, 3, 4, 5 };
                    if (!on) { foreach (var other in cfg.Courses) other.Days.Remove(dd); if (!c.Days.Contains(dd)) c.Days.Add(dd); } // one book a day
                    else c.Days.Remove(dd);
                    Course.SaveConfig(); RefreshTraining();
                };
                days.Children.Add(chip);
            }
            ((StackPanel)((DockPanel)row.Child).Children[^1]).Children.Add(days);
            page.Children.Add(row);
        }

        page.Children.Add(Section("If you miss a lesson"));
        var offer = new RadioButton { Content = "Offer it as an optional catch-up the next lesson day", GroupName = "missed", IsChecked = cfg.MissedPolicy != "carry", Margin = new Thickness(0, 8, 0, 0) };
        var carry = new RadioButton { Content = "Just carry on: the book picks up where it left off on its next day", GroupName = "missed", IsChecked = cfg.MissedPolicy == "carry", Margin = new Thickness(0, 8, 0, 0) };
        offer.Checked += (_, _) => { cfg.MissedPolicy = "offer"; Course.SaveConfig(); };
        carry.Checked += (_, _) => { cfg.MissedPolicy = "carry"; Course.SaveConfig(); };
        var missed = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        missed.Children.Add(offer); missed.Children.Add(carry);
        var mb = new Border { Child = missed }; mb.SetResourceReference(StyleProperty, "SettingRow");
        page.Children.Add(mb);
        page.Children.Add(Note("Catch-ups come one at a time. A lesson you remove from a day under Lessons this week isn't missed.", 10));
    }

    private void LessonsPage(StackPanel page)
    {
        var cfg = Course.Config;
        page.Children.Add(Section("Lesson times"));
        page.Children.Add(SwitchRow("Lessons pop up at these times", "Turn off to start lessons only yourself.", cfg.On, on => { cfg.On = on; Course.SaveConfig(); }));
        var times = TrainingTimes();
        var boxes = new StackPanel { Orientation = Orientation.Horizontal };
        var fields = new List<TextBox>();
        void SaveTimes()
        {
            var parsed = new List<string>();
            foreach (var tb in fields)
            {
                if (tb.Text.Trim().Length == 0) continue;
                if (!TryParseTime(tb.Text, out var t)) return; // left as typed until it's a time
                parsed.Add(t.ToString(@"hh\:mm", CultureInfo.InvariantCulture));
            }
            var list = parsed.Distinct().OrderBy(x => x).ToList();
            if (list.SequenceEqual(cfg.Times)) return;
            cfg.Times = list;
            Course.SaveConfig();
            SaveSharedPracticeTimes();
        }
        for (int i = 0; i < 3; i++)
        {
            var tb = new TextBox { Width = 84, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), Text = i < times.Count ? DateTime.Today.Add(times[i]).ToString("t") : "", ToolTip = i == 2 ? "The last time: required" : "Pops up; Later waits for the next time" };
            tb.LostFocus += (_, _) => SaveTimes();
            fields.Add(tb);
            boxes.Children.Add(tb);
        }
        page.Children.Add(SetRow("Times", "The first two pop up and can wait. At the last one the day's first lesson must be finished (a second lesson that day becomes a catch-up). Lessons never interrupt a meeting or lunch. Also on the practice timelines.", boxes));

        page.Children.Add(Section("Block lists during lessons"));
        page.Children.Add(Note("These switch on when a lesson starts (apps and any Freedom web blocking in them) and off when it ends."));
        if (BlockLists.All.Count == 0) page.Children.Add(Note("No block lists yet (Block lists in the sidebar).", 8));
        foreach (var list in BlockLists.All.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            string id = list.Id;
            page.Children.Add(SwitchRow(list.Name, null, cfg.BlockListIds.Contains(id), on =>
            {
                if (on) { if (!cfg.BlockListIds.Contains(id)) cfg.BlockListIds.Add(id); } else cfg.BlockListIds.Remove(id);
                Course.SaveConfig();
            }));
        }

        page.Children.Add(Section("Review in lessons"));
        page.Children.Add(SetRow("Review questions per lesson", "At most this many earlier questions in each lesson.", NumberField(cfg.ReviewCap, 0, 50, n => { cfg.ReviewCap = n; Course.SaveConfig(); })));
        page.Children.Add(SetRow("Daily review time, in minutes", "New memory cards join your review while it fits in this time; after a busy stretch they wait their turn. Lessons always go ahead.",
                              NumberField(cfg.ReviewMinutes, 2, 60, n => { cfg.ReviewMinutes = n; Course.SaveConfig(); })));
    }

    private void ReadingPage(StackPanel page)
    {
        var cfg = Course.Config;
        page.Children.Add(Section("Layout"));
        page.Children.Add(SwitchRow("Page view", "Turn pages like a book instead of scrolling.", cfg.PageView, on => { cfg.PageView = on; Course.SaveConfig(); }));
        page.Children.Add(Section("How the reading looks"));
        page.Children.Add(Note("Font, size and background of the book's text (also under Aa while reading). Books imported before version 1.7.14 need their Word file chosen again to show text boxes, captions, lists, tables and charts."));
        var look = LessonWindow.AppearancePanel(() => { });
        look.Margin = new Thickness(0, 14, 0, 0);
        page.Children.Add(look);
    }

    private void PracticePage(StackPanel page)
    {
        var cfg = Course.Config;
        var times = new Button { Content = "Practice timelines", Padding = new Thickness(14, 5, 14, 5) };
        times.Click += (_, _) => OpenPracticeTimelines();

        page.Children.Add(Section("Books practice"));
        page.Children.Add(SwitchRow("Pop up at its times", "Multiple-choice rounds. Learn: what's due in the day's book. Review: any book and lesson.", cfg.AllBooksAuto, on => { cfg.AllBooksAuto = on; Course.SaveConfig(); }));

        page.Children.Add(Section("Book flashcards"));
        page.Children.Add(SwitchRow("Pop up at its times", "The hardest memory cards from that day's lesson first. Once a day across your devices.", cfg.CardReviewOn, on => { cfg.CardReviewOn = on; Course.SaveConfig(); }));
        page.Children.Add(SetRow("Length, in minutes", null, NumberField(cfg.CardReviewMinutes, 1, 30, n => { cfg.CardReviewMinutes = n; Course.SaveConfig(); })));

        page.Children.Add(Section("Timeline"));
        page.Children.Add(SwitchRow("Pop up at its times", "Place events on the line. Learn: what's due and your last two lessons' events. Review: an era or a span of years.", cfg.TimelineOn, on => { cfg.TimelineOn = on; Course.SaveConfig(); }));
        page.Children.Add(SetRow("Events a round", null, NumberField(cfg.TimelineCount, 3, 15, n => { cfg.TimelineCount = n; Course.SaveConfig(); })));
        page.Children.Add(SetRow("Browse the timeline", "Every event you've learned on one line you can zoom; click one to read its passage.",
                              TButton("Open", () => new TimelineBrowserWindow { Owner = this }.Show())));

        page.Children.Add(Section("Where practice shows"));
        page.Children.Add(SwitchRow("In the Day plan and Dashboard", "Small markers for the day's practice rounds.", cfg.ShowPracticeInDay, on => { cfg.ShowPracticeInDay = on; Course.SaveConfig(); RefreshDayViews(); }));
        page.Children.Add(SwitchRow("On the Training page", "Today's practice as a line through the day.", cfg.ShowPracticeOnTraining, on => { cfg.ShowPracticeOnTraining = on; Course.SaveConfig(); }));
        page.Children.Add(SetRow("Times for every practice", "Move, add or remove rounds on one line with your meetings.", times));
    }

    private void WeekendsPage(StackPanel page)
    {
        page.Children.Add(Section("Weekends"));
        page.Children.Add(SwitchRow("Pause all practice on weekends", "No lesson, flashcards, Books practice, timeline or Greek round pops up by itself on Saturday and Sunday. A lesson you add to a weekend day under Lessons this week still comes up.",
                                    _settings.PauseWeekends, on => SetPauseWeekends(on)));
        page.Children.Add(Section("Daily reset"));
        var resetBox = new TextBox { Width = 90, IsEnabled = _settings.PracticeEndOn, Text = PracticeDayEnd is TimeSpan pe ? DateTime.Today.Add(pe).ToString("t") : "4:30 PM" };
        var resetCheck = new CheckBox { IsChecked = _settings.PracticeEndOn, Margin = new Thickness(0, 0, 12, 0) };
        void ApplyReset() { SetPracticeEnd(resetCheck.IsChecked == true, ReadClockTime(resetBox.Text)); resetBox.IsEnabled = resetCheck.IsChecked == true; }
        resetCheck.Click += (_, _) => ApplyReset();
        resetBox.LostFocus += (_, _) => ApplyReset();
        var both = new StackPanel { Orientation = Orientation.Horizontal };
        both.Children.Add(resetCheck); both.Children.Add(resetBox);
        page.Children.Add(SetRow("Reset practice at", "Times before the reset stop at it, and anything left on screen while you're away closes (a lesson moves to tomorrow). Times after it last until midnight.", both));
    }

    /// <summary>Greek practice's settings (they used to be in Settings): the same fields, moved here, saving as you change them.</summary>
    private void GreekPage(StackPanel page)
    {
        var greek = SettingsPage_greek;
        if (greek.Parent is Panel old) old.Children.Remove(greek);
        greek.Visibility = Visibility.Visible;
        try { BeginFillingSettings(); FillVocabSettings(); } catch (Exception ex) { Log.Error("Greek settings fill", ex); }
        if (!_greekHooked)
        {
            _greekHooked = true;
            _greekSaveTimer.Tick += (_, _) =>
            {
                _greekSaveTimer.Stop();
                if (_fillingSettings || TypingInNumberBox()) return;
                AutoSaveSettings();
            };
            void Changed() { if (!_fillingSettings) { _greekSaveTimer.Stop(); _greekSaveTimer.Start(); } }
            greek.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Changed()), true);
            greek.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => Changed()), true);
            greek.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => Changed()), true);
            greek.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => Changed()), true);
            greek.AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => Changed()), true);
            greek.AddHandler(UIElement.LostFocusEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is TextBox) Changed(); }), true);
            greek.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Changed()), true);
        }
        page.Children.Add(greek);
    }

    /// <summary>Saves Greek practice's fields straight away (when you leave its page or Training settings).</summary>
    private void FlushGreekSettings()
    {
        if (!_greekSaveTimer.IsEnabled) return;
        _greekSaveTimer.Stop();
        if (!_fillingSettings) AutoSaveSettings();
    }
}
