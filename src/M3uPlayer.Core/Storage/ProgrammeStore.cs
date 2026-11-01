using System.Text.Json;
using M3uPlayer.Core.Epg;
using M3uPlayer.Core.Text;
using Microsoft.Data.Sqlite;

namespace M3uPlayer.Core.Storage;

public record Programme(
    string Title, string? SubTitle, string? Description, string? Category, string? IconUrl,
    DateTimeOffset Start, DateTimeOffset Stop);

public record NowNext(Programme? Now, Programme? Next);

public class ProgrammeStore(Database db)
{
    private const string ProgrammeColumns = "p.title,p.sub_title,p.description,p.category,p.icon_url,p.start_utc,p.stop_utc";

    // Stored times stay as the file gave them; tvg-shift is applied on read, so queries move the window by -shift.
    private const string ByChannel =
        "FROM channel c JOIN channel_match m ON m.channel_key=c.key JOIN programme p " +
        "ON p.source_id=m.source_id AND p.epg_channel_id=m.epg_channel_id WHERE c.key=@0 ";

    private const string ShiftSeconds = "CAST(ROUND(c.shift_hours*3600) AS INTEGER)";

    /// <summary>
    /// Merges an XMLTV file into one source in a single transaction; any failure rolls back and leaves old data intact.
    /// For each EPG channel, old programmes from that channel's earliest new start are replaced; earlier ones stay.
    /// Returns the number of programmes the reader skipped as invalid.
    /// </summary>
    public int ImportEpg(long sourceId, Stream xmltv, DateTimeOffset now, int retentionDays)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Database.Exec(c, "DELETE FROM epg_channel WHERE source_id=@0", sourceId); // takes the write lock before the slow read
        Database.Exec(c, "DROP TABLE IF EXISTS temp.import_programme");
        Database.Exec(c, "CREATE TEMP TABLE import_programme(ch TEXT NOT NULL, start INTEGER NOT NULL, stop INTEGER, " +
            "title TEXT NOT NULL, title_norm TEXT NOT NULL, sub_title TEXT, description TEXT, category TEXT, icon_url TEXT)");

        using var addChannel = Database.Cmd(c, "INSERT INTO epg_channel(source_id,ord,id,names_json) VALUES(@0,@1,@2,@3)");
        var cp = Enumerable.Range(0, 4).Select(i => addChannel.Parameters.AddWithValue("@" + i, DBNull.Value)).ToArray();
        using var addProgramme = Database.Cmd(c, "INSERT INTO import_programme VALUES(@0,@1,@2,@3,@4,@5,@6,@7,@8)");
        var pp = Enumerable.Range(0, 9).Select(i => addProgramme.Parameters.AddWithValue("@" + i, DBNull.Value)).ToArray();

        var ord = 0;
        var seen = new HashSet<string>();
        var skipped = XmltvReader.Read(xmltv, ch =>
        {
            if (!seen.Add(ch.Id)) return;
            cp[0].Value = sourceId; cp[1].Value = ord++; cp[2].Value = ch.Id; cp[3].Value = JsonSerializer.Serialize(ch.DisplayNames);
            addChannel.ExecuteNonQuery();
        }, p =>
        {
            object?[] v =
            [
                p.ChannelId, p.Start.ToUnixTimeSeconds(), p.Stop?.ToUnixTimeSeconds(), p.Title, TextNormalizer.ForSearch(p.Title),
                p.SubTitle, p.Description, p.Category, p.IconUrl
            ];
            for (var i = 0; i < pp.Length; i++) pp[i].Value = v[i] ?? DBNull.Value;
            addProgramme.ExecuteNonQuery();
        });

        var cutoff = now.AddDays(-retentionDays).ToUnixTimeSeconds();
        Database.Exec(c, "CREATE INDEX import_programme_ch ON import_programme(ch, start)");
        Database.Exec(c, "UPDATE import_programme SET stop=COALESCE((SELECT MIN(t.start) FROM import_programme t " +
            "WHERE t.ch=import_programme.ch AND t.start>import_programme.start), start+3600) WHERE stop IS NULL");
        Database.Exec(c, "DELETE FROM import_programme WHERE stop<@0", cutoff);
        if (Convert.ToInt64(Database.Exec(c, "SELECT COUNT(*) FROM import_programme")) == 0)
            throw new InvalidDataException("The EPG file contains no usable programmes.");

        Database.Exec(c, "DELETE FROM programme WHERE source_id=@0 AND start_utc >= " +
            "(SELECT MIN(t.start) FROM import_programme t WHERE t.ch=programme.epg_channel_id)", sourceId);
        // Channels without new programmes (declared empty or dropped) must not keep stale future rows; past rows stay as archive.
        Database.Exec(c, "DELETE FROM programme WHERE source_id=@0 AND start_utc>=@1 AND epg_channel_id NOT IN (SELECT ch FROM import_programme)",
            sourceId, now.ToUnixTimeSeconds());
        Database.Exec(c, "INSERT INTO programme(source_id,epg_channel_id,start_utc,stop_utc,title,title_norm,sub_title,description,category,icon_url) " +
            "SELECT @0,ch,start,stop,title,title_norm,sub_title,description,category,icon_url FROM import_programme", sourceId);
        Database.Exec(c, "DELETE FROM programme WHERE source_id=@0 AND stop_utc<@1", sourceId, cutoff);
        Database.Exec(c, "DROP TABLE import_programme");
        tx.Commit();
        return skipped;
    }

    /// <summary>For each channel picks the first EPG source (by position) that matches it.</summary>
    public void RebuildMatches()
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Database.Exec(c, "DELETE FROM channel_match");
        var matchers = new List<(long Id, EpgMatcher Matcher)>();
        foreach (var id in Database.Rows(c, "SELECT id FROM source WHERE kind=@0 ORDER BY position", r => r.GetInt64(0), (int)SourceKind.Epg))
        {
            var channels = Database.Rows(c, "SELECT id,names_json FROM epg_channel WHERE source_id=@0 ORDER BY ord",
                r => new XmltvChannel(r.GetString(0), JsonSerializer.Deserialize<string[]>(r.GetString(1)) ?? []), id);
            matchers.Add((id, new EpgMatcher(channels)));
        }

        var entries = Database.Rows(c, "SELECT key,name,tvg_id,tvg_name FROM channel",
            r => (Key: r.GetString(0), Name: r.GetString(1), TvgId: r.IsDBNull(2) ? null : r.GetString(2), TvgName: r.IsDBNull(3) ? null : r.GetString(3)));
        using var insert = Database.Cmd(c, "INSERT INTO channel_match(channel_key,source_id,epg_channel_id) VALUES(@0,@1,@2)");
        var ip = Enumerable.Range(0, 3).Select(i => insert.Parameters.AddWithValue("@" + i, DBNull.Value)).ToArray();
        foreach (var e in entries)
            foreach (var (id, matcher) in matchers)
                if (matcher.Match(e.TvgId, e.Name, e.TvgName) is { } epgId)
                {
                    ip[0].Value = e.Key; ip[1].Value = id; ip[2].Value = epgId;
                    insert.ExecuteNonQuery();
                    break;
                }

        Database.Exec(c, "UPDATE source SET matched_count=(SELECT COUNT(*) FROM channel_match m WHERE m.source_id=source.id), " +
            "channel_count=(SELECT COUNT(*) FROM epg_channel e WHERE e.source_id=source.id) WHERE kind=@0", (int)SourceKind.Epg);
        Database.Exec(c, "UPDATE source SET matched_count=(SELECT COUNT(*) FROM channel ch JOIN channel_match m ON m.channel_key=ch.key " +
            "WHERE ch.source_id=source.id) WHERE kind=@0", (int)SourceKind.Playlist);
        tx.Commit();
    }

    public NowNext GetNowNext(string channelKey, DateTimeOffset now)
    {
        using var c = db.Open();
        var shift = Shift(c, channelKey);
        var t = now.ToUnixTimeSeconds() - shift;
        var current = Database.Rows(c, $"SELECT {ProgrammeColumns} {ByChannel} AND p.start_utc<=@1 AND p.stop_utc>@1 ORDER BY p.start_utc DESC LIMIT 1",
            r => Map(r, shift), channelKey, t).FirstOrDefault();
        var next = Database.Rows(c, $"SELECT {ProgrammeColumns} {ByChannel} AND p.start_utc>@1 ORDER BY p.start_utc LIMIT 1",
            r => Map(r, shift), channelKey, t).FirstOrDefault();
        return new NowNext(current, next);
    }

    public IReadOnlyList<Programme> GetGuide(string channelKey, DateTimeOffset from, DateTimeOffset to)
    {
        using var c = db.Open();
        var shift = Shift(c, channelKey);
        return Database.Rows(c, $"SELECT {ProgrammeColumns} {ByChannel} AND p.stop_utc>@1 AND p.start_utc<@2 ORDER BY p.start_utc",
            r => Map(r, shift), channelKey, from.ToUnixTimeSeconds() - shift, to.ToUnixTimeSeconds() - shift);
    }

    /// <summary>Keys of channels having a programme whose normalized title contains the normalized query and that has not ended by <paramref name="from"/>.</summary>
    public IReadOnlyList<string> SearchProgrammeChannels(string query, DateTimeOffset from)
    {
        var q = TextNormalizer.ForSearch(query).Trim();
        if (q.Length == 0) return [];
        var escaped = q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        using var c = db.Open();
        return Database.Rows(c,
            "SELECT c.key FROM channel c JOIN source s ON s.id=c.source_id WHERE EXISTS(SELECT 1 FROM channel_match m JOIN programme p " +
            $"ON p.source_id=m.source_id AND p.epg_channel_id=m.epg_channel_id WHERE m.channel_key=c.key AND p.stop_utc+{ShiftSeconds}>@1 " +
            "AND p.title_norm LIKE @0 ESCAPE '\\') ORDER BY s.position, c.rowid",
            r => r.GetString(0), $"%{escaped}%", from.ToUnixTimeSeconds());
    }

    public void Prune(DateTimeOffset now, int retentionDays)
    {
        using var c = db.Open();
        Database.Exec(c, "DELETE FROM programme WHERE stop_utc<@0", now.AddDays(-retentionDays).ToUnixTimeSeconds());
    }

    private static long Shift(SqliteConnection c, string key) =>
        Convert.ToInt64(Database.Exec(c, $"SELECT {ShiftSeconds} FROM channel c WHERE c.key=@0", key) ?? 0L);

    private static Programme Map(SqliteDataReader r, long shift) => new(
        r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5) + shift), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6) + shift));
}
