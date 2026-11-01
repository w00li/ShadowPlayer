using System.Text.RegularExpressions;
using M3uPlayer.Core.Refresh;
using M3uPlayer.Core.Text;

namespace M3uPlayer.Core.Sources;

/// <summary>Pure helpers behind the Add-source form and the source rows.</summary>
public static partial class SourceInput
{
    [GeneratedRegex("[0-9a-fA-F]{16,}")]
    private static partial Regex LongHex();

    public static string NextDefaultName(string prefix, IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (taken.Contains($"{prefix} {n}")) n++;
        return $"{prefix} {n}";
    }

    /// <returns>A Polish message saying what to fix, or null when the address is acceptable.</returns>
    public static string? ValidateUrl(string? url)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url)) return "Wpisz adres, który zaczyna się od http:// lub https://.";
        return SourceFetcher.IsHttp(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0
            ? null
            : "Ten adres nie jest obsługiwany. Wpisz pełny adres zaczynający się od http:// lub https://.";
    }

    public static bool IsHashLike(string name) => LongHex().IsMatch(name);

    public static string DefaultNameForFile(string path, string prefix, IEnumerable<string> existing)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length == 0 || IsHashLike(name) ? NextDefaultName(prefix, existing) : name;
    }

    /// <summary>What a row may show of a location: never the address of a server, nor a folder, nor a provider-issued file name.</summary>
    public static string DisplayLocation(string location)
    {
        if (SourceFetcher.IsHttp(location)) return "Adres internetowy";
        var name = Path.GetFileName(location);
        return name.Length == 0 || IsHashLike(name) ? "Plik lokalny" : "Plik: " + Redactor.Redact(name);
    }
}
