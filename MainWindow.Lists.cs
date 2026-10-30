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
        DeleteListBtn.IsEnabled = !_ui.Running;
        ListRunningNote.Visibility = _ui.Running ? Visibility.Visible : Visibility.Collapsed;
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
        var list = _editingList;
        if (list == null) return;
        if (_ui.Running)
        {
            Info("A session is running, so block lists can't be deleted until it ends.", "Block lists");
            return;
        }
        if (!Ask($"Delete the block list \"{list.Name}\"? Sessions that use it will stop blocking its apps.", "Delete list", defaultNo: true))
            return;

        BlockLists.All.Remove(list);
        foreach (var b in _plan.Where(b => b.BlockListIds.Contains(list.Id)))
            b.BlockListIds = b.BlockListIds.Where(id => id != list.Id).ToList();
        _settings.PomodoroLists.Remove(list.Id);
        _settings.QuickFocusLists.Remove(list.Id);
        SaveSettingsQuietly();
        _editingList = null;
        BlockLists.Save();
    }
}
