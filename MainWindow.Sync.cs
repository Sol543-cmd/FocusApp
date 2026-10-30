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
        FillSyncSettings();
    }

    private void FillSyncSettings()
    {
        SyncStatusText.Text = Sync.IsOn
            ? $"Syncing through {Sync.Folder}"
            : "Not syncing. Your data is saved on this computer only.";
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
        using var onWork = TaskStores.Use(TaskStores.Main); // background work always uses your work tasks (not Personal)
        try
        {
            string? editingId = _editingList?.Id;
            BlockLists.Load();
            AdoptSharedPlanDefaults();
            AdoptSharedPracticeTimes();
            _editingList = editingId != null ? BlockLists.Find(editingId) : null;
            BlockLists_Changed();

            _layouts.Clear();
            foreach (var layout in Storage.Load<System.Collections.Generic.List<DayLayout>>("layouts.json").OrderBy(l => l.Name))
                _layouts.Add(layout);

            Projects.Reload();        // merges too: projects added on the other computer arrive
            ResolvedTasks.Reload();   // merges, so nothing done here is lost
            TaskBin.Reload();
            PersonalTasks.Reload();   // Personal's tasks, finished list and projects (including ones added on the phone)
            if (!VocabWindow.IsShowing) { Vocab.Save(); Alphabet.Instance.Save(); } // merge in the other computer's progress
            RefreshTasksTab();
            UpdateTodayCapacity();

            if (force) _stats = null; // reload this computer's totals from the new place
            else _stats = _stats != null ? FocusStatsReload(_stats) : null;

            // The plan on screen: reload if it changed elsewhere, unless you're editing it or it's running.
            if (!ViewingRunningDay && !_planDirty && !BlockPopup.IsOpen && (force || PlanFileStamp(_planDate) != _planFileStamp))
                LoadPlanForDate(_planDate);

            UpdateDayHeader();
            RefreshCalendarTab();
            RefreshDashboard(full: true);
        }
        catch (Exception ex)
        {
            Log.Error("ReloadSharedData", ex);
        }
    }

    /// <summary>Keeps this computer's unsaved minutes and picks up the other computer's totals.</summary>
    private static FocusStats FocusStatsReload(FocusStats current)
    {
        current.Save();
        return FocusStats.Load();
    }
}
