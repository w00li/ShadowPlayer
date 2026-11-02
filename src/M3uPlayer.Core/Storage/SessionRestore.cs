namespace M3uPlayer.Core.Storage;

public static class SessionRestore
{
    public const string AllKey = "*all", FavoritesKey = "*fav";

    /// <summary>The category to open with: the saved one while it still exists and still holds the saved channel, else "Wszystkie".</summary>
    /// <param name="knownCategoryKeys">Keys of the categories that exist now ("c:" + name).</param>
    public static string StartCategory(string? saved, IReadOnlySet<string> knownCategoryKeys, Channel? channel)
    {
        var known = saved is AllKey or FavoritesKey || (saved is not null && knownCategoryKeys.Contains(saved));
        var holds = channel is null || saved == AllKey || (saved == FavoritesKey && channel.IsFavorite) || saved == "c:" + channel.Category;
        return known && holds ? saved! : AllKey;
    }
}
