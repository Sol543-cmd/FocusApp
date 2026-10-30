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

    public static bool SignedIn => !string.IsNullOrEmpty(Data.ProtectedToken);

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

    public static async Task SignInAsync(string email, string password)
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
        string token = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("access_token", out var t)) token = t.GetString() ?? "";
        }
        catch (JsonException) { }
        if (token.Length == 0)
        {
            Log.Write("Freedom sign-in reply without a token: " + Trim(body));
            throw new FreedomException("Freedom answered, but not the way Focus expects. Its service may have changed; see log.txt.");
        }
        Data.ProtectedToken = CalendarService.Protect(token);
        Data.Email = email.Trim();
        Save();
        await SyncAsync();
    }

    public static void SignOut()
    {
        Data.ProtectedToken = "";
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

    private static async Task<string> GetAsync(string path)
    {
        string token = Token();
        if (token.Length == 0) throw new FreedomException("Sign in to Freedom first.", signedOut: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, Base + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Check(response, body);
        return body;
    }

    private static void Check(HttpResponseMessage response, string body)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Data.ProtectedToken = "";
            Save();
            throw new FreedomException("Freedom signed you out. Sign in again in the Freedom window.", signedOut: true);
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

        string token = Token();
        if (token.Length == 0) throw new FreedomException("Sign in to Freedom first.", signedOut: true);
        var payload = new
        {
            device_ids = devices.Select(AsJsonId).ToList(),
            start_time = "now",
            filter_list_ids = lists.Select(AsJsonId).ToList(),
            duration = minutes * 60
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Base + "/api/v1/schedules")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Check(response, body);
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
