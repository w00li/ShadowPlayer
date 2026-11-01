using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace M3uPlayer.Core.Text;

public record ChannelNameParts(string Base, string Quality);

public static partial class TextNormalizer
{
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(uhd|fhd|hd|sd|4k)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex QualityTag();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();

    public static ChannelNameParts ParseChannelName(string name)
    {
        // Provider EPG uses Cyrillic K in "4K"
        name = name.Replace('К', 'k').Replace('к', 'k');
        var quality = QualityTag().Match(name).Value.ToLowerInvariant();
        var rest = QualityTag().Replace(name, " ");
        return new ChannelNameParts(NonAlnum().Replace(ForSearch(rest), ""), quality);
    }

    public static string ForSearch(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Replace('ł', 'l').Replace('Ł', 'L').Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
