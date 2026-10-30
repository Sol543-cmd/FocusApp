using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace FocusApp;

/// <summary>
/// Website blocking through the Windows "hosts" file: during focus, the sites you list point
/// nowhere, in every browser. Changing that file needs administrator permission, so Focus starts a
/// tiny copy of itself that Windows asks you to approve; it only edits Focus's own marked section.
/// </summary>
public static class HostsBlocker
{
    private const string Begin = "# >>> Focus website blocking (added by Focus; removed automatically)";
    private const string End = "# <<< Focus website blocking";

    public static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    /// <summary>True if Focus's blocking section is in the hosts file right now. Reading needs no permission.</summary>
    public static bool IsActive()
    {
        try { return File.ReadAllText(HostsPath).Contains(Begin); }
        catch { return false; }
    }

    /// <summary>Cleans up what you typed into plain site names, like "youtube.com".</summary>
    public static List<string> CleanSites(IEnumerable<string> input) =>
        input.Select(s => s.Trim().ToLowerInvariant())
             .Select(s => s.Replace("https://", "").Replace("http://", ""))
             .Select(s => s.Split('/')[0])
             .Select(s => s.StartsWith("www.") ? s[4..] : s)
             .Where(s => s.Length > 3 && s.Contains('.') && s.All(c => char.IsLetterOrDigit(c) || c is '.' or '-'))
             .Distinct()
             .ToList();

    /// <summary>Asks Windows for permission and turns blocking on (with these sites) or off. Returns false if refused.</summary>
    public static bool Request(bool on, IEnumerable<string> sites)
    {
        try
        {
            string args = "--hosts off";
            if (on)
            {
                string file = Path.Combine(Storage.Folder, "blocked-sites.txt");
                Directory.CreateDirectory(Storage.Folder);
                File.WriteAllLines(file, CleanSites(sites));
                args = $"--hosts on \"{file}\"";
            }
            if (Environment.ProcessPath is not string exe) return false;
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit(30000);
            return p?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            // Most often: you chose "No" on the Windows permission question.
            Log.Write("Website blocking not changed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Runs in the approved helper copy: writes or removes Focus's section, then refreshes Windows' name cache.</summary>
    public static int RunHelper(string[] args)
    {
        try
        {
            bool on = args.Length > 1 && args[1] == "on";
            var lines = File.ReadAllLines(HostsPath).ToList();

            // Remove any earlier Focus section first.
            int b = lines.FindIndex(l => l.Trim() == Begin);
            int e = lines.FindIndex(l => l.Trim() == End);
            if (b >= 0 && e > b) lines.RemoveRange(b, e - b + 1);

            if (on && args.Length > 2 && File.Exists(args[2]))
            {
                var sites = CleanSites(File.ReadAllLines(args[2]));
                if (sites.Count > 0)
                {
                    lines.Add(Begin);
                    foreach (var site in sites)
                    {
                        lines.Add($"0.0.0.0 {site}");
                        lines.Add($"0.0.0.0 www.{site}");
                    }
                    lines.Add(End);
                }
            }
            File.WriteAllLines(HostsPath, lines, new UTF8Encoding(false));
            using var flush = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false });
            flush?.WaitForExit(10000);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("HostsHelper", ex);
            return 1;
        }
    }
}
