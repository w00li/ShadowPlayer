using M3uPlayer.Core.Catchup;
using M3uPlayer.Core.Playlist;
using M3uPlayer.Core.Text;
using Microsoft.Data.Sqlite;

namespace M3uPlayer.Core.Storage;

public record Channel(
    string Key, long SourceId, string Name, string Url, string? Logo, string Category, double ShiftHours,
    CatchupInfo Catchup, string? UserAgent, string? Referrer, bool IsFavorite);

public record CategoryCount(string Name, int Count, bool Pinned = false, bool Hidden = false);

public class ChannelStore(Database db)
{
    public const int MaxArchiveDays = 30;

    private const string Select =
        "SELECT c.key,c.source_id,c.name,c.url,c.logo,c.category,c.shift_hours,c.catchup_type,c.catchup_source,c.catchup_days," +
        "c.user_agent,c.referrer,f.channel_key IS NOT NULL FROM channel c JOIN source s ON s.id=c.source_id " +
        "LEFT JOIN favorite f ON f.channel_key=c.key ";

    private const string Order = " ORDER BY s.position, c.rowid";

    /// <summary>Replaces one source's channels. Matches of those channels are dropped (cascade); call RebuildMatches afterwards.</summary>
    public void Replace(long sourceId, IReadOnlyList<M3uEntry> entries)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var oldKeys = Database.Rows(c, "SELECT key FROM channel WHERE source_id=@0", r => r.GetString(0), sourceId).ToHashSet();
        // The delete cascades to channel_match; keep the rows of surviving keys so the guide survives until the next rebuild.
        var oldMatches = Database.Rows(c,
            "SELECT m.channel_key,m.source_id,m.epg_channel_id FROM channel_match m JOIN channel ch ON ch.key=m.channel_key WHERE ch.source_id=@0",
            r => (Key: r.GetString(0), Source: r.GetInt64(1), Epg: r.GetString(2)), sourceId);
        Database.Exec(c, "DELETE FROM channel WHERE source_id=@0", sourceId);
        using var exists = Database.Cmd(c, "SELECT 1 FROM channel WHERE key=@k");
        var k = exists.Parameters.AddWithValue("@k", "");
        using var insert = Database.Cmd(c,
            "INSERT INTO channel(key,source_id,name,norm_name,url,logo,category,shift_hours,catchup_type,catchup_source," +
            "catchup_days,user_agent,referrer,tvg_id,tvg_name) VALUES(@0,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13,@14)");
        var p = Enumerable.Range(0, 15).Select(i => insert.Parameters.AddWithValue("@" + i, DBNull.Value)).ToArray();
        foreach (var e in entries)
        {
            k.Value = e.Key;
            // Reuse the key this source held before, so a star never flips between lists; otherwise append "#id" until free.
            var suffix = $"#{sourceId}";
            var key = new[] { e.Key, e.Key + suffix, e.Key + suffix + suffix }.FirstOrDefault(oldKeys.Contains) ?? e.Key;
            for (k.Value = key; exists.ExecuteScalar() is not null; k.Value = key) key += suffix;
            object?[] values =
            [
                key, sourceId, e.Name, TextNormalizer.ForSearch(e.Name), e.Url, e.Logo, e.Category, e.ShiftHours,
                e.CatchupType, e.CatchupSource, e.CatchupDays, e.UserAgent, e.Referrer, e.TvgId, e.TvgName
            ];
            for (var i = 0; i < p.Length; i++) p[i].Value = values[i] ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
        foreach (var m in oldMatches)
            Database.Exec(c, "INSERT OR IGNORE INTO channel_match(channel_key,source_id,epg_channel_id) " +
                "SELECT @0,@1,@2 WHERE EXISTS(SELECT 1 FROM channel WHERE key=@0)", m.Key, m.Source, m.Epg);
        Database.Exec(c, "UPDATE source SET channel_count=@1 WHERE id=@0", sourceId, entries.Count);
        tx.Commit();
    }

    public IReadOnlyList<CategoryCount> Categories()
    {
        using var c = db.Open();
        return Database.Rows(c,
            "SELECT x.category, COUNT(*), COALESCE(MAX(p.pinned),0), COALESCE(MAX(p.hidden),0) FROM (SELECT c.category, " +
            "ROW_NUMBER() OVER (ORDER BY s.position, c.rowid) rn FROM channel c JOIN source s ON s.id=c.source_id) x " +
            "LEFT JOIN category_pref p ON p.name=x.category GROUP BY x.category ORDER BY COALESCE(MAX(p.pinned),0) DESC, MIN(rn)",
            r => new CategoryCount(r.GetString(0), r.GetInt32(1), r.GetInt64(2) != 0, r.GetInt64(3) != 0));
    }

    /// <summary>Pinned and hidden exclude each other; with neither the row goes. Kept by name, so it outlives a refresh of the list.</summary>
    public void SetCategoryPref(string name, bool pinned, bool hidden)
    {
        if (pinned && hidden) throw new ArgumentException("A category cannot be both pinned and hidden.", nameof(hidden));
        using var c = db.Open();
        if (!pinned && !hidden) Database.Exec(c, "DELETE FROM category_pref WHERE name=@0", name);
        else Database.Exec(c, "INSERT INTO category_pref(name,pinned,hidden) VALUES(@0,@1,@2) " +
            "ON CONFLICT(name) DO UPDATE SET pinned=excluded.pinned, hidden=excluded.hidden", name, pinned ? 1 : 0, hidden ? 1 : 0);
    }

    public void ShowAllCategories()
    {
        using var c = db.Open();
        Database.Exec(c, "DELETE FROM category_pref WHERE pinned=0");
        Database.Exec(c, "UPDATE category_pref SET hidden=0");
    }

    public int CountAll()
    {
        using var c = db.Open();
        return Convert.ToInt32(Database.Exec(c, "SELECT COUNT(*) FROM channel c WHERE " + Visible));
    }

    public int CountFavorites()
    {
        using var c = db.Open();
        return Convert.ToInt32(Database.Exec(c, "SELECT COUNT(*) FROM channel c JOIN favorite f ON f.channel_key=c.key"));
    }

    public IReadOnlyList<Channel> ByCategory(string name) => Query("WHERE c.category=@0", name);

    public IReadOnlyList<Channel> All() => Query("WHERE " + Visible);

    // Channels of hidden categories stay out of "All"; search, favourites and the category itself still show them.
    private const string Visible = "c.category NOT IN (SELECT name FROM category_pref WHERE hidden=1)";

    public Channel? Find(string key) => Query("WHERE c.key=@0", key).FirstOrDefault();

    public IReadOnlyList<Channel> Favorites() => Query("WHERE f.channel_key IS NOT NULL");

    public IReadOnlyList<Channel> Search(string query)
    {
        var q = TextNormalizer.ForSearch(query).Trim();
        if (q.Length == 0) return [];
        var escaped = q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        return Query("WHERE c.norm_name LIKE @0 ESCAPE '\\'", $"%{escaped}%");
    }

    public void SetFavorite(string key, bool on)
    {
        using var c = db.Open();
        Database.Exec(c, on ? "INSERT OR IGNORE INTO favorite(channel_key) VALUES(@0)" : "DELETE FROM favorite WHERE channel_key=@0", key);
    }

    /// <summary>Largest archive length among channels, clamped because playlist values are untrusted; 0 when none has archive.</summary>
    public int MaxCatchupDays()
    {
        using var c = db.Open();
        var max = Database.Exec(c, "SELECT MAX(catchup_days) FROM channel WHERE catchup_days>0");
        return max is null ? 0 : (int)Math.Min(Convert.ToInt64(max), MaxArchiveDays);
    }

    private IReadOnlyList<Channel> Query(string where, params object?[] args)
    {
        using var c = db.Open();
        return Database.Rows(c, Select + where + Order, r => new Channel(
            r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), Str(r, 4), r.GetString(5), r.GetDouble(6),
            new CatchupInfo(Str(r, 7), Str(r, 8), r.GetInt32(9)), Str(r, 10), Str(r, 11), r.GetInt32(12) != 0), args);
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
