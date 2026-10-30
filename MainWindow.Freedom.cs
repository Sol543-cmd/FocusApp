using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>
/// Freedom in Focus: the Freedom window (sign in, your blocklists and devices exactly as Freedom has
/// them, start a session) and starting Freedom with each focus session. See FreedomService.cs.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _freedomSyncTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private Window? _freedomWindow;
    private Action? _refreshFreedomWindow;

    private void SetUpFreedom()
    {
        FreedomService.Load();
        FreedomService.Changed += () => Dispatcher.BeginInvoke(new Action(() => { UpdateFreedomSummary(); _refreshFreedomWindow?.Invoke(); }));
        _runner.BlockStarted += FreedomBlockStarted;
        _freedomSyncTimer.Tick += async (_, _) => await SyncFreedomQuietly();
        _freedomSyncTimer.Start();
        _tray.ContextMenuStrip?.Items.Insert(1, new System.Windows.Forms.ToolStripMenuItem("Freedom…", null, (_, _) => { ShowFromTray(); OpenFreedomWindow(); }));
        UpdateFreedomSummary();
        Dispatcher.BeginInvoke(new Action(async () => await SyncFreedomQuietly()), DispatcherPriority.ApplicationIdle);
    }

    private async Task SyncFreedomQuietly()
    {
        if (!FreedomService.SignedIn) return;
        try { await FreedomService.SyncAsync(); }
        catch (Exception ex) { Log.Write("Freedom sync: " + ex.Message); }
    }

    /// <summary>A focus session started: start Freedom for the same length, with the ticked blocklists and devices.</summary>
    private async void FreedomBlockStarted(PlanBlock block)
    {
        if (!block.IsFocus || _resuming) return;
        var data = FreedomService.Data;
        if (!data.StartWithFocus || !FreedomService.SignedIn) return;
        int minutes = (int)Math.Ceiling(_runner.Remaining.TotalMinutes);
        if (minutes < 1) return;
        // Freedom is already blocking until at least the end of this session (back-to-back sessions).
        if (data.LastEndUtc > DateTime.UtcNow.AddMinutes(minutes)) return;
        try
        {
            await FreedomService.SyncAsync(); // a list deleted in Freedom since the last sync isn't sent
            if (data.BlocklistIds.Count == 0 || data.DeviceIds.Count == 0)
            {
                _toast.ShowMessage("Freedom didn't start", "No Freedom blocklist or device is ticked any more. Open Freedom from the tray menu to choose.");
                return;
            }
            await FreedomService.StartAsync(data.BlocklistIds, data.DeviceIds, minutes);
        }
        catch (Exception ex)
        {
            Log.Write("Freedom start with focus: " + ex.Message);
            _toast.ShowMessage("Freedom didn't start", ex.Message + " Focus's own blocking is still on.", "Open Freedom", OpenFreedomWindow);
        }
    }

    private void UpdateFreedomSummary()
    {
        if (FindName("FreedomSettingsSummary") is not TextBlock summary) return;
        var d = FreedomService.Data;
        summary.Text = !FreedomService.SignedIn
            ? "Not connected. Connect to start your Freedom blocklists with each focus session."
            : d.StartWithFocus
                ? $"Starts with each focus session: {Plural(d.BlocklistIds.Count, "blocklist")} on {Plural(d.DeviceIds.Count, "device")}."
                : $"Connected as {d.Email}. Not starting with focus sessions.";
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private void OpenFreedom_Click(object sender, RoutedEventArgs e) => OpenFreedomWindow();

    private void OpenFreedomWindow()
    {
        if (_freedomWindow != null) { _freedomWindow.Activate(); return; }

        var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        var scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var window = new Window
        {
            Title = "Freedom", Width = 560, Height = 720, MinHeight = 400, Content = scroll,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = IsVisible ? this : null
        };
        window.SetResourceReference(BackgroundProperty, "Panel");
        window.SetResourceReference(ForegroundProperty, "Text");
        _freedomWindow = window;

        TextBlock Caption(string text, double top = 4)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
            t.SetResourceReference(StyleProperty, "Caption");
            return t;
        }
        TextBlock Sub(string text)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(0, 22, 0, 6) };
            t.SetResourceReference(StyleProperty, "SubHeading");
            return t;
        }
        Button Link(string text, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            b.SetResourceReference(StyleProperty, "LinkButton");
            b.Click += (_, _) => click();
            return b;
        }

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontWeight = FontWeights.SemiBold };
        void Say(string text) => status.Text = text;
        bool busy = false;
        async Task Run(string doing, Func<Task> work, string done)
        {
            if (busy) return;
            busy = true;
            Say(doing);
            Mouse.OverrideCursor = Cursors.AppStarting;
            try { await work(); Say(done); }
            catch (Exception ex) { Say(ex.Message); }
            finally { busy = false; Mouse.OverrideCursor = null; Refresh(); }
        }

        var body = new StackPanel();

        void Refresh()
        {
            body.Children.Clear();
            var d = FreedomService.Data;
            if (!FreedomService.SignedIn)
            {
                body.Children.Add(Caption("Connect your Freedom account. Focus then shows your Freedom blocklists and devices, " +
                                          "exactly as the Freedom tray menu has them, and can start Freedom with each focus session.", 0));
                var email = new TextBox { Text = d.Email, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(6, 4, 6, 4) };
                var password = new PasswordBox { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(6, 4, 6, 4) };
                body.Children.Add(Caption("Freedom email", 14));
                body.Children.Add(email);
                body.Children.Add(Caption("Freedom password", 10));
                body.Children.Add(password);
                var signIn = new Button { Content = "Connect", Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(18, 6, 18, 6) };
                signIn.SetResourceReference(StyleProperty, "PrimaryButton");
                async void Connect()
                {
                    string mail = email.Text.Trim(), pass = password.Password;
                    if (mail.Length == 0 || pass.Length == 0) { Say("Enter your Freedom email and password."); return; }
                    password.Clear();
                    await Run("Connecting to Freedom…", () => FreedomService.SignInAsync(mail, pass), "Connected. Your blocklists and devices are below.");
                }
                signIn.Click += (_, _) => Connect();
                password.KeyDown += (_, e) => { if (e.Key == Key.Enter) Connect(); };
                body.Children.Add(signIn);
                body.Children.Add(Caption("Your password goes only to Freedom and isn't saved. Focus keeps Freedom's sign-in, " +
                                          "encrypted with your Windows account, on this PC only.", 10));
                return;
            }

            string synced = d.LastSyncUtc == default ? "not yet" : d.LastSyncUtc.ToLocalTime().ToString("t");
            body.Children.Add(Caption($"Connected as {d.Email}. Last synced with Freedom {synced}.", 0));
            var links = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            links.Children.Add(Link("Sync Freedom", async () => await Run("Syncing with Freedom…", FreedomService.SyncAsync, "Synced. Focus now matches Freedom.")));
            links.Children.Add(Link("Launch Freedom dashboard", () => OpenWeb("https://freedom.to/dashboard")));
            links.Children.Add(Link("Test connection", async () => await Run("Testing…", async () => await FreedomService.TestAsync(), "Test finished. Freedom's replies are in log.txt (Settings, Data, Open data folder).")));
            links.Children.Add(Link("Disconnect", () => { FreedomService.SignOut(); Say("Disconnected from Freedom."); }));
            body.Children.Add(links);

            var auto = new CheckBox { Content = "Start Freedom with every focus session", IsChecked = d.StartWithFocus, Margin = new Thickness(0, 18, 0, 0), FontWeight = FontWeights.SemiBold };
            auto.Checked += (_, _) => { d.StartWithFocus = true; FreedomService.Save(); };
            auto.Unchecked += (_, _) => { d.StartWithFocus = false; FreedomService.Save(); };
            body.Children.Add(auto);
            body.Children.Add(Caption("Freedom runs for the length of the session, on the devices and with the blocklists ticked below. " +
                                      "Back-to-back sessions extend it. Breaks aren't blocked by Freedom.", 4));

            void AddChecks(string heading, List<FreedomItem> items, List<string> chosen, string empty)
            {
                body.Children.Add(Sub(heading));
                if (items.Count == 0) { body.Children.Add(Caption(empty, 0)); return; }
                foreach (var item in items)
                {
                    var check = new CheckBox { Content = item.Name, IsChecked = chosen.Contains(item.Id), Margin = new Thickness(0, 4, 0, 4) };
                    string id = item.Id;
                    check.Checked += (_, _) => { if (!chosen.Contains(id)) chosen.Add(id); FreedomService.Save(); };
                    check.Unchecked += (_, _) => { chosen.Remove(id); FreedomService.Save(); };
                    body.Children.Add(check);
                }
            }
            AddChecks("Blocklists", d.Blocklists, d.BlocklistIds, "No blocklists in Freedom yet. Make one in the Freedom dashboard, then Sync Freedom.");
            AddChecks("Devices", d.Devices, d.DeviceIds, "No devices in Freedom yet.");

            body.Children.Add(Sub("Start a session now"));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var minutesBox = new TextBox { Text = "60", Width = 60, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
            row.Children.Add(minutesBox);
            row.Children.Add(new TextBlock { Text = "minutes", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 16, 0) });
            var start = new Button { Content = "Start Freedom", Padding = new Thickness(18, 6, 18, 6) };
            start.SetResourceReference(StyleProperty, "PrimaryButton");
            start.Click += async (_, _) =>
            {
                if (!int.TryParse(minutesBox.Text.Trim(), out int m) || m < 1 || m > 1440) { Say("Enter the minutes as a number from 1 to 1440."); return; }
                await Run("Starting Freedom…", async () =>
                {
                    await FreedomService.SyncAsync();
                    await FreedomService.StartAsync(d.BlocklistIds, d.DeviceIds, m);
                }, $"Freedom is on for {m} minutes.");
            };
            row.Children.Add(start);
            body.Children.Add(row);
            body.Children.Add(Caption("Uses the blocklists and devices ticked above. Stopping a Focus session early doesn't stop Freedom; " +
                                      "it ends on its own (or end it from the Freedom tray icon if your account allows).", 8));
        }

        var title = new TextBlock { Text = "Freedom" };
        title.SetResourceReference(StyleProperty, "Heading");
        root.Children.Add(title);
        root.Children.Add(body);
        root.Children.Add(status);
        _refreshFreedomWindow = () => { if (!busy) Refresh(); };
        window.Closed += (_, _) => { _freedomWindow = null; _refreshFreedomWindow = null; };
        Refresh();
        window.Show();
        if (FreedomService.SignedIn)
            _ = Run("Syncing with Freedom…", FreedomService.SyncAsync, "Synced. Focus now matches Freedom.");
    }

    private static void OpenWeb(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("OpenWeb", ex); }
    }
}
