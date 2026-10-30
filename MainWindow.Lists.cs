using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace FocusApp;

/// <summary>The Block lists tab: named groups of apps you can add to any session.</summary>
public partial class MainWindow
{
    private BlockList? _editingList;
    private bool _fillingListName;

    private void BlockLists_Changed()
    {
        RefreshListsTab();
        foreach (var b in _plan) b.RefreshSummary();
        Timeline.RequestRender();
        UpdateQuickBlockButtons();
        if (_runner.IsRunning) ApplyBlockingForCurrent();
    }

    private void RefreshListsTab()
    {
        var lists = BlockLists.All.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        ListsList.ItemsSource = lists;
        NoListsHint.Visibility = lists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_editingList != null && !BlockLists.All.Contains(_editingList)) _editingList = null;
        ShowListEditor();
    }

    private void ShowListEditor()
    {
        bool has = _editingList != null;
        ListEditor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoListSelectedHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (_editingList == null) return;

        _fillingListName = true;
        if (ListNameBox.Text != _editingList.Name) ListNameBox.Text = _editingList.Name;
        _fillingListName = false;

        RefreshListSites();
        ListAppsList.ItemsSource = _editingList.Apps.ToList();
        ListEmptyHint.Visibility = _editingList.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListSummaryText.Text = _editingList.Summary;
        ListRunningNote.Visibility = _ui.Running ? Visibility.Visible : Visibility.Collapsed;
        FillFreedomChoices();
    }

    private void NewList_Click(object sender, RoutedEventArgs e)
    {
        string name = "New block list";
        for (int n = 2; BlockLists.All.Any(l => l.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)); n++)
            name = $"New block list {n}";
        var list = new BlockList { Name = name };
        BlockLists.All.Add(list);
        _editingList = list;
        BlockLists.Save();
        ListNameBox.Focus();
        ListNameBox.SelectAll();
    }

    private void SelectList_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BlockList list) return;
        _editingList = list;
        ShowListEditor();
    }

    private void ListName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_fillingListName || _editingList == null) return;
        // Keep exactly what's typed (so spaces work); ignore an empty box until something is typed.
        if (ListNameBox.Text.Trim().Length == 0) return;
        _editingList.Name = ListNameBox.Text;
        BlockLists.Save();
    }

    private void AddAppsToList_Click(object sender, RoutedEventArgs e)
    {
        var list = _editingList;
        if (list == null) return;
        OpenPicker((UIElement)sender, list.Apps, chosen =>
        {
            list.Apps = chosen;
            BlockLists.Save();
        }, lockCurrent: _ui.Running);
    }

    private void RemoveAppFromList_Click(object sender, RoutedEventArgs e)
    {
        if (_editingList == null || _ui.Running) return;
        if ((sender as FrameworkElement)?.DataContext is not BlockedApp app) return;
        _editingList.Apps = _editingList.Apps.Where(a => a.Key != app.Key).ToList();
        BlockLists.Save();
    }

    private void DeleteList_Click(object sender, RoutedEventArgs e)
    {
        if (_editingList != null) DeleteList(_editingList);
    }

    private void DeleteListFromCard_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // don't also open the list
        if ((sender as FrameworkElement)?.DataContext is BlockList list) DeleteList(list);
    }

    /// <summary>Deletes a list and takes it off every session. Only the session running right now keeps it until it ends.</summary>
    private void DeleteList(BlockList list)
    {
        if (_runner.IsRunning && _runner.Current is PlanBlock cur && cur.IsFocus && cur.BlockListIds.Contains(list.Id))
        {
            Info($"\"{list.Name}\" is blocking the session that's running now, so it can be deleted once this session ends.", "Block lists");
            return;
        }
        if (!Ask($"Delete the block list \"{list.Name}\"? Sessions that use it will stop using it.", "Delete list", defaultNo: true))
            return;

        BlockLists.All.Remove(list);
        foreach (var b in _plan.Where(b => b.BlockListIds.Contains(list.Id)))
            b.BlockListIds = b.BlockListIds.Where(id => id != list.Id).ToList();
        _settings.PomodoroLists.Remove(list.Id);
        _settings.QuickFocusLists.Remove(list.Id);
        SaveSettingsQuietly();
        if (_editingList == list) _editingList = null;
        BlockLists.Save();
    }

    // ---------- Freedom web blocking in a list ----------

    private void FillFreedomChoices()
    {
        var list = _editingList;
        if (list == null) return;
        FreedomListsHost.Children.Clear();
        var data = FreedomService.Data;
        if (!FreedomService.SignedIn)
        {
            FreedomListsNote.Text = "Connect Freedom (Freedom… at the bottom left) to add its web blocking to this list.";
            return;
        }
        if (data.Blocklists.Count == 0)
        {
            FreedomListsNote.Text = "No blocklists in Freedom yet. Make one in the Freedom dashboard, then click Sync.";
            return;
        }
        foreach (var item in data.Blocklists)
        {
            var check = new CheckBox { Content = item.Name, IsChecked = list.FreedomBlocklistIds.Contains(item.Id), Margin = new Thickness(0, 4, 18, 4) };
            string id = item.Id;
            check.Checked += (_, _) => { if (!list.FreedomBlocklistIds.Contains(id)) { list.FreedomBlocklistIds.Add(id); BlockLists.Save(); } };
            check.Unchecked += (_, _) => { if (list.FreedomBlocklistIds.Remove(id)) BlockLists.Save(); };
            FreedomListsHost.Children.Add(check);
        }
        FreedomListsNote.Text = data.DeviceIds.Count == 0
            ? "No Freedom devices are chosen yet: open Freedom… at the bottom left and tick the devices to block on."
            : $"Blocks on {FreedomService.Data.Devices.Where(d => data.DeviceIds.Contains(d.Id)).Select(d => d.Name).DefaultIfEmpty("your devices").Aggregate((a, b) => a + ", " + b)} (change in Freedom…).";
    }

    private async void SyncFreedomLists_Click(object sender, RoutedEventArgs e)
    {
        if (!FreedomService.SignedIn) { OpenFreedomWindow(); return; }
        FreedomListsNote.Text = "Syncing with Freedom…";
        try { await FreedomService.SyncAsync(); }
        catch (Exception ex) { FreedomListsNote.Text = ex.Message; return; }
        PruneFreedomFromLists();
        FillFreedomChoices();
    }

    /// <summary>A Freedom blocklist deleted in Freedom comes off every block list too.</summary>
    private void PruneFreedomFromLists()
    {
        var known = FreedomService.Data.Blocklists.Select(l => l.Id).ToHashSet();
        if (known.Count == 0) return; // never wipe choices because a sync came back empty
        bool changed = false;
        foreach (var l in BlockLists.All)
            changed |= l.FreedomBlocklistIds.RemoveAll(id => !known.Contains(id)) > 0;
        if (changed) BlockLists.Save();
    }

    private void UpdateListsFreedomStatus()
    {
        if (ListsFreedomStatus == null) return;
        var d = FreedomService.Data;
        ListsFreedomStatus.Text = !FreedomService.SignedIn ? "Not connected"
            : $"Connected · {(d.DeviceIds.Count == 1 ? "1 device" : $"{d.DeviceIds.Count} devices")}" + (d.LastSyncUtc == default ? "" : $" · synced {d.LastSyncUtc.ToLocalTime():t}");
    }
}
