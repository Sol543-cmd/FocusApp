using System;
using System.Windows;
using Microsoft.Win32;

namespace FocusApp;

/// <summary>Switches between the light and dark color sets in the Themes folder.</summary>
public static class ThemeManager
{
    private static string _current = "System";

    /// <summary>Whether the current theme is a dark one (for the window title bars).</summary>
    public static bool IsDark { get; private set; }

    /// <summary>The color theme: Classic, Mono or Calm (each has a light and a dark version).</summary>
    public static string ColorTheme { get; set; } = "Classic";

    /// <summary>Raised after colors change, so screens that draw their own colors can redraw.</summary>
    public static event Action? Changed;
    private static bool _listening;

    /// <param name="theme">"Light", "Dark", or "System" (follow the Windows setting).</param>
    public static void Apply(string theme)
    {
        _current = theme;
        bool dark = theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => WindowsUsesDarkApps()
        };

        try
        {
            var colors = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Themes/{(ColorTheme == "Classic" ? "" : ColorTheme)}{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute)
            };
            var merged = Application.Current.Resources.MergedDictionaries;
            if (merged.Count > 0) merged[0] = colors;
            else merged.Add(colors);
        }
        catch (Exception ex)
        {
            Log.Error("ThemeManager", ex);
        }
        IsDark = dark;
        foreach (Window w in Application.Current.Windows) TitleBar.Apply(w);
        Changed?.Invoke();

        if (!_listening)
        {
            // Follow Windows when its light/dark setting changes.
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _current == "System")
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() => Apply("System")));
            };
        }
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }
}
