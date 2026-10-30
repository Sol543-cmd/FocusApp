using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace FocusApp;

/// <summary>One thing the command bar can do.</summary>
public class CommandItem : INotifyPropertyChanged
{
    private bool _selected;
    public string Title { get; init; } = "";
    public string Kind { get; init; } = "";
    public Action Run { get; init; } = () => { };
    public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The command bar (Ctrl+K): type to find a command or a task, then press Enter.
/// Starting with "+" adds the rest as a new task.
/// </summary>
public partial class CommandBarWindow : Window
{
    private readonly Func<string, List<CommandItem>> _search;
    private List<CommandItem> _items = new();
    private int _index;
    private bool _closing;

    public CommandBarWindow(Func<string, List<CommandItem>> search)
    {
        InitializeComponent();
        _search = search;
        Loaded += (_, _) => { Input.Focus(); Refresh(); };
        Deactivated += (_, _) => CloseOnce();
        Closing += (_, _) => _closing = true;
    }

    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Hint.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void Refresh()
    {
        _items = _search(Input.Text.Trim()).Take(12).ToList();
        _index = 0;
        UpdateSelection();
        Results.ItemsSource = _items;
    }

    private void UpdateSelection()
    {
        for (int i = 0; i < _items.Count; i++) _items[i].Selected = i == _index;
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: CloseOnce(); e.Handled = true; break;
            case Key.Down: if (_items.Count > 0) { _index = (_index + 1) % _items.Count; UpdateSelection(); } e.Handled = true; break;
            case Key.Up: if (_items.Count > 0) { _index = (_index - 1 + _items.Count) % _items.Count; UpdateSelection(); } e.Handled = true; break;
            case Key.Enter: RunSelected(); e.Handled = true; break;
        }
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CommandItem item)
        {
            _index = _items.IndexOf(item);
            RunSelected();
        }
    }

    private void RunSelected()
    {
        if (_index < 0 || _index >= _items.Count) return;
        var item = _items[_index];
        CloseOnce();
        try { item.Run(); }
        catch (Exception ex) { Log.Error("CommandBar", ex); }
    }
}
