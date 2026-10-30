using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FocusApp;

/// <summary>
/// Makes each window's title bar (the strip along the top, drawn by Windows) match Focus's theme.
/// Windows 11 takes the theme's exact background and text colors; Windows 10 switches to dark or light.
/// Windows without a title bar (the timer, alerts) are left alone.
/// </summary>
public static class TitleBar
{
    private const int DwmUseDarkMode = 20, DwmUseDarkModeOld = 19, DwmCaptionColor = 35, DwmTextColor = 36, DwmBorderColor = 34;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Every window gets its title bar themed as it opens (registered once at startup).</summary>
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) Apply(w); }));

    public static void Apply(Window window)
    {
        try
        {
            if (window.WindowStyle == WindowStyle.None) return; // no title bar to color
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = ThemeManager.IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmUseDarkMode, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmUseDarkModeOld, ref dark, sizeof(int)); // older Windows 10
            // Windows 11: the theme's own colors (ignored on Windows 10, which keeps the dark/light switch above).
            if (Color("Bg") is int caption) DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
            if (Color("Text") is int text) DwmSetWindowAttribute(hwnd, DwmTextColor, ref text, sizeof(int));
            if (Color("Bg") is int border) DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
        }
        catch (Exception ex) { Log.Error("TitleBar.Apply", ex); }
    }

    /// <summary>A theme color as Windows wants it (0x00BBGGRR).</summary>
    private static int? Color(string key)
    {
        if (Application.Current.TryFindResource(key) is not SolidColorBrush b) return null;
        var c = b.Color;
        return c.R | (c.G << 8) | (c.B << 16);
    }
}
