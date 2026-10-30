using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>
/// While a focus session runs, closes every blocked app, whether it opens with a window,
/// starts quietly in the background, or was already open. Apps already open when the
/// session starts get a short warning first.
/// </summary>
public class AppBlocker
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private List<BlockedApp> _apps = new();
    private HashSet<string> _graceApps = new();
    private HashSet<string> _quietOnce = new();
    private DateTime _graceEndsUtc = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _lastNotice = new();

    /// <summary>A blocked app was opened and closed again.</summary>
    public event Action<BlockedApp>? AppBlocked;

    /// <summary>Some blocked apps were already open; they'll close after the given number of seconds.</summary>
    public event Action<List<BlockedApp>, int>? WarningStarted;

    public bool IsActive => _apps.Count > 0;

    public AppBlocker() => _timer.Tick += (_, _) =>
    {
        try { Enforce(); }
        catch (Exception ex) { Log.Error("AppBlocker", ex); }
    };

    public void Begin(IEnumerable<BlockedApp> apps, int warningSeconds)
    {
        End();
        _apps = apps
            .Where(a => a.PackageName.Length > 0 || !AppCatalog.IsProtected(a.Process))
            .GroupBy(a => a.Key)
            .Select(g => g.First().Copy())
            .ToList();
        if (_apps.Count == 0) return;

        var running = Snapshot();
        var alreadyOpen = _apps.Where(a => running.Any(p => Matches(a, p))).ToList();
        if (alreadyOpen.Count > 0 && warningSeconds > 0)
        {
            _graceApps = new HashSet<string>(alreadyOpen.Select(a => a.Key));
            _quietOnce = new HashSet<string>(_graceApps);
            _graceEndsUtc = DateTime.UtcNow.AddSeconds(warningSeconds);
            WarningStarted?.Invoke(alreadyOpen, warningSeconds);
        }

        _timer.Start();
        Enforce();
    }

    /// <summary>Skip the rest of the warning and close the open apps now.</summary>
    public void CloseOpenAppsNow()
    {
        _graceEndsUtc = DateTime.MinValue;
        Enforce();
    }

    public void End()
    {
        _timer.Stop();
        _apps = new();
        _graceApps.Clear();
        _quietOnce.Clear();
        _graceEndsUtc = DateTime.MinValue;
    }

    private void Enforce()
    {
        if (_apps.Count == 0) return;
        bool inGrace = DateTime.UtcNow < _graceEndsUtc;
        var running = Snapshot();

        foreach (var app in _apps)
        {
            if (inGrace && _graceApps.Contains(app.Key)) continue;

            bool closedAny = false;
            foreach (var p in running)
                if (Matches(app, p) && ProcessTools.TryKill(p.Pid))
                    closedAny = true;
            if (!closedAny) continue;

            // Apps closed at the end of the warning don't also get a "blocked" notice.
            if (_quietOnce.Remove(app.Key)) continue;

            var now = DateTime.UtcNow;
            if (_lastNotice.TryGetValue(app.Key, out var last) && now - last < TimeSpan.FromSeconds(4)) continue;
            _lastNotice[app.Key] = now;
            AppBlocked?.Invoke(app);
        }
    }

    private readonly record struct RunningProgram(int Pid, string Name, string? Package);

    /// <summary>Every running program except Windows itself and Focus.</summary>
    private List<RunningProgram> Snapshot()
    {
        bool needPackages = _apps.Any(a => a.PackageName.Length > 0);
        int self = Environment.ProcessId;
        var list = new List<RunningProgram>();

        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.Id <= 4) continue;
                    string name = p.ProcessName;
                    if (AppCatalog.IsProtected(name)) continue;
                    string? package = needPackages ? ProcessTools.PackageNameFromPath(ProcessTools.GetPath(p.Id)) : null;
                    list.Add(new RunningProgram(p.Id, name, package));
                }
                catch
                {
                    // The program exited while we were looking.
                }
            }
        }
        return list;
    }

    private static bool Matches(BlockedApp app, RunningProgram p) =>
        (app.Process.Length > 0 && p.Name.Equals(app.Process, StringComparison.OrdinalIgnoreCase)) ||
        (app.PackageName.Length > 0 && p.Package != null &&
         p.Package.Equals(app.PackageName, StringComparison.OrdinalIgnoreCase));
}
