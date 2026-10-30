using System;
using System.Windows.Controls;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>A big "time to focus" / "time for a break" message that briefly covers every screen.</summary>
public partial class FlashWindow : Window
{
    private static readonly List<FlashWindow> Open = new();

    private readonly System.Drawing.Rectangle _screen;
    /// <summary>How long the message stays up (from Settings).</summary>
    public static double HoldSeconds { get; set; } = 4;

    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromSeconds(HoldSeconds) };
    private bool _closing;

    /// <summary>Shows the message on every connected screen at once.</summary>
    public static void ShowOnAllScreens(string headline, string detail, Brush accent) =>
        ShowOnAllScreens(headline, detail, accent, null, "", "", Array.Empty<string>());

    /// <summary>
    /// The full-screen alert, in the app's current look: a dimmed backdrop and a card with an icon,
    /// the headline, what's starting, a time chip, and (for focus) the session's first few tasks.
    /// </summary>
    public static void ShowOnAllScreens(string headline, string detail, Brush accent, Brush? tint, string glyph, string time, IReadOnlyList<string> tasks)
    {
        CloseAll();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            try
            {
                var w = new FlashWindow(screen.Bounds, headline, detail, accent, tint, glyph, time, tasks);
                Open.Add(w);
                w.Show();
            }
            catch { /* A screen being unplugged mid-way shouldn't break anything. */ }
        }
    }

    public static void CloseAll()
    {
        foreach (var w in Open.ToArray()) w.CloseNow();
        Open.Clear();
    }

    private FlashWindow(System.Drawing.Rectangle screen, string headline, string detail, Brush accent, Brush? tint, string glyph, string time, IReadOnlyList<string> tasks)
    {
        InitializeComponent();
        _screen = screen;
        HeadText.Text = headline;
        SubText.Text = detail;
        SubText.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        IconGlyph.Text = glyph.Length > 0 ? glyph : "\uE916";
        IconGlyph.Foreground = accent;
        if (tint != null) IconBack.Fill = tint;
        CountdownBar.Background = accent;
        TimeText.Text = time;
        TimeChip.Visibility = time.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var task in tasks.Take(3))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 13, Height = 13, Stroke = accent, StrokeThickness = 1.4, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = task, FontSize = 14, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 });
            TaskPanel.Children.Add(row);
        }
        if (tasks.Count > 3)
            TaskPanel.Children.Add(new TextBlock { Text = $"+{tasks.Count - 3} more", FontSize = 12, Margin = new Thickness(23, 0, 0, 0), Foreground = SubText.Foreground });
        TaskPanel.Visibility = tasks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Cover this screen exactly. Screen.Bounds and SetWindowPos use the same pixel units.
        // It's applied again once loaded, in case moving to a screen with different scaling resized it.
        SourceInitialized += (_, _) => CoverScreen();
        Loaded += (_, _) =>
        {
            CoverScreen();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            // The bar runs down for as long as the card stays up.
            CountdownBar.BeginAnimation(WidthProperty, new DoubleAnimation(CountdownBar.Width, 0, _holdTimer.Interval));
            _holdTimer.Start();
        };
        _holdTimer.Tick += (_, _) => FadeOut();
        Closed += (_, _) =>
        {
            _holdTimer.Stop();
            Open.Remove(this);
        };
    }

    private void CoverScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, HwndTopmost, _screen.Left, _screen.Top, _screen.Width, _screen.Height,
            SwpNoActivate | SwpShowWindow);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Dismissing one screen dismisses them all.
        foreach (var w in Open.ToArray()) w.FadeOut();
    }

    private void FadeOut()
    {
        if (_closing) return;
        _closing = true;
        _holdTimer.Stop();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(400));
        fade.Completed += (_, _) => CloseNow();
        BeginAnimation(OpacityProperty, fade);
    }

    private void CloseNow()
    {
        _closing = true;
        _holdTimer.Stop();
        if (IsLoaded || IsVisible)
        {
            try { Close(); } catch (InvalidOperationException) { }
        }
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
