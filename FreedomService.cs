using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FocusApp;

/// <summary>One Freedom blocklist or device, as Freedom names it.</summary>
public class FreedomItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>freedom.json (this PC only, never synced): the Freedom sign-in and your choices.</summary>
public class FreedomData
{
    /// <summary>The Freedom sign-in token, encrypted with your Windows account. Your password is never saved.</summary>
    public string ProtectedToken { get; set; } = "";
    /// <summary>Freedom's renewal token (encrypted), used to renew the sign-in when it runs out.</summary>
    public string ProtectedRefresh { get; set; } = "";
    /// <summary>Only with "Keep me signed in": your Freedom password, encrypted with your Windows account, used just to sign in again if Freedom ends the sign-in.</summary>
    public string ProtectedPassword { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>Identifies this copy of Focus to Freedom, so signing in again doesn't look like a new device.</summary>
    public string ClientId { get; set; } = "";
    /// <summary>Start Freedom whenever a focus session starts.</summary>
    public bool StartWithFocus { get; set; }
    /// <summary>The blocklists and devices ticked for focus sessions (Freedom's ids).</summary>
    public List<string> BlocklistIds { get; set; } = new();
    public List<string> DeviceIds { get; set; } = new();
    /// <summary>The last copy of what's in Freedom, so the lists show while offline.</summary>
    public List<FreedomItem> Blocklists { get; set; } = new();
    public List<FreedomItem> Devices { get; set; } = new();
    public DateTime LastSyncUtc { get; set; }
    public DateTime LastStartUtc { get; set; }
    public DateTime LastEndUtc { get; set; }
}

/// <summary>
/// Freedom (freedom.to) for Focus. Freedom has no official public API; this uses the same web
/// service its apps use (the one the tray's "Sync Freedom" talks to), as documented by the open-source
/// freedom-py client. If Freedom changes it, sign-in or syncing shows an error and Focus's own blocking
/// keeps working. The raw replies of a test are written to log.txt (Freedom window, Test connection).
///
/// Mirroring: every sync replaces Focus's copy of your blocklists and devices with Freedom's, so lists
/// you add in Freedom appear (unticked) and lists you delete disappear, along with their tick.
/// </summary>
public static class FreedomService
{
    private const string Base = "https://freedom.to";
    private const string FileName = "freedom.json";

    public static FreedomData Data { get; private set; } = new();
    public static event Action? Changed;

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static FreedomService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("FocusApp");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public static void Load()
    {
        try { Data = Storage.Load<FreedomData>(FileName); }
        catch (Exception ex) { Log.Error("Freedom.Load", ex); Data = new FreedomData(); }
        if (string.IsNullOrEmpty(Data.ClientId)) { Data.ClientId = Guid.NewGuid().ToString().ToUpperInvariant(); Save(); }
    }

    public static void Save()
    {
        try { Storage.Save(FileName, Data); }
        catch (Exception ex) { Log.Error("Freedom.Save", ex); }
        Changed?.Invoke();
    }

    /// <summary>Signed in, or able to sign in again by itself (renewal token or Keep me signed in).</summary>
    public static bool SignedIn => !string.IsNullOrEmpty(Data.ProtectedToken) || !string.IsNullOrEmpty(Data.ProtectedRefresh) || !string.IsNullOrEmpty(Data.ProtectedPassword);

    private static string Token()
    {
        try { return CalendarService.Unprotect(Data.ProtectedToken); }
        catch { return ""; }
    }

    /// <summary>A problem Freedom reported, worded for the person reading it.</summary>
    public class FreedomException : Exception
    {
        public bool SignedOut { get; }
        public FreedomException(string message, bool signedOut = false) : base(message) => SignedOut = signedOut;
    }

    // ---------- Signing in ----------

    public static async Task SignInAsync(string email, string password, bool keepSignedIn = true)
    {
        await RequestTokenAsync(email, password);
        Data.Email = email.Trim();
        Data.ProtectedPassword = keepSignedIn ? CalendarService.Protect(password) : "";
        Save();
        await SyncAsync();
    }

    public static bool KeepsSignedIn => !string.IsNullOrEmpty(Data.ProtectedPassword);

    /// <summary>Asks Freedom for a sign-in with your email and password and keeps it (and its renewal token).</summary>
    private static async Task RequestTokenAsync(string email, string password)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["hash"] = Data.ClientId,
            ["os"] = "ios",
            ["password"] = password,
            ["response_type"] = "code",
            ["scope"] = "email",
            ["username"] = email
        };
        using var response = await Http.PostAsync(Base + "/auth/oauth/token", new FormUrlEncodedContent(form));
        string body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            throw new FreedomException("Freedom didn't accept that email and password.");
        if (!response.IsSuccessStatusCode)
            throw new FreedomException($"Freedom couldn't sign you in right now ({(int)response.StatusCode}). Try again later.");
        if (!KeepTokens(body))
        {
            Log.Write("Freedom sign-in reply without a token: " + Trim(body));
            throw new FreedomException("Freedom answered, but not the way Focus expects. Its service may have changed; see log.txt.");
        }
    }

    /// <summary>Saves the access token (and the renewal token, if Freedom sent one) from a sign-in reply. False if there's no token.</summary>
    private static bool KeepTokens(string body)
    {
        string token = "", refresh = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("access_token", out var t)) token = t.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String) refresh = r.GetString() ?? "";
        }
        catch (JsonException) { }
        if (token.Length == 0) return false;
        Data.ProtectedToken = CalendarService.Protect(token);
        if (refresh.Length > 0) Data.ProtectedRefresh = CalendarService.Protect(refresh);
        Save();
        return true;
    }

    private static readonly System.Threading.SemaphoreSlim RenewLock = new(1, 1);

    /// <summary>
    /// Freedom ended the sign-in (it runs out after a while): renew it with the renewal token, or sign in again with
    /// the saved password (Keep me signed in). True when Focus is signed in again.
    /// </summary>
    public static async Task<bool> RenewAsync()
    {
        await RenewLock.WaitAsync();
        try
        {
            string refresh = Unprotect(Data.ProtectedRefresh);
            if (refresh.Length > 0)
            {
                try
                {
                    var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["hash"] = Data.ClientId, ["os"] = "ios" };
                    using var response = await Http.PostAsync(Base + "/auth/oauth/token", new FormUrlEncodedContent(form));
                    string body = await response.Content.ReadAsStringAsync();
                    if (response.IsSuccessStatusCode && KeepTokens(body)) { Log.Write("Freedom sign-in renewed"); return true; }
                    Log.Write($"Freedom renewal refused ({(int)response.StatusCode})");
                    if ((int)response.StatusCode is >= 400 and < 500) { Data.ProtectedRefresh = ""; Save(); }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Log.Write("Freedom renewal: " + ex.Message); return SignedIn; }
            }
            string password = Unprotect(Data.ProtectedPassword);
            if (password.Length > 0 && Data.Email.Length > 0)
            {
                try { await RequestTokenAsync(Data.Email, password); Log.Write("Freedom signed in again (Keep me signed in)"); return true; }
                catch (FreedomException ex) when (ex.Message.StartsWith("Freedom didn't accept", StringComparison.Ordinal))
                {
                    Data.ProtectedPassword = ""; // the password changed: ask once more
                    Save();
                    Log.Write("Freedom: the saved password no longer works");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FreedomException) { Log.Write("Freedom sign-in again: " + ex.Message); return SignedIn; }
            }
            return false;
        }
        finally { RenewLock.Release(); }
    }

    private static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try { return CalendarService.Unprotect(stored); } catch { return ""; }
    }

    public static void SignOut()
    {
        Data.ProtectedToken = "";
        Data.ProtectedRefresh = "";
        Data.ProtectedPassword = "";
        Save();
    }

    // ---------- Mirroring what's in Freedom ----------

    /// <summary>Reads your blocklists and devices from Freedom and makes Focus match them exactly.</summary>
    public static async Task SyncAsync()
    {
        var lists = ReadItems(await GetAsync("/filter_lists/"), "filter_lists");
        var devices = ReadItems(await GetAsync("/devices/"), "devices");

        Data.Blocklists = lists;
        Data.Devices = devices;
        // A tick for something you deleted in Freedom goes with it.
        Data.BlocklistIds = Data.BlocklistIds.Where(id => lists.Any(l => l.Id == id)).Distinct().ToList();
        Data.DeviceIds = Data.DeviceIds.Where(id => devices.Any(d => d.Id == id)).Distinct().ToList();
        Data.LastSyncUtc = DateTime.UtcNow;
        Save();
    }

    private static Task<string> GetAsync(string path) => SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Base + path));

    /// <summary>Sends a request with the sign-in; if Freedom says the sign-in ran out, renews it once and tries again.</summary>
    private static async Task<string> SendAsync(Func<HttpRequestMessage> make)
    {
        if (Token().Length == 0 && !await RenewAsync()) throw new FreedomException("Sign in to Freedom first.", signedOut: true);
        for (int attempt = 0; ; attempt++)
        {
            using var request = make();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
            using var response = await Http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (attempt == 0 && await RenewAsync()) continue;
                Data.ProtectedToken = "";
                Save();
                throw new FreedomException("Freedom signed you out. Sign in again in the Freedom window (tick Keep me signed in).", signedOut: true);
            }
            Check(response, body);
            return body;
        }
    }

    private static void Check(HttpResponseMessage response, string body)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // Not a sign-out: Freedom refused this one thing (for example, a device your plan can't block). Stay signed in.
            Log.Write($"Freedom refused (403): {Trim(body)}");
            throw new FreedomException("Freedom refused that (403). You're still signed in; see log.txt.");
        }
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new FreedomException("Freedom sent Focus somewhere unexpected, so nothing was changed. Its service may have changed; see log.txt.");
        if (!response.IsSuccessStatusCode)
        {
            Log.Write($"Freedom replied {(int)response.StatusCode}: {Trim(body)}");
            throw new FreedomException($"Freedom didn't answer properly ({(int)response.StatusCode}). Try again later.");
        }
    }

    /// <summary>Finds the list in Freedom's reply ({"devices": [...]}, or just [...]) and reads each item's id and name.</summary>
    private static List<FreedomItem> ReadItems(string json, string key)
    {
        var items = new List<FreedomItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement array = default;
            if (root.ValueKind == JsonValueKind.Array) array = root;
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty(key, out var named) && named.ValueKind == JsonValueKind.Array) array = named;
                else foreach (var p in root.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Array) { array = p.Value; break; }
            }
            if (array.ValueKind != JsonValueKind.Array) throw new JsonException("no list");
            foreach (var e in array.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string id = Text(e, "id", "uid", "uuid");
                string name = Text(e, "name", "title", "device_name", "label");
                if (id.Length == 0) continue;
                items.Add(new FreedomItem { Id = id, Name = name.Length > 0 ? name : "Unnamed" });
            }
        }
        catch (JsonException)
        {
            Log.Write($"Freedom {key} reply Focus couldn't read: " + Trim(json));
            throw new FreedomException("Freedom answered, but not the way Focus expects. Its service may have changed; see log.txt.");
        }
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string Text(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v))
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString() ?? "",
                    JsonValueKind.Number => v.GetRawText(),
                    _ => ""
                };
        return "";
    }

    // ---------- Starting a session ----------

    /// <summary>Starts a Freedom session now, on these devices with these blocklists, for this many minutes.</summary>
    public static async Task StartAsync(IEnumerable<string> blocklistIds, IEnumerable<string> deviceIds, int minutes)
    {
        var lists = blocklistIds.Distinct().ToList();
        var devices = deviceIds.Distinct().ToList();
        if (lists.Count == 0) throw new FreedomException("Tick at least one Freedom blocklist.");
        if (devices.Count == 0) throw new FreedomException("Tick at least one Freedom device.");
        minutes = Math.Clamp(minutes, 1, 24 * 60);

        var payload = new
        {
            device_ids = devices.Select(AsJsonId).ToList(),
            start_time = "now",
            filter_list_ids = lists.Select(AsJsonId).ToList(),
            duration = minutes * 60
        };
        string json = JsonSerializer.Serialize(payload);
        await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Base + "/api/v1/schedules")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        Data.LastStartUtc = DateTime.UtcNow;
        Data.LastEndUtc = DateTime.UtcNow.AddMinutes(minutes);
        Save();
        Log.Write($"Freedom session started: {lists.Count} blocklist(s), {devices.Count} device(s), {minutes} min");
    }

    /// <summary>Freedom's ids are numbers; send them as numbers when they look like numbers.</summary>
    private static object AsJsonId(string id) => long.TryParse(id, out long n) ? n : id;

    /// <summary>Writes Freedom's raw replies to log.txt, for working out what changed if syncing stops working.</summary>
    public static async Task<string> TestAsync()
    {
        var report = new StringBuilder("Freedom connection test\n");
        foreach (var path in new[] { "/filter_lists/", "/devices/" })
        {
            try { report.AppendLine(path + " OK: " + Trim(await GetAsync(path))); }
            catch (Exception ex) { report.AppendLine(path + " failed: " + ex.Message); }
        }
        Log.Write(report.ToString());
        return report.ToString();
    }

    private static string Trim(string s) => s.Length > 1500 ? s[..1500] + "…" : s;

    public static string NameOf(IEnumerable<FreedomItem> items, string id) => items.FirstOrDefault(i => i.Id == id)?.Name ?? "";
}
