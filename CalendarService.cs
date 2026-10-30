using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace FocusApp;

/// <summary>A calendar Focus reads from: a private .ics link, or an .ics file you imported.</summary>
public class CalendarSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>The link, encrypted so only your Windows account can read it.</summary>
    public string ProtectedUrl { get; set; } = "";

    /// <summary>For imported files: the copy kept in the data folder.</summary>
    public string FileName { get; set; } = "";

    public DateTime? LastUpdated { get; set; }
    public string LastError { get; set; } = "";

    public bool IsFile => FileName.Length > 0;

    public string Status =>
        LastError.Length > 0
            ? $"Couldn't update: {LastError}"
            : LastUpdated is DateTime t ? $"Updated {t:g}" : "Not updated yet";
}

/// <summary>
/// Reads your calendars (one way: Focus never changes them). Only fetches the links you add,
/// only over secure https, and keeps a copy so plans still work offline.
/// </summary>
public static class CalendarService
{
    private const string SourcesFile = "calendars.json";
    private const string CacheFile = "calendar-cache.json";
    private const int MaxBytes = 15 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();
    private static List<CalendarEvent> _events = new();

    public static List<CalendarSource> Sources { get; private set; } = new();
    public static IReadOnlyList<CalendarEvent> Events => _events;
    public static event Action? Updated;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,       // .NET never follows a redirect from https to plain http
            MaxAutomaticRedirections = 5,
            UseCookies = false
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = MaxBytes };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FocusApp/1.0");
        return client;
    }

    public static void Load()
    {
        Sources = Storage.Load<List<CalendarSource>>(SourcesFile);
        _events = Storage.Load<List<CalendarEvent>>(CacheFile);
    }

    public static void SaveSources()
    {
        try { Storage.Save(SourcesFile, Sources); }
        catch (Exception ex) { Log.Error("CalendarService.SaveSources", ex); }
    }

    /// <summary>Busy (not "free") timed events on that day, for planning around.</summary>
    public static List<CalendarEvent> BusyEventsOn(DateTime day) =>
        _events.Where(e => !e.AllDay && e.Busy && e.Start < day.Date.AddDays(1) && e.End > day.Date)
               .OrderBy(e => e.Start).ToList();

    /// <summary>Every event on that day (including all-day and "free" ones), for showing.</summary>
    public static List<CalendarEvent> EventsOn(DateTime day) =>
        _events.Where(e => e.Start < day.Date.AddDays(1) && e.End > day.Date).OrderBy(e => e.Start).ToList();

    /// <summary>
    /// Checks a link before saving it. Accepts https:// and webcal:// (treated as https).
    /// Returns an explanation if it isn't usable, or null if it's fine.
    /// </summary>
    public static string? CheckLink(string url, out Uri? uri)
    {
        uri = null;
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return "That doesn't look like a web link.";
        if (parsed.Scheme != Uri.UriSchemeHttps) return "For safety, only secure links starting with https:// are allowed.";
        if (parsed.IsLoopback || parsed.HostNameType == UriHostNameType.IPv4 || parsed.HostNameType == UriHostNameType.IPv6)
            return "Use the calendar's web link, not a computer address.";
        uri = parsed;
        return null;
    }

    public static void AddLink(string name, Uri url)
    {
        Sources.Add(new CalendarSource { Name = name, ProtectedUrl = Protect(url.AbsoluteUri) });
        SaveSources();
    }

    /// <summary>Copies an .ics file into the data folder so it keeps working if the original moves.</summary>
    public static void AddFile(string name, string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes) throw new InvalidOperationException("That file is too large to be a calendar.");
        string text = File.ReadAllText(path);
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That file isn't a calendar (.ics) file.");

        var source = new CalendarSource { Name = name, FileName = $"calendar-{Guid.NewGuid():N}.ics" };
        Directory.CreateDirectory(Storage.Folder);
        File.WriteAllText(Path.Combine(Storage.Folder, source.FileName), text);
        source.LastUpdated = DateTime.Now;
        Sources.Add(source);
        SaveSources();
    }

    public static void Remove(CalendarSource source)
    {
        Sources.Remove(source);
        if (source.IsFile) Storage.Delete(source.FileName);
        SaveSources();
    }

    /// <summary>
    /// Downloads every linked calendar, reads events from a week ago to about four months ahead,
    /// and keeps the last good copy of any calendar that can't be reached right now.
    /// </summary>
    public static async Task RefreshAsync()
    {
        var from = DateTime.Today.AddDays(-7);
        var to = DateTime.Today.AddDays(120);
        var all = new List<CalendarEvent>();

        foreach (var source in Sources.ToList())
        {
            string? text = null;
            try
            {
                if (source.IsFile)
                {
                    text = await File.ReadAllTextAsync(Path.Combine(Storage.Folder, source.FileName));
                }
                else
                {
                    string url = Unprotect(source.ProtectedUrl);
                    string? problem = CheckLink(url, out var uri);
                    if (problem != null || uri == null) throw new InvalidOperationException(problem ?? "the saved link can't be used");
                    text = await Http.GetStringAsync(uri);
                    if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("the link didn't return a calendar (it may have expired)");
                    await File.WriteAllTextAsync(Path.Combine(Storage.Folder, $"calendar-{source.Id}.cache.ics"), text);
                }
                source.LastUpdated = DateTime.Now;
                source.LastError = "";
            }
            catch (Exception ex)
            {
                source.LastError = ex is TaskCanceledException ? "no response (offline?)" : ex.Message;
                Log.Write($"Calendar '{source.Name}' couldn't update: {ex.Message}");
                // Fall back to the last copy we downloaded.
                string cached = Path.Combine(Storage.Folder, $"calendar-{source.Id}.cache.ics");
                if (File.Exists(cached)) text = await File.ReadAllTextAsync(cached);
            }

            if (text != null)
            {
                try { all.AddRange(await Task.Run(() => IcsParser.Parse(text, from, to, source.Name))); }
                catch (Exception ex) { Log.Error($"Reading calendar '{source.Name}'", ex); }
            }
        }

        _events = all.OrderBy(e => e.Start).ToList();
        try { Storage.Save(CacheFile, _events); } catch (Exception ex) { Log.Error("CalendarService.Cache", ex); }
        SaveSources();
        Updated?.Invoke();
    }

    /// <summary>Removes downloaded copies for calendars that no longer exist.</summary>
    public static void CleanUpFiles()
    {
        try
        {
            if (!Directory.Exists(Storage.Folder)) return;
            var keep = new HashSet<string>(Sources.Select(s => $"calendar-{s.Id}.cache.ics")
                .Concat(Sources.Where(s => s.IsFile).Select(s => s.FileName)), StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(Storage.Folder, "calendar-*.ics"))
                if (!keep.Contains(Path.GetFileName(file))) File.Delete(file);
        }
        catch { }
    }

    // ---------- Encrypting links with Windows (DPAPI) ----------
    // Calendar links are secret (anyone with one can read your calendar), so they're stored
    // encrypted with your Windows account. Another user, or a copy of the file, can't read them.

    private static string Protect(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(Crypt(data, protect: true));
    }

    private static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        return Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(stored), protect: false));
    }

    private static byte[] Crypt(byte[] input, bool protect)
    {
        var inBlob = new DataBlob();
        var outBlob = new DataBlob();
        var handle = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            inBlob.Size = input.Length;
            inBlob.Data = handle.AddrOfPinnedObject();
            bool ok = protect
                ? CryptProtectData(ref inBlob, "FocusApp calendar", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, ref outBlob);
            if (!ok) throw new InvalidOperationException("Windows couldn't unlock the saved calendar link.");
            var output = new byte[outBlob.Size];
            Marshal.Copy(outBlob.Data, output, 0, outBlob.Size);
            return output;
        }
        finally
        {
            handle.Free();
            if (outBlob.Data != IntPtr.Zero) LocalFree(outBlob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
