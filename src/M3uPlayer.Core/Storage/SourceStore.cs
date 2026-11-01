using Microsoft.Data.Sqlite;

namespace M3uPlayer.Core.Storage;

public enum SourceKind { Playlist, Epg }

public record Source(
    long Id, SourceKind Kind, string Location, string Name, int Position, int IntervalMinutes, bool AutoRefresh,
    long? ParentPlaylistId, string? ETag, string? LastModified, DateTimeOffset? LastAttempt, DateTimeOffset? LastSuccess,
    string? LastError, int SkippedCount, int MatchedCount, int ChannelCount);

public class SourceStore(Database db)
{
    public long Add(SourceKind kind, string location, string name, long? parentPlaylistId = null)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        // Manual EPG sources go above the url-tvg children, so a guide the user chose outranks the playlist's own.
        var at = kind == SourceKind.Epg && parentPlaylistId is null
            ? Database.Exec(c, "SELECT MIN(position) FROM source WHERE kind=@0 AND parent_playlist_id IS NOT NULL", (int)kind)
            : null;
        if (at is not null) Database.Exec(c, "UPDATE source SET position=position+1 WHERE kind=@0 AND position>=@1", (int)kind, at);
        var id = (long)Database.Exec(c,
            "INSERT INTO source(kind,location,name,position,parent_playlist_id) " +
            "VALUES(@0,@1,@2,COALESCE(@4,(SELECT COALESCE(MAX(position),-1)+1 FROM source WHERE kind=@0)),@3) RETURNING id",
            (int)kind, location, name, parentPlaylistId, at)!;
        tx.Commit();
        return id;
    }

    /// <summary>Points a source at a new address; the old validators belong to the old address, so they and the last attempt are cleared (the source becomes due).</summary>
    public void SetLocation(long id, string location)
    {
        using var c = db.Open();
        Database.Exec(c, "UPDATE source SET location=@0, etag=NULL, last_modified=NULL, last_attempt=NULL WHERE id=@1", location, id);
    }

    public IReadOnlyList<Source> List(SourceKind kind)
    {
        using var c = db.Open();
        return Database.Rows(c,
            "SELECT id,kind,location,name,position,interval_minutes,auto_refresh,parent_playlist_id,etag,last_modified," +
            "last_attempt,last_success,last_error,skipped_count,matched_count,channel_count FROM source WHERE kind=@0 ORDER BY position",
            r => new Source(r.GetInt64(0), (SourceKind)r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                r.GetInt32(5), r.GetInt32(6) != 0, Nullable(r, 7, r.GetInt64), Str(r, 8), Str(r, 9),
                Time(r, 10), Time(r, 11), Str(r, 12),
                r.GetInt32(13), r.GetInt32(14), r.GetInt32(15)), (int)kind);
    }

    /// <summary>Cascades (foreign keys) to the source's channels, matches, EPG data and child url-tvg source. Favorites stay by design.</summary>
    public void Remove(long id)
    {
        using var c = db.Open();
        Database.Exec(c, "DELETE FROM source WHERE id=@0", id);
    }

    public void Move(long id, int newPosition)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var kind = Database.Exec(c, "SELECT kind FROM source WHERE id=@0", id);
        if (kind is null) return;
        var ids = Database.Rows(c, "SELECT id FROM source WHERE kind=@0 AND id<>@1 ORDER BY position", r => r.GetInt64(0), kind, id);
        ids.Insert(Math.Clamp(newPosition, 0, ids.Count), id);
        for (var i = 0; i < ids.Count; i++) Database.Exec(c, "UPDATE source SET position=@0 WHERE id=@1", i, ids[i]);
        tx.Commit();
    }

    public void SetInterval(long id, int minutes, bool auto)
    {
        using var c = db.Open();
        Database.Exec(c, "UPDATE source SET interval_minutes=@0, auto_refresh=@1 WHERE id=@2", minutes, auto, id);
    }

    public void RecordAttempt(long id, DateTimeOffset at, string? error)
    {
        using var c = db.Open();
        Database.Exec(c, "UPDATE source SET last_attempt=@0, last_error=@1 WHERE id=@2", at.ToUnixTimeSeconds(), error, id);
    }

    public void RecordSuccess(long id, DateTimeOffset at, string? etag, string? lastModified, int skipped)
    {
        using var c = db.Open();
        Database.Exec(c,
            "UPDATE source SET last_attempt=@0, last_success=@0, last_error=NULL, etag=@1, last_modified=@2, skipped_count=@3 WHERE id=@4",
            at.ToUnixTimeSeconds(), etag, lastModified, skipped, id);
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? Time(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(i));

    private static T? Nullable<T>(SqliteDataReader r, int i, Func<int, T> get) where T : struct =>
        r.IsDBNull(i) ? null : get(i);
}
