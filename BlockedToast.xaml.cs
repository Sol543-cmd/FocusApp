using System;
using System.Windows;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>The "this app is blocked" notice in the bottom-right corner.</summary>
public partial class BlockedToast : Window
{
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private Action? _action;

    public BlockedToast()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
    }

    public void ShowMessage(string title, string detail) => ShowMessage(title, detail, null, null);

    /// <summary>Shows a notice with an optional button (like Undo); it stays a little longer when there's a button.</summary>
    public void ShowMessage(string title, string detail, string? actionText, Action? action)
    {
        _action = action;
        ActionBtn.Content = actionText ?? "";
        ActionBtn.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
        _hideTimer.Interval = TimeSpan.FromSeconds(action != null ? 6 : 3.5);
        DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Text = title;
        DetailText.Text = detail;
        if (!IsVisible) Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void ActionBtn_Click(object sender, RoutedEventArgs e)
    {
        var action = _action;
        _action = null;
        _hideTimer.Stop();
        Hide();
        action?.Invoke();
    }
}
