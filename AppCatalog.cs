using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;

namespace FocusApp;

/// <summary>An app shown in the "apps to block" picker.</summary>
public class AppChoice : INotifyPropertyChanged
{
    private bool _isChecked;

    public string Name { get; init; } = "";
    public string Process { get; init; } = "";
    public string PackageName { get; init; } = "";
    public bool IsRunning { get; init; }

    /// <summary>False when this app can't be unchecked (it's blocked by a running session).</summary>
    public bool CanChange { get; init; } = true;
    public string Key => BlockedApp.KeyFor(Process, PackageName);

    public string Detail
    {
        get
        {
            string kind = PackageName.Length > 0 ? "Microsoft Store app" : $"{Process}.exe";
            return IsRunning ? $"{kind}, open now" : kind;
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; OnChanged(); }
    }

    public BlockedApp ToBlockedApp() => new() { Name = Name, Process = Process, PackageName = PackageName };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Finds the apps on this PC: Start menu shortcuts, installed Microsoft Store apps (like WhatsApp),
/// and anything open right now.
/// </summary>
public static class AppCatalog
{
    // Parts of Windows (and this app) that must never be blocked.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "focusapp", "explorer", "dwm", "csrss", "winlogon", "wininit", "lsass", "services", "svchost",
        "smss", "system", "idle", "registry", "sihost", "ctfmon", "fontdrvhost", "searchhost", "searchui",
        "searchapp", "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "runtimebroker",
        "applicationframehost", "lockapp", "logonui", "taskmgr", "conhost", "openconsole", "windowsterminal",
        "cmd", "powershell", "pwsh", "dotnet", "securityhealthsystray", "msedgewebview2", "widgets",
        "rundll32", "msiexec", "mmc", "control", "systemsettings", "wwahost", "backgroundtaskhost",
        "dllhost", "audiodg", "spoolsv", "wudfhost", "consent", "userinit", "smartscreen", "crossdeviceresume"
    };

    // Store packages that are part of Windows rather than apps you'd block.
    private static readonly string[] SystemPackagePrefixes =
    {
        "Microsoft.Windows.", "MicrosoftWindows.", "Windows.", "Microsoft.UI.", "Microsoft.VCLibs",
        "Microsoft.NET.", "Microsoft.Services.Store", "Microsoft.WindowsTerminal", "Microsoft.Win32WebViewHost",
        "Microsoft.AAD.", "Microsoft.AccountsControl", "Microsoft.LockApp", "Microsoft.ECApp",
        "Microsoft.CredDialogHost", "Microsoft.SecHealthUI", "Microsoft.WindowsAppRuntime",
        "Microsoft.DesktopAppInstaller", "Microsoft.WebExperience", "Microsoft.BioEnrollment",
        "Microsoft.XboxGameCallableUI", "Microsoft.Advertising", "Microsoft.StorePurchaseApp"
    };

    private static readonly string[] SkipWords =
        { "uninstall", "uninst", "readme", "read me", "help", "website", "documentation", "release notes", "setup", "license" };

    public static bool IsProtected(string process) =>
        string.IsNullOrWhiteSpace(process) || Protected.Contains(process);

    private static bool IsSystemPackage(string package) =>
        SystemPackagePrefixes.Any(p => package.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads the app list on a background thread (shortcut reading needs its own STA thread).</summary>
    public static Task<List<AppChoice>> LoadAllAsync()
    {
        var tcs = new TaskCompletionSource<List<AppChoice>>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(LoadAll()); }
            catch (Exception ex)
            {
                Log.Error("AppCatalog", ex);
                tcs.SetResult(new List<AppChoice>());
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private sealed class Entry
    {
        public string Name = "";
        public string Process = "";
        public string Package = "";
        public bool Running;
    }

    private static List<AppChoice> LoadAll()
    {
        var byKey = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string process, string package, bool running)
        {
            string key = BlockedApp.KeyFor(process, package);
            if (byKey.TryGetValue(key, out var e))
            {
                e.Running |= running;
                if (!running && name.Length > 0 && name.Length < e.Name.Length) e.Name = name;
            }
            else
            {
                byKey[key] = new Entry { Name = name, Process = process, Package = package, Running = running };
            }
        }

        foreach (var (name, process) in Safe(LoadInstalled)) Add(name, process, "", false);
        foreach (var (name, process, package) in Safe(LoadStoreApps)) Add(name, process, package, false);
        foreach (var (name, process, package) in Safe(LoadRunning))
        {
            // A running Store app joins its installed entry, keeping the nicer installed name.
            if (package.Length > 0 && byKey.TryGetValue(BlockedApp.KeyFor(process, package), out var e))
                e.Running = true;
            else
                Add(name, process, package, true);
        }

        return byKey.Values
            .Select(e => new AppChoice { Name = e.Name, Process = e.Process, PackageName = e.Package, IsRunning = e.Running })
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<T> Safe<T>(Func<IEnumerable<T>> load)
    {
        try { return load().ToList(); }
        catch (Exception ex)
        {
            Log.Error("AppCatalog", ex);
            return new List<T>();
        }
    }

    private static IEnumerable<(string Name, string Process)> LoadInstalled()
    {
        var results = new List<(string, string)>();
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;

            foreach (var lnk in Directory.EnumerateFiles(folder, "*.lnk", options))
            {
                string name = Path.GetFileNameWithoutExtension(lnk);
                string lower = name.ToLowerInvariant();
                if (SkipWords.Any(w => lower.Contains(w))) continue;

                var (target, args) = ShellLink.Resolve(lnk);
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                string process = Path.GetFileNameWithoutExtension(target);

                // Some apps (Discord, for example) launch through Update.exe --processStart RealApp.exe
                if (process.Equals("update", StringComparison.OrdinalIgnoreCase))
                {
                    string? real = ProcessStartArg(args);
                    if (real == null) continue;
                    process = real;
                }

                if (IsProtected(process)) continue;
                results.Add((name, process));
            }
        }
        return results;
    }

    /// <summary>Installed Microsoft Store apps, read from Windows' list of app packages for this user.</summary>
    private static IEnumerable<(string Name, string Process, string Package)> LoadStoreApps()
    {
        var results = new List<(string, string, string)>();
        const string root = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        using var key = Registry.CurrentUser.OpenSubKey(root);
        if (key == null) return results;

        foreach (var fullName in key.GetSubKeyNames())
        {
            try
            {
                string package = fullName.Split('_')[0];
                if (package.Length == 0 || IsSystemPackage(package)) continue;

                string? folder, registryName;
                using (var pk = key.OpenSubKey(fullName))
                {
                    folder = pk?.GetValue("PackageRootFolder") as string;
                    registryName = pk?.GetValue("DisplayName") as string;
                }
                if (string.IsNullOrEmpty(folder)) continue;
                if (!string.IsNullOrEmpty(windows) && folder.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;

                string manifestPath = Path.Combine(folder, "AppxManifest.xml");
                if (!File.Exists(manifestPath)) continue;
                var manifest = XDocument.Load(manifestPath);

                bool isFramework = manifest.Descendants()
                    .Any(e => e.Name.LocalName == "Framework" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
                if (isFramework) continue;

                foreach (var app in manifest.Descendants().Where(e => e.Name.LocalName == "Application"))
                {
                    string? exe = app.Attribute("Executable")?.Value;
                    if (string.IsNullOrWhiteSpace(exe) || exe.Contains('$')) continue;

                    bool hiddenFromStart = app.Descendants().Any(e =>
                        e.Name.LocalName == "VisualElements" &&
                        string.Equals(e.Attribute("AppListEntry")?.Value, "none", StringComparison.OrdinalIgnoreCase));
                    if (hiddenFromStart) continue;

                    string process = Path.GetFileNameWithoutExtension(exe);
                    if (IsProtected(process)) continue;

                    string name = ResolveName(registryName) ?? ManifestName(manifest) ?? FriendlyPackageName(package);
                    results.Add((name, process, package));
                    break; // one entry per app; blocking covers every program inside it
                }
            }
            catch
            {
                // Skip any package we can't read.
            }
        }
        return results;
    }

    private static string? ResolveName(string? registryName)
    {
        if (string.IsNullOrWhiteSpace(registryName)) return null;
        if (!registryName.StartsWith("@", StringComparison.Ordinal))
            return registryName.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase) ? null : registryName;

        var buffer = new StringBuilder(512);
        if (SHLoadIndirectString(registryName, buffer, buffer.Capacity, IntPtr.Zero) != 0) return null;
        string name = buffer.ToString().Trim();
        return name.Length == 0 || name.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    private static string? ManifestName(XDocument manifest)
    {
        var name = manifest.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "DisplayName" && e.Parent?.Name.LocalName == "Properties")
            ?.Value.Trim();
        return string.IsNullOrEmpty(name) || name.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    private static string FriendlyPackageName(string package)
    {
        int dot = package.LastIndexOf('.');
        return dot >= 0 && dot < package.Length - 1 ? package[(dot + 1)..] : package;
    }

    private static string? ProcessStartArg(string args)
    {
        const string flag = "--processStart";
        int i = args.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string rest = args[(i + flag.Length)..].Trim().Trim('"');
        int end = rest.IndexOfAny(new[] { ' ', '"' });
        if (end >= 0) rest = rest[..end];
        rest = Path.GetFileNameWithoutExtension(rest);
        return rest.Length > 0 ? rest : null;
    }

    private static IEnumerable<(string Name, string Process, string Package)> LoadRunning()
    {
        var results = new List<(string, string, string)>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    string process = p.ProcessName;
                    if (IsProtected(process)) continue;

                    string? path = ProcessTools.GetPath(p.Id);
                    string package = ProcessTools.PackageNameFromPath(path) ?? "";
                    if (package.Length > 0 && IsSystemPackage(package)) continue;

                    string display = process;
                    try
                    {
                        if (path != null)
                        {
                            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                            if (!string.IsNullOrWhiteSpace(description)) display = description.Trim();
                        }
                    }
                    catch { /* The program name is fine. */ }

                    results.Add((display, process, package));
                }
                catch { /* Program exited while we were looking at it. */ }
            }
        }
        return results;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int outputSize, IntPtr reserved);

    /// <summary>Reads the target of a Windows .lnk shortcut.</summary>
    private static class ShellLink
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

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
        }

        public static (string Target, string Args) Resolve(string lnkPath)
        {
            object? link = null;
            try
            {
                link = new CShellLink();
                ((IPersistFile)link).Load(lnkPath, 0);
                var shellLink = (IShellLinkW)link;

                var path = new StringBuilder(1024);
                shellLink.GetPath(path, path.Capacity, IntPtr.Zero, 0);
                var args = new StringBuilder(2048);
                shellLink.GetArguments(args, args.Capacity);

                return (Environment.ExpandEnvironmentVariables(path.ToString()), args.ToString());
            }
            catch
            {
                return ("", "");
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
            }
        }
    }
}
