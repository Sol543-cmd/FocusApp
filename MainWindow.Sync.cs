using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace FocusApp;

/// <summary>Turning sync on and off, and showing changes made on the other computer.</summary>
public partial class MainWindow
{
    private DateTime _planFileStamp = DateTime.MinValue;

    private void SetUpSync()
    {
        Sync.RemoteChanged += () => ReloadSharedData(force: false);
        SetUpSyncTriggers();
        FillSyncSettings();
    }

    private void FillSyncSettings()
    {
        SyncStatusText.Text = Sync.IsOn
            ? $"Syncing through {Sync.Folder}"
            : "Not syncing. Your data is saved on this computer only.";
        FillSyncStatus();
        SyncNowBtn.IsEnabled = Sync.IsOn;
        SyncStopBtn.IsEnabled = Sync.IsOn;
        SyncOneDriveBtn.IsEnabled = Sync.SuggestedOneDriveFolder() != null;
    }

    private static DateTime PlanFileStamp(DateTime date)
    {
        string path = Storage.PathFor(DayPlanFile.FileFor(date));
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    private void SyncOneDrive_Click(object sender, RoutedEventArgs e)
    {
        string? folder = Sync.SuggestedOneDriveFolder();
        if (folder == null)
        {
            Info("OneDrive isn't set up on this computer. Sign in to OneDrive first, or use Choose folder.", "Sync");
            return;
        }
        EnableSync(folder);
    }

    private void SyncChoose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder to sync Focus through (it should be inside OneDrive)",
            InitialDirectory = Path.GetDirectoryName(Sync.SuggestedOneDriveFolder() ?? "") ??
                               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == true) EnableSync(dialog.FolderName);
    }

    private void EnableSync(string folder)
    {
        if (_runner.IsRunning)
        {
            Info("Turn on sync when no session is running.", "Sync");
            return;
        }
        bool hasData = File.Exists(Path.Combine(folder, "blocklists.json")) ||
                       File.Exists(Path.Combine(folder, "layouts.json")) ||
                       Directory.Exists(Path.Combine(folder, "plans"));
        string question = hasData
            ? $"This folder already has Focus data, probably from your other computer:\n{folder}\n\n" +
              "Focus will combine it with this computer's plans, layouts, block lists and task list (nothing is deleted), " +
              "then keep both computers in step from now on. Continue?"
            : $"Focus will copy this computer's plans, layouts, block lists, task list and focus totals into:\n{folder}\n\n" +
              "and use that folder from now on.\n\n" +
              "If you already turned on sync on your other computer, choose No and wait until its Focus folder " +
              "(with files in it) shows up in OneDrive on this computer, then try again. Continue?";
        if (!Ask(question, "Sync between computers")) return;

        try
        {
            SavePlanNow();
            _stats?.Save();
            Sync.Enable(folder);
        }
        catch (Exception ex)
        {
            Log.Error("EnableSync", ex);
            Info("Couldn't turn on sync: " + ex.Message, "Sync");
            return;
        }
        ReloadSharedData(force: true);
        FillSyncSettings();
        Info(hasData
                ? "Sync is on. This computer now shares its data with your other computer."
                : "Sync is on. Now open Focus on your other computer, go to Settings, and turn on sync there with the same folder.",
            "Sync");
    }

    private void SyncStop_Click(object sender, RoutedEventArgs e)
    {
        if (!Sync.IsOn) return;
        if (_runner.IsRunning)
        {
            Info("Turn off sync when no session is running.", "Sync");
            return;
        }
        if (!Ask("Stop syncing on this computer? It keeps a copy of everything as it is now, and the folder is left as it is for your other computer.",
                "Sync"))
            return;
        SavePlanNow();
        _stats?.Save();
        Sync.Disable();
        ReloadSharedData(force: true);
        FillSyncSettings();
    }

    /// <summary>Re-reads the shared files (after the other computer changed them, or sync was turned on or off).</summary>
    private void ReloadSharedData(bool force)
    {
        if (_reloading) return; // a conflict copy being tidied reloads too: once is enough
        _reloading = true;
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        // Each part on its own, so one problem (a block list, a layout) can't stop your tasks from updating.
        void Step(string name, Action a) { try { a(); } catch (Exception ex) { Log.Error("ReloadSharedData: " + name, ex); } }
        try
        {
            // Your tasks first: they're what you most need to be current.
            Step("conflicts", () => { if (!force) CheckSyncConflicts(); });
            Step("projects", Projects.Reload);         // merges too: projects added on the other computer arrive
            Step("finished", ResolvedTasks.Reload);    // merges, so nothing done here is lost
            Step("tasks", TaskBin.Reload);
            Step("personal", PersonalTasks.Reload);    // Personal's tasks, finished list and projects (including ones added on the phone)
            Step("task views", () => { RefreshTasksTab(); UpdateTodayCapacity(); UpdateOverlayTasks(); });

            Step("block lists", () =>
            {
                string? editingId = _editingList?.Id;
                BlockLists.Load();
                _editingList = editingId != null ? BlockLists.Find(editingId) : null;
                BlockLists_Changed();
            });
            Step("plan defaults", AdoptSharedPlanDefaults);
            Step("practice times", AdoptSharedPracticeTimes);
            Step("layouts", () =>
            {
                _layouts.Clear();
                foreach (var layout in Storage.Load<System.Collections.Generic.List<DayLayout>>("layouts.json").OrderBy(l => l.Name))
                    _layouts.Add(layout);
            });
            Step("vocab", () => { if (!VocabWindow.IsShowing) { Vocab.Save(); Alphabet.Instance.Save(); } }); // merge in the other computer's progress
            Step("stats", () =>
            {
                if (force) _stats = null; // reload this computer's totals from the new place
                else _stats = _stats != null ? FocusStatsReload(_stats) : null;
            });
            // The plan on screen: reload if it changed elsewhere, unless you're editing it or it's running.
            Step("plan", () =>
            {
                if (!ViewingRunningDay && !_planDirty && !BlockPopup.IsOpen && (force || PlanFileStamp(_planDate) != _planFileStamp))
                    LoadPlanForDate(_planDate);
            });
            Step("screens", () => { UpdateDayHeader(); RefreshCalendarTab(); RefreshDashboard(full: true); });
        }
        finally
        {
            _reloading = false;
            // OneDrive was still writing a file: try again shortly rather than waiting for the next change.
            if (SyncMerge.AnyWaiting) _syncRetry?.Start();
            FillSyncStatus();
        }
    }

    private bool _reloading;
    private System.Windows.Threading.DispatcherTimer? _syncRetry;
    private DateTime _lastSyncCheck;

    /// <summary>
    /// Faster, steadier syncing: retries a save or reload that OneDrive was busy with (every 10 seconds until it
    /// goes through), checks for the other computer's changes whenever Focus comes to the front and after the
    /// computer wakes, and shows when changes last arrived.
    /// </summary>
    private void SetUpSyncTriggers()
    {
        _syncRetry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _syncRetry.Tick += (_, _) =>
        {
            _syncRetry.Stop();
            ReloadSharedData(force: false); // saves what waited, and reads what arrived
        };
        SyncMerge.RetryNeeded += () => Dispatcher.BeginInvoke(new Action(() => _syncRetry?.Start()));
        Activated += (_, _) =>
        {
            if ((DateTime.Now - _lastSyncCheck).TotalSeconds < 15) return;
            _lastSyncCheck = DateTime.Now;
            Sync.CheckNow();
        };
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode != PowerModes.Resume) return;
            // Just woke up: OneDrive needs a moment to reconnect and download. Look a few times.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                foreach (var wait in new[] { 10, 30, 90 })
                {
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(wait));
                    Sync.CheckNow(force: wait == 30);
                }
            }));
        };
    }

    private void FillSyncStatus()
    {
        if (SyncStatusText == null || !Sync.IsOn) return;
        string last = Sync.LastRemoteChange is DateTime t ? $" Last change from your other computer: {Ago(t)}." : "";
        string waiting = SyncMerge.AnyWaiting ? " Waiting for OneDrive to finish with a file; trying again shortly." : "";
        SyncStatusText.Text = $"Syncing through {Sync.Folder}.{last}{waiting}";
    }

    private void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        SavePlanNow();
        Sync.CheckNow(force: true);
        ReloadSharedData(force: false);
        _toast.ShowMessage("Synced", "Checked for changes from your other computer.");
    }

    /// <summary>Keeps this computer's unsaved minutes and picks up the other computer's totals.</summary>
    private static FocusStats FocusStatsReload(FocusStats current)
    {
        current.Save();
        return FocusStats.Load();
    }
}
