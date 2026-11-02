using System.Globalization;
using System.Text.RegularExpressions;
using M3uPlayer.Core.Playlist;

namespace M3uPlayer.Core.Catchup;

public record CatchupInfo(string? Type, string? Source, int Days);

public static partial class CatchupUrlBuilder
{
    public const string FlussonicPlaylistName = "mono"; // index- ends at once on 16 of the owner's 22 4K channels; mono- plays 21 of them, and every other channel tried

    private static readonly HashSet<string> Supported =
        new(["default", "append", "shift", "flussonic", "fs", "xc"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(?<base>[a-z][a-z0-9+.-]*://[^/?#]+)(?:/live)?/(?<u>[^/?#]+)/(?<p>[^/?#]+)/(?<id>[^/?#.]+)(?<ext>\.[^/?#]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex XcShape();

    [GeneratedRegex(@"\{(utc|utcend|lutc|duration|Y|m|d|H|M|S)\}")]
    private static partial Regex Placeholder();

    public static bool IsAvailable(CatchupInfo info, DateTimeOffset programmeStart, DateTimeOffset now) =>
        info.Type is { } t && Supported.Contains(t) && info.Days > 0
        && programmeStart <= now && (now - programmeStart).TotalDays <= info.Days;

    /// <summary>Null when the scheme gives no address, or when the result is not an address the player may open (the source template comes from the list).</summary>
    public static string? Build(CatchupInfo info, string liveUrl, DateTimeOffset start, DateTimeOffset stop, DateTimeOffset now) =>
        BuildRaw(info, liveUrl, start, stop, now) is { } url && M3uParser.HasAllowedScheme(url) ? url : null;

    private static string? BuildRaw(CatchupInfo info, string liveUrl, DateTimeOffset start, DateTimeOffset stop, DateTimeOffset now)
    {
        if (info.Type is not { } type || !Supported.Contains(type)) return null;
        var utc = start.ToUnixTimeSeconds();
        var seconds = (long)(stop - start).TotalSeconds;
        switch (type.ToLowerInvariant())
        {
            case "default":
                return info.Source is null ? null : Fill(info.Source, start, stop, now);
            case "append":
                return info.Source is null ? null : liveUrl + Fill(info.Source, start, stop, now);
            case "shift":
                return $"{liveUrl}{(liveUrl.Contains('?') ? '&' : '?')}utc={utc}&lutc={now.ToUnixTimeSeconds()}";
            case "xc":
                var m = XcShape().Match(liveUrl);
                if (!m.Success) return null;
                var minutes = (long)Math.Ceiling((stop - start).TotalMinutes);
                var at = start.UtcDateTime.ToString("yyyy-MM-dd:HH-mm", CultureInfo.InvariantCulture);
                return $"{m.Groups["base"].Value}/timeshift/{m.Groups["u"].Value}/{m.Groups["p"].Value}/{minutes}/{at}/{m.Groups["id"].Value}{m.Groups["ext"].Value}";
            default: // flussonic, fs
                var tail = liveUrl.IndexOfAny(['?', '#']);
                var path = tail < 0 ? liveUrl : liveUrl[..tail];
                var schemeEnd = path.IndexOf("://", StringComparison.Ordinal);
                var slash = path.LastIndexOf('/');
                if (schemeEnd < 0 || slash <= schemeEnd + 2 || slash == path.Length - 1) return null;
                var ext = Path.GetExtension(path[(slash + 1)..]).ToLowerInvariant();
                if (ext is not (".ts" or ".m3u8")) ext = ".m3u8";
                return $"{path[..(slash + 1)]}{FlussonicPlaylistName}-{utc}-{seconds}{ext}{(tail < 0 ? "" : liveUrl[tail..])}";
        }
    }

    private static string Fill(string template, DateTimeOffset start, DateTimeOffset stop, DateTimeOffset now)
    {
        var s = start.UtcDateTime;
        return Placeholder().Replace(template.Replace("${start}", "{utc}").Replace("${end}", "{utcend}"), m => m.Groups[1].Value switch
        {
            "utc" => start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "utcend" => stop.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "lutc" => now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "duration" => ((long)(stop - start).TotalSeconds).ToString(CultureInfo.InvariantCulture),
            "Y" => s.ToString("yyyy", CultureInfo.InvariantCulture),
            "m" => s.ToString("MM", CultureInfo.InvariantCulture),
            "d" => s.ToString("dd", CultureInfo.InvariantCulture),
            "H" => s.ToString("HH", CultureInfo.InvariantCulture),
            "M" => s.ToString("mm", CultureInfo.InvariantCulture),
            _ => s.ToString("ss", CultureInfo.InvariantCulture),
        });
    }
}
