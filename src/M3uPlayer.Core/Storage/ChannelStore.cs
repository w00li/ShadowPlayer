using M3uPlayer.Core.Catchup;
using M3uPlayer.Core.Playlist;
using M3uPlayer.Core.Text;
using Microsoft.Data.Sqlite;

namespace M3uPlayer.Core.Storage;

public record Channel(
    string Key, long SourceId, string Name, string Url, string? Logo, string Category, double ShiftHours,
    CatchupInfo Catchup, string? UserAgent, string? Referrer, bool IsFavorite);

public record CategoryCount(string Name, int Count);

public class ChannelStore(Database db)
{
    private const int MaxArchiveDays = 30;

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
            var key = exists.ExecuteScalar() is null ? e.Key : $"{e.Key}#{sourceId}";
            object?[] values =
            [
                key, sourceId, e.Name, TextNormalizer.ForSearch(e.Name), e.Url, e.Logo, e.Category, e.ShiftHours,
                e.CatchupType, e.CatchupSource, e.CatchupDays, e.UserAgent, e.Referrer, e.TvgId, e.TvgName
            ];
            for (var i = 0; i < p.Length; i++) p[i].Value = values[i] ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
        Database.Exec(c, "UPDATE source SET channel_count=@1 WHERE id=@0", sourceId, entries.Count);
        tx.Commit();
    }

    public IReadOnlyList<CategoryCount> Categories()
    {
        using var c = db.Open();
        return Database.Rows(c,
            "SELECT c.category, COUNT(*) FROM channel c JOIN source s ON s.id=c.source_id " +
            "GROUP BY c.category ORDER BY MIN(s.position), MIN(c.rowid)",
            r => new CategoryCount(r.GetString(0), r.GetInt32(1)));
    }

    public int CountAll()
    {
        using var c = db.Open();
        return Convert.ToInt32(Database.Exec(c, "SELECT COUNT(*) FROM channel"));
    }

    public int CountFavorites()
    {
        using var c = db.Open();
        return Convert.ToInt32(Database.Exec(c, "SELECT COUNT(*) FROM channel c JOIN favorite f ON f.channel_key=c.key"));
    }

    public IReadOnlyList<Channel> ByCategory(string name) => Query("WHERE c.category=@0", name);

    public IReadOnlyList<Channel> All() => Query("");

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
