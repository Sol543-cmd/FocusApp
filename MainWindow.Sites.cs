using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// Entering websites the Raycast way: each site is a small chip with a globe and an ✕, and you type
/// new ones into the box at the end (Enter, comma or space adds; pasting a list adds them all;
/// Backspace in the empty box removes the last one). Used in Settings, block lists and sessions.
/// </summary>
public partial class MainWindow
{
    private List<string> _editBlockedSites = new(), _editAllowedSites = new();
    private bool _fillingSites;

    /// <summary>Draws a site editor into <paramref name="host"/>. <paramref name="save"/> gets the new list after every change.</summary>
    private void BuildSiteEditor(Panel host, List<string> sites, Action<List<string>> save, bool canRemove, string placeholder = "Add a website, like youtube.com")
    {
        host.Children.Clear();
        var wrap = new WrapPanel { Margin = new Thickness(0) };
        var box = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(8, 8, 8, 2), Child = wrap };
        box.SetResourceReference(Border.BackgroundProperty, "Panel");
        box.SetResourceReference(Border.BorderBrushProperty, "Line");
        var error = new TextBlock { FontSize = 11.5, Margin = new Thickness(2, 4, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        error.SetResourceReference(TextBlock.ForegroundProperty, "PriorityHigh");
        var hint = new TextBlock { FontSize = 11, Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = "Press Enter to add. A site covers all its pages and subdomains (www., m., and so on)." };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var current = sites.ToList();
        void Commit(List<string> next)
        {
            save(next);
            BuildSiteEditor(host, next, save, canRemove, placeholder);
            // Keep typing where you were.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (host.Children.OfType<Border>().FirstOrDefault()?.Child is WrapPanel w)
                    w.Children.OfType<Grid>().LastOrDefault()?.Children.OfType<TextBox>().FirstOrDefault()?.Focus();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        foreach (var site in current)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal };
            var globe = new TextBlock { Text = "\uE774", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            globe.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            chip.Children.Add(globe);
            chip.Children.Add(new TextBlock { Text = site, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            if (canRemove)
            {
                var x = new Button { Content = "✕", FontSize = 10, Padding = new Thickness(6, 1, 4, 1), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Remove " + site };
                x.SetResourceReference(StyleProperty, "IconButton");
                string s = site;
                x.Click += (_, _) => Commit(current.Where(c => c != s).ToList());
                chip.Children.Add(x);
            }
            var pill = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 4, canRemove ? 4 : 10, 4), Margin = new Thickness(0, 0, 6, 6), Child = chip };
            pill.SetResourceReference(Border.BackgroundProperty, "Panel2");
            wrap.Children.Add(pill);
        }

        // The input at the end of the chips.
        var input = new TextBox { MinWidth = 200, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center };
        var ghost = new TextBlock { Text = current.Count == 0 ? placeholder : "Add another", IsHitTestVisible = false, Margin = new Thickness(7, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center };
        ghost.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var cell = new Grid();
        cell.Children.Add(input);
        cell.Children.Add(ghost);
        wrap.Children.Add(cell);

        bool TryAdd(string text)
        {
            var parts = text.Split(new[] { ',', ' ', '\n', '\r', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(SiteRules.Clean).ToList();
            var bad = parts.Where(p => !p.Contains('.') || p.StartsWith('.') || p.Length < 4).ToList();
            var good = parts.Except(bad).Where(p => !current.Contains(p)).Distinct().ToList();
            error.Visibility = bad.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            error.Text = bad.Count > 0 ? $"\"{bad[0]}\" doesn't look like a website. Try something like youtube.com." : "";
            if (good.Count == 0) return bad.Count == 0;
            Commit(current.Concat(good).ToList());
            return true;
        }
        input.TextChanged += (_, _) =>
        {
            ghost.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // Pasting a list (several lines or commas) adds them all at once.
            if (input.Text.IndexOfAny(new[] { '\n', ',', ';' }) >= 0 && TryAdd(input.Text)) input.Text = "";
        };
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space or Key.OemComma || (e.Key == Key.Tab && input.Text.Trim().Length > 0))
            {
                if (input.Text.Trim().Length == 0) { if (e.Key != Key.Tab) e.Handled = true; return; }
                e.Handled = true;
                if (TryAdd(input.Text)) input.Text = "";
            }
            else if (e.Key == Key.Back && input.Text.Length == 0 && canRemove && current.Count > 0)
            {
                e.Handled = true;
                Commit(current.Take(current.Count - 1).ToList());
            }
        };
        input.LostFocus += (_, _) => { if (input.Text.Trim().Length > 0 && TryAdd(input.Text)) input.Text = ""; };

        host.Children.Add(box);
        host.Children.Add(error);
        host.Children.Add(hint);
    }

    // ---------- Settings ----------

    private void FillSiteSettings()
    {
        _editBlockedSites = _settings.BlockedSites.ToList();
        _editAllowedSites = _settings.AllowedSites.ToList();
        BuildSiteEditor(BlockedSitesHost, _editBlockedSites, l => _editBlockedSites = l, canRemove: true);
        BuildSiteEditor(AllowedSitesHost, _editAllowedSites, l => _editAllowedSites = l, canRemove: true, "Add a site that's always allowed");
    }

    // ---------- Block lists ----------

    private void RefreshListSites()
    {
        if (_editingList == null) return;
        _fillingSites = true;
        ListSitesBlock.IsChecked = _editingList.SitesMode != "allow";
        ListSitesAllow.IsChecked = _editingList.SitesMode == "allow";
        _fillingSites = false;
        var list = _editingList;
        BuildSiteEditor(ListSitesHost, list.Sites ?? new(), next =>
        {
            list.Sites = next;
            BlockLists.Save();
            UpdateListSummaries();
        }, canRemove: !_ui.Running, list.SitesMode == "allow" ? "Add a site to allow, like docs.google.com" : "Add a website, like facebook.com");
        ListSitesNote.Text = !_settings.On("websites") ? "Website blocking is switched off in Settings, Features, so these sites aren't blocked yet."
            : _ui.Running ? "While a session runs you can add sites but not remove them." : "";
    }

    private void ListSitesMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_fillingSites || _editingList == null || ListSitesAllow == null) return;
        _editingList.SitesMode = ListSitesAllow.IsChecked == true ? "allow" : "block";
        BlockLists.Save();
        RefreshListSites();
        UpdateListSummaries();
    }

    /// <summary>Refreshes the list names and summaries without redrawing the open editor.</summary>
    private void UpdateListSummaries()
    {
        try { ListsList.Items.Refresh(); } catch { }
    }

    // ---------- A single session ----------

    private void RefreshEditorSites(PlanBlock? block)
    {
        if (EditorSites == null) return;
        bool show = false; // website blocking was removed
        EditorSites.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || block == null) return;
        _fillingSites = true;
        EditorSitesBlock.IsChecked = block.SitesMode != "allow";
        EditorSitesAllow.IsChecked = block.SitesMode == "allow";
        _fillingSites = false;
        bool runningNow = ReferenceEquals(block, _runner.Current);
        BuildSiteEditor(EditorSitesHost, block.Sites ?? new(), next =>
        {
            block.Sites = next;
            SaveSessionSites();
        }, canRemove: !runningNow, block.SitesMode == "allow" ? "Allow only, like docs.google.com" : "Block a website, like youtube.com");
    }

    private void EditorSitesMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_fillingSites || _selected == null || EditorSitesAllow == null) return;
        if (ReferenceEquals(_selected, _runner.Current) && LockedNow && EditorSitesBlock.IsChecked == true && _selected.SitesMode == "allow")
        {
            _toast.ShowMessage("Locked", "This session can't be loosened while it's running.");
            RefreshEditorSites(_selected);
            return;
        }
        _selected.SitesMode = EditorSitesAllow.IsChecked == true ? "allow" : "block";
        SaveSessionSites();
        RefreshEditorSites(_selected);
    }

    private void SaveSessionSites()
    {
        _planDirty = true;
        SavePlanNow();
        if (_runner.IsRunning) SaveRunState();
    }

    // ---------- Tasks: search and add live behind icons ----------

    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SearchRow.Visibility == Visibility.Visible && SearchBox.Text.Length == 0) { SearchRow.Visibility = Visibility.Collapsed; return; }
        ShowSearch();
    }

    private void ShowSearch()
    {
        SearchRow.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() => { SearchBox.Focus(); SearchBox.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        SearchBox.Text = "";
        SearchRow.Visibility = Visibility.Collapsed;
    }

    private void AddToggle_Click(object sender, RoutedEventArgs e)
    {
        if (AddRow.Visibility == Visibility.Visible && BinNewBox.Text.Length == 0) { AddRow.Visibility = Visibility.Collapsed; return; }
        ShowAddBox();
    }

    private void ShowAddBox()
    {
        if (BinTodoView.IsChecked != true && BinUndatedView.IsChecked != true && BinTodayView.IsChecked != true && BinTomorrowView.IsChecked != true) BinTodoView.IsChecked = true;
        AddRow.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() => BinNewBox.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>The add box tucks away when you click elsewhere with nothing typed in it.</summary>
    private void BinNewBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (BinNewBox.Text.Trim().Length == 0) AddRow.Visibility = Visibility.Collapsed;
    }

    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        NewProjectRow.Visibility = NewProjectRow.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (NewProjectRow.Visibility == Visibility.Visible)
            Dispatcher.BeginInvoke(new Action(() => NewProjectBox.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }
}
