using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FocusApp;

/// <summary>
/// Updates from GitHub Releases. The GitHub build (.github/workflows/release.yml) stamps the repository
/// name into FocusApp.exe; copies built on your own PC have none, so they never check for updates.
/// Only reads the public release list, downloads FocusSetup.exe over https, checks its fingerprint
/// (SHA-256) when GitHub provides one, and runs it. Nothing is ever uploaded.
/// </summary>
public static class Updater
{
    public record Release(Version Version, string Tag, string Notes, string DownloadUrl, long Size, string? Sha256);

    private const string AssetName = "FocusSetup.exe";
    private const long MaxBytes = 300L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>"owner/repo", set by the GitHub build. Empty for local builds.</summary>
    public static string Repo { get; } =
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepo")?.Value?.Trim() ?? "";

    public static bool Available => Repo.Contains('/');

    public static Version Current { get; } =
        Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0));

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FocusApp-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        string text = tag.Trim().TrimStart('v', 'V');
        int dash = text.IndexOf('-');
        if (dash >= 0) text = text[..dash];
        if (!Version.TryParse(text.Contains('.') ? text : text + ".0", out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    /// <summary>The newest release, or null if there's none newer than this copy.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        if (!Available) return null;
        string url = $"https://api.github.com/repos/{Repo}/releases/latest";
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if ((int)response.StatusCode == 404) return null; // no releases yet
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!TryParseTag(tag, out var version) || version <= Current) return null;

        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string download = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!download.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return null;
            long size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            string? sha = null;
            if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String &&
                d.GetString() is string digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = digest[7..].ToLowerInvariant();
            string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            return new Release(version, tag, notes, download, size, sha);
        }
        return null;
    }

    /// <summary>Downloads the installer to a temporary folder and returns its path.</summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (release.Size > MaxBytes) throw new InvalidOperationException("The update file is unexpectedly large.");
        string folder = Path.Combine(Path.GetTempPath(), "FocusUpdate");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"FocusSetup-{release.Version}.exe");

        using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            string host = response.RequestMessage?.RequestUri?.Host ?? "";
            if (response.RequestMessage?.RequestUri?.Scheme != "https" ||
                !(host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) ||
                  host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The update didn't come from GitHub, so it wasn't used.");

            long total = response.Content.Headers.ContentLength ?? release.Size;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                done += read;
                if (done > MaxBytes) throw new InvalidOperationException("The update file is unexpectedly large.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        if (release.Sha256 != null)
        {
            await using var check = File.OpenRead(path);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (actual != release.Sha256)
            {
                try { File.Delete(path); } catch { }
                throw new InvalidOperationException("The downloaded update didn't match GitHub's fingerprint, so it wasn't used.");
            }
        }
        return path;
    }
}
