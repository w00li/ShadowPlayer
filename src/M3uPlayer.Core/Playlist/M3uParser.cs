using System.Globalization;
using System.Text.RegularExpressions;

namespace M3uPlayer.Core.Playlist;

public record M3uEntry(
    string Key, string Name, string Url, string? TvgId, string? TvgName, string? Logo,
    double ShiftHours, string Category, string? CatchupType, string? CatchupSource,
    int CatchupDays, string? UserAgent, string? Referrer);

public record M3uDocument(string? EpgUrl, IReadOnlyList<M3uEntry> Entries, int SkippedCount);

public static partial class M3uParser
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
        { "http", "https", "rtsp", "rtp", "udp" };

    [GeneratedRegex("([\\w-]+)=\"([^\"]*)\"")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.-]*)://")]
    private static partial Regex Scheme();

    public static M3uDocument Parse(TextReader reader)
    {
        string? line;
        do line = reader.ReadLine()?.Trim('﻿', ' ', '\t'); while (line is { Length: 0 });
        if (line is null || !line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Missing #EXTM3U header.");

        var header = Attrs(line);
        var epg = NonEmpty(header, "url-tvg") ?? NonEmpty(header, "x-tvg-url");

        var entries = new List<M3uEntry>();
        var keyCounts = new Dictionary<string, int>();
        var skipped = 0;
        Dictionary<string, string>? attrs = null;
        var name = "";
        string? userAgent = null, referrer = null;

        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                if (attrs is not null) { skipped++; userAgent = referrer = null; } // previous EXTINF never got a URL
                var comma = NameComma(line);
                attrs = Attrs(comma < 0 ? line : line[..comma]);
                name = comma < 0 ? "" : line[(comma + 1)..].Trim();
            }
            else if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
            {
                var kv = line["#EXTVLCOPT:".Length..];
                var eq = kv.IndexOf('=');
                if (eq < 0) continue;
                var value = kv[(eq + 1)..].Trim();
                switch (kv[..eq].Trim().ToLowerInvariant())
                {
                    case "http-user-agent": userAgent = value; break;
                    case "http-referrer": referrer = value; break;
                }
            }
            else if (line[0] != '#')
            {
                if (attrs is null) { userAgent = referrer = null; continue; }
                var ok = name.Length > 0 && Scheme().Match(line) is { Success: true } m
                    && AllowedSchemes.Contains(m.Groups[1].Value);
                if (ok) entries.Add(Build(attrs, name, line, userAgent, referrer, keyCounts));
                else skipped++;
                attrs = null;
                userAgent = referrer = null;
            }
        }
        if (attrs is not null) skipped++;

        return new M3uDocument(epg, entries, skipped);
    }

    private static M3uEntry Build(Dictionary<string, string> a, string name, string url,
        string? userAgent, string? referrer, Dictionary<string, int> keyCounts)
    {
        var tvgId = NonEmpty(a, "tvg-id");
        var key = tvgId ?? name;
        var n = keyCounts.GetValueOrDefault(key) + 1;
        keyCounts[key] = n;
        if (n > 1) key += "#" + n;

        var days = NonEmpty(a, "catchup-days") ?? NonEmpty(a, "timeshift");
        return new M3uEntry(
            key, name, url, tvgId, NonEmpty(a, "tvg-name"), NonEmpty(a, "tvg-logo"),
            double.TryParse(NonEmpty(a, "tvg-shift"), NumberStyles.Float, CultureInfo.InvariantCulture, out var shift) && Math.Abs(shift) <= 24 ? shift : 0, // NaN fails the comparison too
            NonEmpty(a, "group-title") ?? "Inne",
            NonEmpty(a, "catchup"), NonEmpty(a, "catchup-source"),
            int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 0,
            userAgent, referrer);
    }

    // First comma outside quotes; the duration before the attributes never contains one.
    private static int NameComma(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ',' && !inQuotes) return i;
        }
        return -1;
    }

    private static Dictionary<string, string> Attrs(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Attribute().Matches(text)) d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }

    private static string? NonEmpty(Dictionary<string, string> a, string key) =>
        a.TryGetValue(key, out var v) && v.Trim().Length > 0 ? v.Trim() : null;
}
