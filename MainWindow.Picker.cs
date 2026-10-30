using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;

namespace FocusApp;

/// <summary>The "block apps" picker and the "block list" picker.</summary>
public partial class MainWindow
{
    private List<AppChoice>? _catalog;
    private List<AppChoice> _pickerItems = new();
    private ICollectionView? _pickerView;
    private Action<List<BlockedApp>>? _pickerApply;
    private List<BlockedApp> _pickerStartSelection = new();
    private HashSet<string> _pickerLocked = new(StringComparer.OrdinalIgnoreCase);
    private bool _pickerReady;

    private Action<List<string>>? _listPickerApply;
    private List<ListChoice> _listChoices = new();

    private async System.Threading.Tasks.Task LoadCatalogAsync()
    {
        try { _catalog = await AppCatalog.LoadAllAsync(); }
        catch (Exception ex)
        {
            Log.Error("LoadCatalog", ex);
            _catalog = new List<AppChoice>();
        }
    }

    /// <param name="lockCurrent">True while a session runs: apps already chosen can't be unchecked.</param>
    private async void OpenPicker(UIElement anchor, List<BlockedApp> current, Action<List<BlockedApp>> apply, bool lockCurrent = false)
    {
        KeepBlockEditorOpen();
        _pickerApply = apply;
        _pickerStartSelection = current.Select(a => a.Copy()).ToList();
        _pickerLocked = lockCurrent
            ? new HashSet<string>(current.Select(a => a.Key), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _pickerReady = false;
        PickerSearch.Text = "";
        PickerAddedText.Text = "";
        PickerList.ItemsSource = null;
        PickerStatus.Text = "Finding your apps...";
        PickerStatus.Visibility = Visibility.Visible;
        PickerPopup.PlacementTarget = anchor;
        PickerPopup.IsOpen = true;

        if (_catalog == null) await LoadCatalogAsync();
        if (!PickerPopup.IsOpen || _pickerApply != apply) return; // closed or reopened while loading
        FillPicker(_pickerStartSelection);
    }

    private void FillPicker(List<BlockedApp> selected)
    {
        var chosen = new HashSet<string>(selected.Select(a => a.Key), StringComparer.OrdinalIgnoreCase);
        var items = new List<AppChoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in _catalog ?? new List<AppChoice>())
        {
            if (!seen.Add(c.Key)) continue;
            items.Add(new AppChoice
            {
                Name = c.Name, Process = c.Process, PackageName = c.PackageName, IsRunning = c.IsRunning,
                IsChecked = chosen.Contains(c.Key), CanChange = !_pickerLocked.Contains(c.Key)
            });
        }
        // Keep chosen apps visible even if they aren't installed or open right now.
        foreach (var a in selected)
            if (seen.Add(a.Key))
                items.Add(new AppChoice
                {
                    Name = a.Name, Process = a.Process, PackageName = a.PackageName,
                    IsChecked = true, CanChange = !_pickerLocked.Contains(a.Key)
                });

        _pickerItems = items
            .OrderByDescending(i => i.IsChecked)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _pickerView = CollectionViewSource.GetDefaultView(_pickerItems);
        _pickerView.Filter = PickerFilter;
        PickerList.ItemsSource = _pickerView;
        PickerStatus.Visibility = _pickerItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PickerStatus.Text = "No apps found. Click Find app file, or open the app and click Refresh.";
        _pickerReady = true;
        PickerSearch.Focus();
    }

    private bool PickerFilter(object o)
    {
        string q = PickerSearch.Text.Trim();
        return q.Length == 0 || (o is AppChoice c &&
            (c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
             c.Process.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private void PickerSearch_TextChanged(object sender, TextChangedEventArgs e) => _pickerView?.Refresh();

    /// <summary>Enter adds the top match and clears the search, ready for the next one.</summary>
    private void PickerSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            PickerPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (!_pickerReady || _pickerView == null) return;

        if (PickerSearch.Text.Trim().Length == 0)
        {
            PickerAddedText.Text = "Type part of an app's name first.";
            return;
        }

        var matches = _pickerView.Cast<AppChoice>().ToList();
        var match = matches.FirstOrDefault(c => !c.IsChecked);
        if (match == null)
        {
            PickerAddedText.Text = matches.Count == 0 ? "No app matches that." : $"{matches[0].Name} is already blocked.";
            return;
        }
        match.IsChecked = true;
        PickerAddedText.Text = $"Added {match.Name}. Type another, or click Done.";
        PickerSearch.Text = "";
    }

    private async void PickerRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_pickerReady) return;
        var selection = CurrentPickerSelection();
        _pickerReady = false;
        PickerList.ItemsSource = null;
        PickerStatus.Text = "Finding your apps...";
        PickerStatus.Visibility = Visibility.Visible;
        await LoadCatalogAsync();
        if (PickerPopup.IsOpen) FillPicker(selection);
    }

    /// <summary>Checked apps, plus any locked ones (which can never be removed during a session).</summary>
    private List<BlockedApp> CurrentPickerSelection()
    {
        var result = _pickerItems.Where(i => i.IsChecked || !i.CanChange).Select(i => i.ToBlockedApp()).ToList();
        foreach (var a in _pickerStartSelection)
            if (_pickerLocked.Contains(a.Key) && !result.Any(r => r.Key == a.Key))
                result.Add(a.Copy());
        return result;
    }

    /// <summary>Lets you pick any program file to block, for apps that don't show up in the list.</summary>
    private void PickerBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (!_pickerReady) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose an app to block",
            Filter = "Apps (*.exe)|*.exe",
            CheckFileExists = true
        };

        bool? picked;
        PickerPopup.StaysOpen = true; // keep the list open behind the file dialog
        try { picked = dialog.ShowDialog(this); }
        finally { PickerPopup.StaysOpen = false; }
        if (picked != true || !PickerPopup.IsOpen) return;

        string path = dialog.FileName;
        string process = Path.GetFileNameWithoutExtension(path);
        string package = ProcessTools.PackageNameFromPath(path) ?? "";
        if (package.Length == 0 && AppCatalog.IsProtected(process))
        {
            Info("That program is part of Windows (or Focus itself), so it can't be blocked.", "Block apps");
            return;
        }

        string name = process;
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
        }
        catch { }

        var selection = CurrentPickerSelection();
        var added = new BlockedApp { Name = name, Process = process, PackageName = package };
        if (!selection.Any(a => a.Key == added.Key)) selection.Add(added);
        FillPicker(selection);
    }

    private void PickerDone_Click(object sender, RoutedEventArgs e) => PickerPopup.IsOpen = false;

    private void PickerPopup_Closed(object? sender, EventArgs e)
    {
        // If the list never finished loading, keep the previous choices.
        if (_pickerReady) _pickerApply?.Invoke(CurrentPickerSelection());
        _pickerApply = null;
        _pickerReady = false;
    }

    // ---------- Block list picker ----------

    /// <param name="lockCurrent">True while a session runs: lists already chosen can't be unchecked.</param>
    private void OpenListPicker(UIElement anchor, List<string> currentIds, Action<List<string>> apply, bool lockCurrent = false)
    {
        KeepBlockEditorOpen();
        _listChoices = BlockLists.All
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(l => new ListChoice
            {
                Id = l.Id,
                Name = l.Name,
                Detail = l.Summary,
                IsChecked = currentIds.Contains(l.Id),
                CanChange = !(lockCurrent && currentIds.Contains(l.Id))
            })
            .ToList();
        ListPickerList.ItemsSource = _listChoices;
        ListPickerEmpty.Visibility = _listChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _listPickerApply = apply;
        ListPickerPopup.PlacementTarget = anchor;
        ListPickerPopup.IsOpen = true;
    }

    private void ListPickerDone_Click(object sender, RoutedEventArgs e) => ListPickerPopup.IsOpen = false;

    private void ListPickerPopup_Closed(object? sender, EventArgs e)
    {
        var apply = _listPickerApply;
        _listPickerApply = null;
        apply?.Invoke(_listChoices.Where(c => c.IsChecked || !c.CanChange).Select(c => c.Id).ToList());
    }

    private void ManageLists_Click(object sender, RoutedEventArgs e)
    {
        ListPickerPopup.IsOpen = false;
        NavLists.IsChecked = true;
    }
}
