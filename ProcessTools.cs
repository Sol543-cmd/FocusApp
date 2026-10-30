using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FocusApp;

/// <summary>Small helpers for looking at and closing other programs.</summary>
internal static class ProcessTools
{
    private const string StoreFolder = "\\WindowsApps\\";

    /// <summary>Full path of a running program, or null if Windows won't say.</summary>
    public static string? GetPath(int pid)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// For Microsoft Store apps, the package name from the install path, for example
    /// "...\WindowsApps\5319275A.WhatsAppDesktop_2.24.0.0_x64__cv1g1gvanyjgm\WhatsApp.exe" gives "5319275A.WhatsAppDesktop".
    /// </summary>
    public static string? PackageNameFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int i = path.IndexOf(StoreFolder, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string rest = path.Substring(i + StoreFolder.Length);
        int underscore = rest.IndexOf('_');
        int slash = rest.IndexOf('\\');
        if (underscore <= 0 || (slash >= 0 && underscore > slash)) return null;
        return rest.Substring(0, underscore);
    }

    public static bool TryKill(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.Kill();
            return true;
        }
        catch
        {
            return false; // already gone, or running as administrator
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
