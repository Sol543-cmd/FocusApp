using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>
/// Updates: "Check for updates" in the tray menu, and (with the "updates" feature on) a quiet check a few
/// minutes after start and twice a day. A newer version is only offered, never installed without asking,
/// and never while a session or break is running. See Updater.cs.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
    private DateTime _firstUpdateCheckUtc = DateTime.UtcNow.AddMinutes(3);
    private Updater.Release? _offeredUpdate;
    private string _notifiedVersion = "";
    private bool _updateFromBalloon;
    private bool _updating;

    private void SetUpUpdates()
    {
        _tray.Text = "Focus " + Updater.CurrentText;
        _tray.ContextMenuStrip?.Items.Insert(1, new System.Windows.Forms.ToolStripMenuItem(
            "Check for updates", null, (_, _) => _ = CheckForUpdatesAsync(manual: true)));

        _tray.BalloonTipClicked += (_, _) =>
        {
            if (!_updateFromBalloon) return;
            _updateFromBalloon = false;
            if (_offeredUpdate != null) _ = InstallUpdateAsync(_offeredUpdate);
        };
        _tray.BalloonTipClosed += (_, _) => _updateFromBalloon = false;

        _updateTimer.Tick += (_, _) =>
        {
            if (!Updater.Available || IsTestBuild || !_settings.On("updates")) return;
            var now = DateTime.UtcNow;
            if (now < _firstUpdateCheckUtc || now - _lastUpdateCheckUtc < TimeSpan.FromHours(12)) return;
            _ = CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updating) return;
        if (!Updater.Available)
        {
            if (manual)
                Info($"This copy of Focus ({Updater.CurrentText}) was built on this PC, so it doesn't check for updates.\n\n" +
                     "Install Focus once from the FocusSetup.exe on your GitHub Releases page, and it will update itself from then on.",
                     "Updates");
            return;
        }

        _lastUpdateCheckUtc = DateTime.UtcNow;
        Updater.Release? release;
        try { release = await Updater.CheckAsync(); }
        catch (Exception ex)
        {
            Log.Error("Updates.Check", ex);
            if (manual) Info("Couldn't check for updates (are you online?).\n\n" + ex.Message, "Updates");
            return;
        }

        if (release == null)
        {
            if (manual) Info($"You have the newest version of Focus ({Updater.CurrentText}).", "Updates");
            return;
        }

        _offeredUpdate = release;
        if (manual)
        {
            ShowFromTray();
            await InstallUpdateAsync(release);
            return;
        }

        // Automatic check: a quiet notice once per version, and never during a session or break.
        string text = release.Version.ToString(3);
        if (_runner.IsRunning || _notifiedVersion == text) return;
        _notifiedVersion = text;
        _updateFromBalloon = true;
        _tray.ShowBalloonTip(15000, "Focus " + text + " is ready",
            "Click here to update. Your plans, tasks and settings are kept.", System.Windows.Forms.ToolTipIcon.None);
    }

    private async Task InstallUpdateAsync(Updater.Release release)
    {
        if (_updating) return;
        if (_runner.IsRunning)
        {
            Info("A session or break is running. Update Focus after it ends (or stop it first), using Check for updates in the tray menu.", "Updates");
            return;
        }

        string notes = (release.Notes ?? "").Trim();
        if (notes.Length > 700) notes = notes[..700] + "…";
        string version = release.Version.ToString(3);
        if (!Ask($"Focus {version} is available (you have {Updater.CurrentText}).\n\n" +
                 (notes.Length > 0 ? "What's new:\n" + notes + "\n\n" : "") +
                 "Update now? Focus closes for a few seconds and reopens. Your plans, tasks and settings are kept.",
                 "Update Focus"))
            return;

        _updating = true;
        string tip = _tray.Text;
        try
        {
            var progress = new Progress<double>(p => _tray.Text = $"Focus: downloading update {p:P0}");
            string installer = await Updater.DownloadAsync(release, progress);

            // Inno Setup: install without the wizard, close Focus, then reopen it (FocusSetup.iss, [Run]).
            Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
            {
                UseShellExecute = true
            });
            Log.Write($"Updating from {Updater.CurrentText} to {version}");
            QuitApp();
        }
        catch (Exception ex)
        {
            Log.Error("Updates.Install", ex);
            Info("The update didn't install:\n\n" + ex.Message + "\n\nFocus keeps working as it is. You can try again later from the tray menu.", "Updates");
        }
        finally
        {
            _updating = false;
            try { _tray.Text = tip; } catch { }
        }
    }
}
