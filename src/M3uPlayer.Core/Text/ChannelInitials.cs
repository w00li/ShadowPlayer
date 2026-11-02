namespace M3uPlayer.Core.Text;

public static class ChannelInitials
{
    /// <summary>Up to two capital letters or digits: first letters of the first two words, or the first two characters of a single word.</summary>
    public static string From(string name)
    {
        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.SkipWhile(c => !char.IsLetterOrDigit(c)).ToArray()))
            .Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return "?";
        var s = words.Count == 1 ? new string(words[0].Where(char.IsLetterOrDigit).Take(2).ToArray()) : $"{words[0][0]}{words[1][0]}";
        return s.ToUpperInvariant();
    }
}
