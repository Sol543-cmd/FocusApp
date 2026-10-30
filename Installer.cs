using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace FocusApp;

/// <summary>
/// Installs Focus for the current Windows user (no administrator needed): copies FocusApp.exe to
/// %LocalAppData%\Programs\Focus, adds a Start menu shortcut, and starts it with Windows.
/// </summary>
public static class Installer
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Focus";

    public static string InstallFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Focus");

    public static string InstalledExe => Path.Combine(InstallFolder, "FocusApp.exe");

    public static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Focus.lnk");

    public static bool IsRunningInstalledCopy =>
        Environment.ProcessPath is string exe &&
        string.Equals(Path.GetFullPath(exe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies this FocusApp.exe into place (replacing an older one) and adds the shortcut.</summary>
    public static void Install()
    {
        string source = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find this program's file.");
        Directory.CreateDirectory(InstallFolder);
        File.Copy(source, InstalledExe, overwrite: true);
        CreateShortcut(StartMenuShortcut, InstalledExe, "Focus: plan your day and block distractions");
        SetStartWithWindows(InstalledExe);
    }

    public static void SetStartWithWindows(string exe)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(RunValue, $"\"{exe}\" --startup");
    }

    /// <summary>
    /// Removes the startup entry and shortcut, then deletes the program (and, if asked, your data)
    /// a few seconds after Focus closes.
    /// </summary>
    public static void Uninstall(bool deleteData)
    {
        // Installed with FocusSetup.exe: use its uninstaller so Windows' app list stays correct.
        string setupUninstaller = Path.Combine(InstallFolder, "unins000.exe");
        if (IsRunningInstalledCopy && File.Exists(setupUninstaller))
        {
            if (deleteData)
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 8 /nobreak > nul & rmdir /s /q \"{Storage.Folder}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            Process.Start(new ProcessStartInfo(setupUninstaller) { UseShellExecute = true });
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex) { Log.Error("Uninstall.RunKey", ex); }

        try { if (File.Exists(StartMenuShortcut)) File.Delete(StartMenuShortcut); }
        catch (Exception ex) { Log.Error("Uninstall.Shortcut", ex); }

        // A running program can't delete itself, so a hidden command window waits, then cleans up.
        var cleanup = new StringBuilder("/c timeout /t 4 /nobreak > nul");
        if (IsRunningInstalledCopy) cleanup.Append($" & del /f /q \"{InstalledExe}\" & rmdir \"{InstallFolder}\"");
        if (deleteData) cleanup.Append($" & rmdir /s /q \"{Storage.Folder}\"");
        Process.Start(new ProcessStartInfo("cmd.exe", cleanup.ToString())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    // ---------- Creating a Start menu shortcut ----------

    private static void CreateShortcut(string shortcutPath, string target, string description)
    {
        object? link = null;
        try
        {
            link = new CShellLink();
            var shellLink = (IShellLinkW)link;
            shellLink.SetPath(target);
            shellLink.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            shellLink.SetDescription(description);
            shellLink.SetIconLocation(target, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath) ?? "");
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    // Methods must stay in this exact order (it matches Windows' definition).
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
