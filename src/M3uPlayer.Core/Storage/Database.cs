using Microsoft.Data.Sqlite;

namespace M3uPlayer.Core.Storage;

public class Database
{
    private const string Schema = """
        CREATE TABLE source(
            id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, location TEXT NOT NULL, name TEXT NOT NULL,
            position INTEGER NOT NULL, interval_minutes INTEGER NOT NULL DEFAULT 720, auto_refresh INTEGER NOT NULL DEFAULT 1,
            parent_playlist_id INTEGER REFERENCES source(id) ON DELETE CASCADE,
            etag TEXT, last_modified TEXT, last_attempt INTEGER, last_success INTEGER, last_error TEXT,
            skipped_count INTEGER NOT NULL DEFAULT 0, matched_count INTEGER NOT NULL DEFAULT 0, channel_count INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE channel(
            key TEXT PRIMARY KEY, source_id INTEGER NOT NULL REFERENCES source(id) ON DELETE CASCADE,
            name TEXT NOT NULL, norm_name TEXT NOT NULL, url TEXT NOT NULL, logo TEXT, category TEXT NOT NULL,
            shift_hours REAL NOT NULL, catchup_type TEXT, catchup_source TEXT, catchup_days INTEGER NOT NULL,
            user_agent TEXT, referrer TEXT, tvg_id TEXT, tvg_name TEXT);
        CREATE INDEX channel_source ON channel(source_id);
        CREATE TABLE favorite(channel_key TEXT PRIMARY KEY);
        CREATE TABLE epg_channel(
            source_id INTEGER NOT NULL REFERENCES source(id) ON DELETE CASCADE,
            ord INTEGER NOT NULL, id TEXT NOT NULL, names_json TEXT NOT NULL, PRIMARY KEY(source_id, ord));
        CREATE TABLE programme(
            source_id INTEGER NOT NULL REFERENCES source(id) ON DELETE CASCADE,
            epg_channel_id TEXT NOT NULL, start_utc INTEGER NOT NULL, stop_utc INTEGER NOT NULL,
            title TEXT NOT NULL, title_norm TEXT NOT NULL, sub_title TEXT, description TEXT, category TEXT, icon_url TEXT);
        CREATE INDEX programme_lookup ON programme(source_id, epg_channel_id, start_utc);
        CREATE TABLE channel_match(
            channel_key TEXT PRIMARY KEY REFERENCES channel(key) ON DELETE CASCADE,
            source_id INTEGER NOT NULL REFERENCES source(id) ON DELETE CASCADE, epg_channel_id TEXT NOT NULL);
        CREATE INDEX channel_match_source ON channel_match(source_id);
        CREATE TABLE setting(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """;

    private readonly string _connectionString;

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL");
        var version = Convert.ToInt32(Exec(c, "PRAGMA user_version"));
        if (version < 1)
        {
            using var tx = c.BeginTransaction();
            Exec(c, Schema);
            Exec(c, "PRAGMA user_version=1");
            tx.Commit();
        }
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        try
        {
            c.Open();
            Exec(c, "PRAGMA foreign_keys=ON");
            Exec(c, "PRAGMA busy_timeout=10000");
            Exec(c, "PRAGMA journal_size_limit=33554432"); // a big import must not leave a huge WAL behind
            return c;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    /// <summary>Returns the space of deleted rows to the file. Rewrites the whole database: seconds on a big one, so never on the UI thread.</summary>
    public void Compact()
    {
        using var c = Open();
        Exec(c, "VACUUM");
        Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)"); // in WAL mode the file only shrinks once the log is folded back
    }

    public string? GetSetting(string key)
    {
        using var c = Open();
        return Exec(c, "SELECT value FROM setting WHERE key=@0", key) as string;
    }

    public void SetSetting(string key, string value)
    {
        using var c = Open();
        Exec(c, "INSERT INTO setting(key,value) VALUES(@0,@1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);
    }

    internal static SqliteCommand Cmd(SqliteConnection c, string sql, params object?[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("@" + i, args[i] ?? DBNull.Value);
        return cmd;
    }

    /// <summary>Runs a statement with positional parameters @0, @1, ...; returns the first column of the first row (null when none).</summary>
    internal static object? Exec(SqliteConnection c, string sql, params object?[] args)
    {
        using var cmd = Cmd(c, sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    internal static List<T> Rows<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        using var cmd = Cmd(c, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }
}
