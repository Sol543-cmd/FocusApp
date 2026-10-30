using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FocusApp;

public partial class App : Application
{
#if DEBUG
    private const string InstanceId = "FocusApp.Debug";
#else
    private const string InstanceId = "FocusApp";
#endif

    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private EventWaitHandle? _showSignal;
    private DateTime _lastErrorShownUtc = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HookErrorHandling();
        AutoScroll.Register(); // middle-button scrolling in every window
        TitleBar.Register();   // title bars match the theme

        // Website blocking helper (started with Windows' permission): edit the hosts file and exit.
        if (e.Args.Length > 0 && e.Args[0].Equals("--hosts", StringComparison.OrdinalIgnoreCase))
        {
            int code = HostsBlocker.RunHelper(e.Args);
            Shutdown(code);
            return;
        }

        // Helper mode: a tiny invisible copy of Focus that reopens it if it's force-closed
        // during an extreme locked session. It never shows any windows.
        int watchIndex = Array.FindIndex(e.Args, a => a.Equals("--watchdog", StringComparison.OrdinalIgnoreCase));
        if (watchIndex >= 0)
        {
            int.TryParse(e.Args.ElementAtOrDefault(watchIndex + 1), out int parentId);
            RunWatchdog(parentId);
            return;
        }

        bool startHidden = e.Args.Any(a =>
            a.Equals("--startup", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--resume", StringComparison.OrdinalIgnoreCase));

#if !DEBUG
        // Opened from a download or USB stick: offer to install (or update) Focus for this user.
        if (!startHidden && !Installer.IsRunningInstalledCopy && OfferInstall())
        {
            Shutdown();
            return;
        }
#endif

        // Only one copy of Focus runs at a time. Opening it again brings the running copy to the front.
        _singleInstance = new Mutex(true, InstanceId + ".Instance", out bool isFirst);
        if (!isFirst)
        {
            // If the earlier copy was force-closed, its lock is left behind. Take it over.
            try { isFirst = _singleInstance.WaitOne(0); }
            catch (AbandonedMutexException) { isFirst = true; }
        }
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(InstanceId + ".Show").Set(); } catch { }
            Shutdown();
            return;
        }
        _ownsMutex = true;
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceId + ".Show");

        var window = new MainWindow();
        MainWindow = window;

        // When Windows starts Focus at sign-in, or the helper reopens it, it waits quietly in the tray.
        if (!startHidden) window.Show();

        var signal = _showSignal;
        var listener = new Thread(() =>
        {
            while (true)
            {
                signal.WaitOne();
                Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
            }
        }) { IsBackground = true };
        listener.Start();
    }

    /// <summary>
    /// Asks whether to install this copy. Returns true if this copy should close
    /// (it installed and started the installed copy, or you chose Cancel).
    /// </summary>
    private bool OfferInstall()
    {
        bool update = System.IO.File.Exists(Installer.InstalledExe);
        var answer = MessageBox.Show(
            (update ? "Update Focus on this PC with this copy?" : "Install Focus on this PC?") + "\n\n" +
            "Yes: install it for your Windows account (recommended). It's added to the Start menu and starts when you sign in. " +
            "Your plans and settings are kept.\n\n" +
            "No: just run this copy without installing.",
            "Focus", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Cancel) return true;
        if (answer == MessageBoxResult.No) return false;

        if (Mutex.TryOpenExisting(InstanceId + ".Instance", out var running))
        {
            running.Dispose();
            MessageBox.Show("Focus is running right now. Quit it first (right-click its icon by the clock, then Quit Focus), " +
                            "then open this file again.", "Focus", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        try
        {
            Installer.Install();
            Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Install", ex);
            MessageBox.Show("Couldn't install Focus: " + ex.Message + "\n\nThis copy will run without installing.",
                "Focus", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void HookErrorHandling()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI", args.Exception);
            args.Handled = true;

            // Never pile up error boxes: show at most one every 10 minutes. Everything goes to the log.
            if (DateTime.UtcNow - _lastErrorShownUtc < TimeSpan.FromMinutes(10)) return;
            _lastErrorShownUtc = DateTime.UtcNow;
            MessageBox.Show(
                "Something went wrong:\n\n" + args.Exception.Message +
                "\n\nFocus will keep running. Details are saved in log.txt (Settings, Open data folder).",
                "Focus", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error("Background", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Task", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// Waits for the main Focus window process to exit. If it was force-closed while an
    /// extreme locked session is running, starts Focus again so the session carries on.
    /// </summary>
    private void RunWatchdog(int parentId)
    {
        var thread = new Thread(() =>
        {
            try
            {
                Process? parent = null;
                try { parent = Process.GetProcessById(parentId); } catch { }

                while (parent != null)
                {
                    if (parent.WaitForExit(2000)) break;

                    // Nothing left to guard: the extreme session ended normally.
                    if (!Storage.TryLoad<RunState>("run-state.json", out var current) || !current.IsActive || !current.Extreme)
                    {
                        parent.Dispose();
                        parent = null;
                        Dispatcher.BeginInvoke(new Action(Shutdown));
                        return;
                    }
                }
                parent?.Dispose();

                Thread.Sleep(1500); // let the old copy finish closing
                if (Storage.TryLoad<RunState>("run-state.json", out var state) &&
                    state.IsActive && state.Extreme && !state.WindowsShuttingDown &&
                    Environment.ProcessPath is string exe)
                {
                    Log.Write("Focus was closed during an extreme locked session; reopening it.");
                    Process.Start(new ProcessStartInfo(exe, "--resume") { UseShellExecute = false });
                }
            }
            catch (Exception ex)
            {
                Log.Error("Watchdog", ex);
            }
            Dispatcher.BeginInvoke(new Action(Shutdown));
        }) { IsBackground = true };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try { _singleInstance?.ReleaseMutex(); } catch { }
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
