using System;
using System.Collections.Generic;
using System.Linq;

namespace FocusApp;

/// <summary>
/// Which websites are allowed: the built-in categories (like Freedom's), your own list, and
/// block-list or allow-only mode. Pure logic, so it can be tested.
/// </summary>
public static class SiteRules
{
    /// <summary>Built-in categories: the big, well-known sites. You can add or remove sites per category in Settings.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Categories = new Dictionary<string, string[]>
    {
        ["Social media"] = new[] { "facebook.com", "instagram.com", "twitter.com", "x.com", "tiktok.com", "snapchat.com", "linkedin.com",
            "pinterest.com", "threads.net", "bsky.app", "tumblr.com", "mastodon.social", "messenger.com", "whatsapp.com", "discord.com", "telegram.org" },
        ["News"] = new[] { "cnn.com", "foxnews.com", "nytimes.com", "washingtonpost.com", "wsj.com", "bbc.com", "bbc.co.uk", "theguardian.com",
            "reuters.com", "apnews.com", "nbcnews.com", "cbsnews.com", "abcnews.go.com", "npr.org", "usatoday.com", "news.google.com",
            "news.yahoo.com", "msn.com", "huffpost.com", "politico.com", "axios.com", "bloomberg.com", "cnbc.com", "theverge.com", "drudgereport.com" },
        ["Sports"] = new[] { "espn.com", "foxsports.com", "cbssports.com", "nfl.com", "nba.com", "mlb.com", "nhl.com", "bleacherreport.com",
            "theathletic.com", "si.com", "sports.yahoo.com", "skysports.com", "goal.com", "247sports.com", "on3.com", "fanduel.com", "draftkings.com" },
        ["Video and streaming"] = new[] { "youtube.com", "youtu.be", "netflix.com", "hulu.com", "disneyplus.com", "primevideo.com", "max.com",
            "twitch.tv", "vimeo.com", "dailymotion.com", "peacocktv.com", "paramountplus.com", "crunchyroll.com", "tv.apple.com", "pluto.tv", "rumble.com" },
        ["Shopping"] = new[] { "amazon.com", "ebay.com", "walmart.com", "target.com", "etsy.com", "aliexpress.com", "temu.com", "shein.com",
            "bestbuy.com", "wayfair.com", "costco.com", "homedepot.com", "lowes.com", "zillow.com", "craigslist.org" },
        ["Games"] = new[] { "store.steampowered.com", "steamcommunity.com", "epicgames.com", "roblox.com", "miniclip.com", "poki.com", "chess.com",
            "lichess.org", "itch.io", "ign.com", "gamespot.com", "kongregate.com", "coolmathgames.com", "addictinggames.com", "crazygames.com" },
        ["Timewasters"] = new[] { "reddit.com", "9gag.com", "imgur.com", "buzzfeed.com", "boredpanda.com", "quora.com", "medium.com",
            "knowyourmeme.com", "ifunny.co", "cracked.com", "giphy.com", "tumblr.com", "news.ycombinator.com", "digg.com" },
    };

    /// <summary>Cleans what you type into a plain site name: "https://www.YouTube.com/watch" gives "youtube.com".</summary>
    public static string Clean(string input)
    {
        string s = (input ?? "").Trim().ToLowerInvariant();
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        int end = s.IndexOfAny(new[] { '/', '?', '#', ' ' });
        if (end >= 0) s = s[..end];
        int at = s.LastIndexOf('@'); // user@host
        if (at >= 0) s = s[(at + 1)..];
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        if (s.StartsWith("www.")) s = s[4..];
        if (s.StartsWith("m.")) s = s[2..];
        return s.Trim('.');
    }

    /// <summary>True for addresses that are the browser's own pages (new tab, settings), never blocked.</summary>
    public static bool IsBrowserPage(string address)
    {
        string a = (address ?? "").Trim().ToLowerInvariant();
        return a.Length == 0 || a.StartsWith("brave://") || a.StartsWith("chrome://") || a.StartsWith("edge://") ||
               a.StartsWith("about:") || a.StartsWith("opera://") || a.StartsWith("vivaldi://") || a.StartsWith("chrome-extension://") ||
               a.StartsWith("file:") || a.StartsWith("view-source:") || !Clean(a).Contains('.');
    }

    /// <summary>True if the host is the site or one of its subdomains ("m.youtube.com" counts as youtube.com; "notyoutube.com" doesn't).</summary>
    public static bool Covers(string host, string site)
    {
        site = Clean(site);
        if (site.Length == 0 || host.Length == 0) return false;
        // Sites with a path (like facebook.com/marketplace) match on the host part.
        int slash = site.IndexOf('/');
        if (slash >= 0) site = site[..slash];
        return host == site || host.EndsWith("." + site, StringComparison.Ordinal);
    }

    /// <summary>
    /// Decides whether an address is blocked. Block mode: blocked if it's in a chosen category or your
    /// list. Allow-only mode: blocked unless it's in your allowed list (categories don't apply).
    /// </summary>
    public static bool IsBlocked(string address, string mode, IEnumerable<string> blockedSites, IEnumerable<string> allowedSites)
    {
        if (IsBrowserPage(address)) return false;
        string host = Clean(address);
        if (host.Length == 0) return false;
        // Blocked sites are always blocked; in allow-only mode, so is everything not allowed.
        if (blockedSites.Any(s => Covers(host, s))) return true;
        return mode == "allow" && !allowedSites.Any(s => Covers(host, s));
    }

    /// <summary>All sites blocked in block mode: your list plus the chosen categories (with your edits to them).</summary>
    public static List<string> BlockedFrom(AppSettings settings)
    {
        var sites = new List<string>(settings.BlockedSites);
        foreach (var category in settings.WebCategories)
            sites.AddRange(SitesIn(category, settings));
        return sites.Select(Clean).Where(s => s.Length > 0).Distinct().ToList();
    }

    public static IEnumerable<string> SitesIn(string category, AppSettings settings) =>
        settings.WebCategorySites.TryGetValue(category, out var custom) ? custom
        : Categories.TryGetValue(category, out var builtIn) ? builtIn : Array.Empty<string>();
}
