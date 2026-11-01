using System.Diagnostics;
using Xunit;
using System.Text;
using M3uPlayer.Core.Playlist;
using M3uPlayer.Core.Storage;
using Microsoft.Data.Sqlite;

namespace M3uPlayer.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"m3u-{Guid.NewGuid():N}.db");
    private readonly Database _db;
    private readonly SourceStore _sources;
    private readonly ChannelStore _channels;
    private readonly ProgrammeStore _programmes;

    public StorageTests()
    {
        _db = new Database(_path);
        _sources = new SourceStore(_db);
        _channels = new ChannelStore(_db);
        _programmes = new ProgrammeStore(_db);
    }

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_path) + "*")) File.Delete(f);
    }

    private static M3uEntry Entry(string key, string name = "Chan", string category = "News", double shift = 0, int days = 0,
        string? tvgId = null) =>
        new(key, name, "http://example.invalid/" + key, tvgId, null, null, shift, category, null, null, days, null, null);

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static string Stamp(DateTimeOffset t) => t.ToString("yyyyMMddHHmmss +0000");

    private static MemoryStream Xml(string channelId, params (DateTimeOffset Start, DateTimeOffset? Stop, string Title)[] items)
    {
        var sb = new StringBuilder($"<tv><channel id=\"{channelId}\"><display-name>{channelId}</display-name></channel>");
        foreach (var i in items)
            sb.Append($"<programme start=\"{Stamp(i.Start)}\"{(i.Stop is { } s ? $" stop=\"{Stamp(s)}\"" : "")} channel=\"{channelId}\"><title>{i.Title}</title></programme>");
        return new MemoryStream(Encoding.UTF8.GetBytes(sb.Append("</tv>").ToString()));
    }

    private (long Pl, long Epg) Linked(string key = "A", string tvgId = "e1", string epgId = "e1")
    {
        var pl = _sources.Add(SourceKind.Playlist, "p", "P");
        _channels.Replace(pl, [Entry(key, tvgId: tvgId)]);
        var epg = _sources.Add(SourceKind.Epg, "e", "E");
        return (pl, epg);
    }

    [Fact]
    public void Schema_migrates_empty_database_and_reopens()
    {
        _sources.Add(SourceKind.Playlist, "p", "P");
        var again = new Database(_path);
        Assert.Single(new SourceStore(again).List(SourceKind.Playlist));
        using var c = again.Open();
        Assert.Equal(1L, Scalar(c, "PRAGMA user_version"));
        Assert.Equal("wal", Scalar(c, "PRAGMA journal_mode"));
    }

    [Fact]
    public void Settings_round_trip()
    {
        Assert.Null(_db.GetSetting("k"));
        _db.SetSetting("k", "v1");
        _db.SetSetting("k", "v2");
        Assert.Equal("v2", _db.GetSetting("k"));
    }

    [Fact]
    public void Source_ordering_and_move()
    {
        var a = _sources.Add(SourceKind.Playlist, "a", "A");
        var b = _sources.Add(SourceKind.Playlist, "b", "B");
        var c = _sources.Add(SourceKind.Playlist, "c", "C");
        _sources.Move(c, 0);
        Assert.Equal([c, a, b], _sources.List(SourceKind.Playlist).Select(s => s.Id));
        _sources.Move(c, 99);
        Assert.Equal([a, b, c], _sources.List(SourceKind.Playlist).Select(s => s.Id));
    }

    [Fact]
    public void Source_records_attempt_and_success()
    {
        var id = _sources.Add(SourceKind.Playlist, "a", "A");
        _sources.SetInterval(id, 60, false);
        _sources.RecordAttempt(id, Now, "boom");
        var s = _sources.List(SourceKind.Playlist).Single();
        Assert.Equal(("boom", 60, false, Now), (s.LastError, s.IntervalMinutes, s.AutoRefresh, s.LastAttempt));
        _sources.RecordSuccess(id, Now, "etag", "lm", 3);
        s = _sources.List(SourceKind.Playlist).Single();
        Assert.Equal((null, "etag", "lm", 3, Now), (s.LastError, s.ETag, s.LastModified, s.SkippedCount, s.LastSuccess));
    }

    [Fact]
    public void Remove_playlist_removes_its_channels_and_child_url_tvg_source()
    {
        var (pl, _) = Linked();
        var child = _sources.Add(SourceKind.Epg, "u", "child", pl);
        var other = _sources.Add(SourceKind.Epg, "o", "other");
        _programmes.ImportEpg(child, Xml("e1", (Now, Now.AddHours(1), "T")), Now, 7);
        _programmes.ImportEpg(other, Xml("e1", (Now, Now.AddHours(1), "T")), Now, 7);
        _programmes.RebuildMatches();
        _channels.SetFavorite("A", true);

        _sources.Remove(pl);

        Assert.Equal(0, _channels.CountAll());
        Assert.Equal([other], _sources.List(SourceKind.Epg).Select(s => s.Id).Where(i => i == other || i == child));
        using var c = _db.Open();
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(DISTINCT source_id) FROM programme"));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM channel_match"));
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM favorite"));
    }

    [Fact]
    public void Replace_keeps_favorite_across_refresh()
    {
        var pl = _sources.Add(SourceKind.Playlist, "p", "P");
        _channels.Replace(pl, [Entry("X")]);
        _channels.SetFavorite("X", true);
        _channels.Replace(pl, [Entry("X"), Entry("Y")]);
        Assert.True(_channels.All().Single(c => c.Key == "X").IsFavorite);
        Assert.Equal(1, _channels.CountFavorites());
    }

    [Fact]
    public void Favorite_survives_channel_absence()
    {
        var pl = _sources.Add(SourceKind.Playlist, "p", "P");
        _channels.Replace(pl, [Entry("X")]);
        _channels.SetFavorite("X", true);
        _channels.Replace(pl, [Entry("Y")]);
        Assert.Empty(_channels.Favorites());
        _channels.Replace(pl, [Entry("X")]);
        Assert.True(_channels.Favorites().Single().IsFavorite);
        _channels.SetFavorite("X", false);
        Assert.Empty(_channels.Favorites());
    }

    [Fact]
    public void Replace_suffixes_key_held_by_another_source_without_touching_it()
    {
        var a = _sources.Add(SourceKind.Playlist, "a", "A");
        var b = _sources.Add(SourceKind.Playlist, "b", "B");
        _channels.Replace(a, [Entry("K")]);
        _channels.Replace(b, [Entry("K")]);
        _channels.Replace(a, [Entry("K")]);
        Assert.Equal(["K", $"K#{b}"], _channels.All().Select(c => c.Key));
    }

    [Fact]
    public void Categories_merge_across_playlists_with_counts()
    {
        var a = _sources.Add(SourceKind.Playlist, "a", "A");
        var b = _sources.Add(SourceKind.Playlist, "b", "B");
        _channels.Replace(a, [Entry("1", category: "Sport"), Entry("2", category: "Film")]);
        _channels.Replace(b, [Entry("3", category: "Sport")]);
        Assert.Equal([new CategoryCount("Sport", 2), new CategoryCount("Film", 1)], _channels.Categories());
        Assert.Equal(2, _channels.ByCategory("Sport").Count);
    }

    [Fact]
    public void Categories_keep_playlist_order()
    {
        var a = _sources.Add(SourceKind.Playlist, "a", "A");
        _channels.Replace(a, [Entry("1", category: "Zeta"), Entry("2", category: "Alpha"), Entry("3", category: "Zeta")]);
        Assert.Equal(["Zeta", "Alpha"], _channels.Categories().Select(c => c.Name));
    }

    [Fact]
    public void Search_ignores_polish_letters()
    {
        var a = _sources.Add(SourceKind.Playlist, "a", "A");
        _channels.Replace(a, [Entry("1", "TVP Miłość"), Entry("2", "Other")]);
        Assert.Equal("1", _channels.Search("milosc").Single().Key);
        Assert.Empty(_channels.Search("  "));
    }

    [Fact]
    public void Search_matches_like_wildcards_literally()
    {
        var a = _sources.Add(SourceKind.Playlist, "a", "A");
        _channels.Replace(a, [Entry("1", "100% Sport"), Entry("2", "a_b"), Entry("3", "axb"), Entry("4", "Plain")]);
        Assert.Equal("1", _channels.Search("%").Single().Key);
        Assert.Equal("2", _channels.Search("a_b").Single().Key);
        Assert.Empty(_channels.Search("x' OR 1=1 --"));
    }

    [Fact]
    public void MaxCatchupDays_returns_largest_or_zero()
    {
        var a = _sources.Add(SourceKind.Playlist, "a", "A");
        _channels.Replace(a, [Entry("1")]);
        Assert.Equal(0, _channels.MaxCatchupDays());
        _channels.Replace(a, [Entry("1", days: 3), Entry("2", days: 7)]);
        Assert.Equal(7, _channels.MaxCatchupDays());
    }

    [Fact]
    public void MaxCatchupDays_clamps_untrusted_values()
    {
        var a = _sources.Add(SourceKind.Playlist, "a", "A");
        _channels.Replace(a, [Entry("1", days: 99999)]);
        Assert.Equal(30, _channels.MaxCatchupDays());
    }

    [Fact]
    public void Import_replaces_only_from_earliest_new_start()
    {
        var (_, epg) = Linked();
        var h = (int n) => Now.AddHours(n);
        _programmes.ImportEpg(epg, Xml("e1", (h(0), h(1), "old0"), (h(1), h(2), "old1"), (h(2), h(3), "old2")), Now, 7);
        _programmes.ImportEpg(epg, Xml("e1", (h(1), h(2), "new1"), (h(2), h(3), "new2")), Now, 7);
        _programmes.RebuildMatches();
        var titles = _programmes.GetGuide("A", h(-1), h(10)).Select(p => p.Title);
        Assert.Equal(["old0", "new1", "new2"], titles);
    }

    [Fact]
    public void Import_invalid_stream_keeps_old_data()
    {
        var (_, epg) = Linked();
        _programmes.ImportEpg(epg, Xml("e1", (Now, Now.AddHours(1), "old")), Now, 7);
        _programmes.RebuildMatches();
        var truncated = Xml("e1", (Now, Now.AddHours(1), "new"));
        var broken = new MemoryStream(truncated.ToArray()[..^20]);
        Assert.ThrowsAny<Exception>(() => _programmes.ImportEpg(epg, broken, Now, 7));
        Assert.Equal("old", _programmes.GetNowNext("A", Now.AddMinutes(1)).Now?.Title);
        using var c = _db.Open();
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM epg_channel"));
    }

    [Fact]
    public void Import_without_programmes_throws_and_keeps_old_data()
    {
        var (_, epg) = Linked();
        _programmes.ImportEpg(epg, Xml("e1", (Now, Now.AddHours(1), "old")), Now, 7);
        var ex = Assert.Throws<InvalidDataException>(() => _programmes.ImportEpg(epg, Xml("e1"), Now, 7));
        Assert.DoesNotContain("http", ex.Message);
        _programmes.RebuildMatches();
        Assert.Equal("old", _programmes.GetNowNext("A", Now.AddMinutes(1)).Now?.Title);
    }

    [Fact]
    public void Import_missing_stop_uses_next_start_in_channel()
    {
        var (_, epg) = Linked();
        _programmes.ImportEpg(epg, Xml("e1", (Now, null, "a"), (Now.AddMinutes(30), Now.AddHours(1), "b")), Now, 7);
        _programmes.RebuildMatches();
        Assert.Equal(Now.AddMinutes(30), _programmes.GetGuide("A", Now, Now.AddHours(2))[0].Stop);
    }

    [Fact]
    public void Import_missing_stop_on_last_programme_uses_one_hour()
    {
        var (_, epg) = Linked();
        _programmes.ImportEpg(epg, Xml("e1", (Now, null, "a")), Now, 7);
        _programmes.RebuildMatches();
        Assert.Equal(Now.AddHours(1), _programmes.GetGuide("A", Now, Now.AddHours(2))[0].Stop);
    }

    [Fact]
    public void Prune_removes_older_than_retention_only()
    {
        var (_, epg) = Linked();
        _programmes.ImportEpg(epg, Xml("e1", (Now.AddDays(-10), Now.AddDays(-10).AddHours(1), "old"), (Now, Now.AddHours(1), "new")), Now.AddDays(-30), 7);
        _programmes.RebuildMatches();
        _programmes.Prune(Now, 7);
        Assert.Equal(["new"], _programmes.GetGuide("A", Now.AddDays(-20), Now.AddDays(1)).Select(p => p.Title));
    }

    [Fact]
    public void Rebuild_first_source_by_position_wins()
    {
        var (_, e1) = Linked();
        var e2 = _sources.Add(SourceKind.Epg, "e2", "E2");
        _programmes.ImportEpg(e1, Xml("e1", (Now, Now.AddHours(1), "from1")), Now, 7);
        _programmes.ImportEpg(e2, Xml("e1", (Now, Now.AddHours(1), "from2")), Now, 7);
        _programmes.RebuildMatches();
        Assert.Equal("from1", _programmes.GetNowNext("A", Now).Now?.Title);
        _sources.Move(e2, 0);
        _programmes.RebuildMatches();
        Assert.Equal("from2", _programmes.GetNowNext("A", Now).Now?.Title);
    }

    [Fact]
    public void Rebuild_records_matched_count()
    {
        var (pl, epg) = Linked();
        _channels.Replace(pl, [Entry("A", tvgId: "e1"), Entry("B", tvgId: "nope")]);
        _programmes.ImportEpg(epg, Xml("e1", (Now, Now.AddHours(1), "t")), Now, 7);
        _programmes.RebuildMatches();
        var e = _sources.List(SourceKind.Epg).Single();
        var p = _sources.List(SourceKind.Playlist).Single();
        Assert.Equal((1, 1, 2, 1), (e.MatchedCount, e.ChannelCount, p.ChannelCount, p.MatchedCount));
    }

    [Fact]
    public void GetNowNext_returns_current_and_following()
    {
        var (_, epg) = Linked();
        _programmes.ImportEpg(epg, Xml("e1", (Now, Now.AddHours(1), "a"), (Now.AddHours(1), Now.AddHours(2), "b")), Now, 7);
        _programmes.RebuildMatches();
        var nn = _programmes.GetNowNext("A", Now.AddMinutes(10));
        Assert.Equal(("a", "b"), (nn.Now?.Title, nn.Next?.Title));
    }

    [Fact]
    public void GetNowNext_applies_tvg_shift()
    {
        var pl = _sources.Add(SourceKind.Playlist, "p", "P");
        _channels.Replace(pl, [Entry("A", tvgId: "e1", shift: -1.5)]);
        var epg = _sources.Add(SourceKind.Epg, "e", "E");
        _programmes.ImportEpg(epg, Xml("e1", (Now, Now.AddHours(1), "a")), Now, 7);
        _programmes.RebuildMatches();
        Assert.Null(_programmes.GetNowNext("A", Now.AddMinutes(10)).Now);
        var nn = _programmes.GetNowNext("A", Now.AddMinutes(-80));
        Assert.Equal(Now.AddMinutes(-90), nn.Now?.Start);
        Assert.Single(_programmes.GetGuide("A", Now.AddMinutes(-80), Now.AddMinutes(-70)));
    }

    [Fact]
    public void GetNowNext_no_epg_gives_nulls()
    {
        Linked();
        var nn = _programmes.GetNowNext("A", Now);
        Assert.Null(nn.Now);
        Assert.Null(nn.Next);
        Assert.Equal(new NowNext(null, null), _programmes.GetNowNext("missing", Now));
    }

    [Fact]
    public void GetGuide_returns_ordered_range()
    {
        var (_, epg) = Linked();
        _programmes.ImportEpg(epg, Xml("e1", (Now.AddHours(2), Now.AddHours(3), "c"), (Now, Now.AddHours(1), "a"), (Now.AddHours(1), Now.AddHours(2), "b")), Now, 7);
        _programmes.RebuildMatches();
        Assert.Equal(["b", "c"], _programmes.GetGuide("A", Now.AddMinutes(90), Now.AddHours(2.5)).Select(p => p.Title));
    }

    [Fact]
    public void SearchProgrammeChannels_finds_by_normalized_title()
    {
        var (_, epg) = Linked();
        _programmes.ImportEpg(epg, Xml("e1", (Now, Now.AddHours(1), "Wiadomości"), (Now.AddHours(-5), Now.AddHours(-4), "Przeszłość")), Now, 7);
        _programmes.RebuildMatches();
        Assert.Equal(["A"], _programmes.SearchProgrammeChannels("wiadomosci", Now));
        Assert.Empty(_programmes.SearchProgrammeChannels("przeszlosc", Now));
        Assert.Empty(_programmes.SearchProgrammeChannels("%", Now));
    }

    [Fact]
    public void GetNowNext_during_open_import_returns_old_data()
    {
        var (_, epg) = Linked();
        _programmes.ImportEpg(epg, Xml("e1", (Now, Now.AddHours(1), "old")), Now, 7);
        _programmes.RebuildMatches();
        NowNext? during = null;
        var hooked = new HookStream(Xml("e1", (Now, Now.AddHours(1), "new")).ToArray(),
            () => during = _programmes.GetNowNext("A", Now.AddMinutes(1)));
        _programmes.ImportEpg(epg, hooked, Now, 7);
        Assert.Equal("old", during?.Now?.Title);
        Assert.Equal("new", _programmes.GetNowNext("A", Now.AddMinutes(1)).Now?.Title);
    }

    [Fact]
    public void Import_handles_100k_programmes_quickly()
    {
        var (_, epg) = Linked();
        var sb = new StringBuilder("<tv>");
        for (var ch = 0; ch < 100; ch++) sb.Append($"<channel id=\"c{ch}\"><display-name>C{ch}</display-name></channel>");
        for (var ch = 0; ch < 100; ch++)
            for (var i = 0; i < 1100; i++)
                sb.Append($"<programme start=\"{Stamp(Now.AddMinutes(i * 10))}\" stop=\"{Stamp(Now.AddMinutes(i * 10 + 10))}\" channel=\"c{ch}\"><title>Show {i}</title><desc>d</desc></programme>");
        var data = new MemoryStream(Encoding.UTF8.GetBytes(sb.Append("</tv>").ToString()));

        var sw = Stopwatch.StartNew();
        _programmes.ImportEpg(epg, data, Now, 7);
        sw.Stop();

        Console.WriteLine($"Import of 110000 programmes took {sw.Elapsed.TotalSeconds:F2}s");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}");
        using var c = _db.Open();
        Assert.Equal(110000L, Scalar(c, "SELECT COUNT(*) FROM programme"));
    }

    private sealed class HookStream(byte[] data, Action onFirstRead) : MemoryStream(data)
    {
        private bool _fired;
        private void Fire() { if (_fired) return; _fired = true; onFirstRead(); }
        public override int Read(byte[] buffer, int offset, int count) { Fire(); return base.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { Fire(); return base.Read(buffer); }
        public override int ReadByte() { Fire(); return base.ReadByte(); }
    }
}
