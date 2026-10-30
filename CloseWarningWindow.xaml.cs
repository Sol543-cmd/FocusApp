using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>Warns that open apps are about to be closed for the session.</summary>
public partial class CloseWarningWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _secondsLeft;

    public event Action? CloseNowRequested;

    public CloseWarningWindow(List<BlockedApp> apps, int seconds)
    {
        InitializeComponent();
        _secondsLeft = seconds;
        AppsText.Text = string.Join(", ", apps.Select(a => a.Name));
        UpdateText();
        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            if (_secondsLeft <= 0) Close();
            else UpdateText();
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void UpdateText()
    {
        string unit = _secondsLeft == 1 ? "second" : "seconds";
        CountdownText.Text = $"Closing blocked apps in {_secondsLeft} {unit}";
    }

    private void CloseNow_Click(object sender, RoutedEventArgs e)
    {
        CloseNowRequested?.Invoke();
        Close();
    }
}
