using System.Net;
using System.Text;
using M3uPlayer.Core.Diagnostics;
using M3uPlayer.Core.Playlist;
using M3uPlayer.Core.Refresh;
using M3uPlayer.Core.Storage;
using M3uPlayer.Core.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace M3uPlayer.Core.Tests;

internal sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this(r => Task.FromResult(respond(r))) { }
    public List<(string Url, string? IfNoneMatch, string? IfModifiedSince)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.RequestUri!.ToString(), request.Headers.TryGetValues("If-None-Match", out var n) ? n.Single() : null,
            request.Headers.TryGetValues("If-Modified-Since", out var m) ? m.Single() : null));
        return respond(request);
    }
}

internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class RefreshTests : IDisposable
{
    private const string PlUrl = "http://p.invalid/a.m3u";
    private const string EpgUrl = "http://e.invalid/epg.xml";
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"m3u-r-{Guid.NewGuid():N}");
    private readonly Database _db;
    private readonly SourceStore _sources;
    private readonly ChannelStore _channels;
    private readonly ProgrammeStore _programmes;
    private readonly TestClock _clock = new(T0);
    private readonly Dictionary<string, (HttpStatusCode Status, string Body, string? ETag)> _served = [];
    private readonly FakeHandler _handler;
    private readonly RefreshService _svc;
    private int _active, _maxActive;
    private string? _holdUrl;
    private readonly TaskCompletionSource _hold = new(), _entered = new();

    public RefreshTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new Database(Path.Combine(_dir, "t.db"));
        _sources = new SourceStore(_db);
        _channels = new ChannelStore(_db);
        _programmes = new ProgrammeStore(_db);
        _handler = new FakeHandler(async r =>
        {
            var now = Interlocked.Increment(ref _active);
            _maxActive = Math.Max(_maxActive, now);
            await Task.Delay(30, TestContext.Current.CancellationToken);
            if (r.RequestUri!.ToString() == _holdUrl) { _entered.TrySetResult(); await _hold.Task; }
            Interlocked.Decrement(ref _active);
            if (!_served.TryGetValue(r.RequestUri!.ToString(), out var s)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var resp = new HttpResponseMessage(s.Status) { Content = new StringContent(s.Body) };
            if (s.ETag is not null) resp.Headers.TryAddWithoutValidation("ETag", s.ETag);
            return resp;
        });
        _svc = Service(new FileLog(Path.Combine(_dir, "log.txt")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string TmpDir => Path.Combine(_dir, "tmp");

    private RefreshService Service(FileLog log) => new(_db,
        new SourceFetcher(new HttpClient(_handler), [], (_, _) => Task.CompletedTask, TmpDir), _clock, log);

    private static string Playlist(string tvg = EpgUrl, int days = 0, string id = "e1") =>
        $"#EXTM3U url-tvg=\"{tvg}\"\n#EXTINF:-1 tvg-id=\"{id}\" catchup-days=\"{days}\",Chan\nhttp://s.invalid/1\n";

    private static string Stamp(DateTimeOffset t) => t.ToString("yyyyMMddHHmmss +0000");

    private static string Xml(params (string Ch, DateTimeOffset Start, string Title)[] items) =>
        "<tv><channel id=\"e1\"><display-name>Chan</display-name></channel>" + string.Concat(items.Select(i =>
            $"<programme start=\"{Stamp(i.Start)}\" stop=\"{Stamp(i.Start.AddHours(1))}\" channel=\"{i.Ch}\"><title>{i.Title}</title></programme>")) + "</tv>";

    private void Serve(string url, string body, HttpStatusCode status = HttpStatusCode.OK, string? etag = null) =>
        _served[url] = (status, body, etag);

    private Source Get(long id) => _sources.List(SourceKind.Playlist).Concat(_sources.List(SourceKind.Epg)).Single(s => s.Id == id);

    private static string Many(int n) =>
        "#EXTM3U\n" + string.Concat(Enumerable.Range(0, n).Select(i => $"#EXTINF:-1 tvg-id=\"c{i}\",Chan{i}\nhttp://s.invalid/{i}\n"));

    private void Sql(string sql)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed class NeverStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    private long AddPlaylist(string url = PlUrl) => _sources.Add(SourceKind.Playlist, url, "P");

    private long AddEpg(string url = EpgUrl) => _sources.Add(SourceKind.Epg, url, "E");

    // ---- Fetcher ----

    private SourceFetcher Fetcher(List<TimeSpan>? delays = null, long max = 500_000_000, params TimeSpan[] retry) =>
        new(new HttpClient(_handler), retry, (d, _) => { delays?.Add(d); return Task.CompletedTask; }, TmpDir, max);

    private Source Src(string location, string? etag = null, string? lastModified = null, bool success = false)
    {
        var id = _sources.Add(SourceKind.Playlist, location, "S");
        if (success) _sources.RecordSuccess(id, T0, etag, lastModified, 0);
        return Get(id);
    }

    [Fact]
    public async Task Fetch_sends_etag_and_if_modified_since()
    {
        Serve(PlUrl, "x");
        var r = await Fetcher().FetchAsync(Src(PlUrl, "\"v1\"", "Sat, 01 Nov 2026 10:00:00 GMT", true), TestContext.Current.CancellationToken);
        Assert.Equal(("\"v1\"", "Sat, 01 Nov 2026 10:00:00 GMT"), (_handler.Requests.Single().IfNoneMatch, _handler.Requests.Single().IfModifiedSince));
        File.Delete(r.TempFilePath!);
    }

    [Fact]
    public async Task Fetch_304_returns_not_modified_without_file()
    {
        Serve(PlUrl, "", HttpStatusCode.NotModified);
        var r = await Fetcher().FetchAsync(Src(PlUrl, "\"v1\"", null, true), TestContext.Current.CancellationToken);
        Assert.True(r.NotModified);
        Assert.Null(r.TempFilePath);
        Assert.False(Directory.Exists(TmpDir) && Directory.GetFiles(TmpDir).Length > 0);
    }

    [Fact]
    public async Task Fetch_local_file_unchanged_mtime_not_modified()
    {
        var file = Path.Combine(_dir, "l.m3u");
        File.WriteAllText(file, Playlist());
        var first = await Fetcher().FetchAsync(Src(file), TestContext.Current.CancellationToken);
        Assert.False(first.NotModified);
        Assert.Null(first.ETag);
        Assert.Equal(File.GetLastWriteTimeUtc(file).ToString("O"), first.LastModified);
        File.Delete(first.TempFilePath!);
        var second = await Fetcher().FetchAsync(Src(file, null, first.LastModified, true), TestContext.Current.CancellationToken);
        Assert.True(second.NotModified);
    }

    [Fact]
    public async Task Fetch_missing_local_file_throws()
    {
        await Assert.ThrowsAnyAsync<IOException>(() => Fetcher().FetchAsync(Src(Path.Combine(_dir, "none.m3u")), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ftp://h.invalid/x.m3u")]
    [InlineData("file:///c:/x.m3u")]
    [InlineData("relative/x.m3u")]
    public async Task Fetch_rejects_other_schemes_and_relative_paths(string location)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fetcher().FetchAsync(Src(location), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Fetch_retries_three_times_with_given_delays_then_throws()
    {
        Serve(PlUrl, "", HttpStatusCode.InternalServerError);
        var delays = new List<TimeSpan>();
        var f = Fetcher(delays, 500_000_000, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120));
        await Assert.ThrowsAsync<HttpRequestException>(() => f.FetchAsync(Src(PlUrl), TestContext.Current.CancellationToken));
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)], delays);
        Assert.Equal(4, _handler.Requests.Count);
    }

    [Fact]
    public async Task Fetch_error_message_contains_no_token()
    {
        var url = "http://h.invalid/get.php?username=u&password=abc123";
        Serve(url, "", HttpStatusCode.Forbidden);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Fetcher().FetchAsync(Src(url), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("abc123", Redactor.Redact(ex.Message));
    }

    [Fact]
    public async Task Fetch_aborts_and_removes_file_when_over_size_cap()
    {
        Serve(PlUrl, new string('x', 5000));
        await Assert.ThrowsAsync<InvalidDataException>(() => Fetcher(max: 1000).FetchAsync(Src(PlUrl), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(TmpDir));
    }

    [Fact]
    public async Task Fetch_rejects_redirect_that_ends_on_non_http()
    {
        var h = new FakeHandler((Func<HttpRequestMessage, HttpResponseMessage>)(r => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("x"), RequestMessage = new HttpRequestMessage(HttpMethod.Get, "ftp://h.invalid/x") }));
        var f = new SourceFetcher(new HttpClient(h), [], (_, _) => Task.CompletedTask, TmpDir);
        await Assert.ThrowsAsync<HttpRequestException>(() => f.FetchAsync(Src(PlUrl), TestContext.Current.CancellationToken));
    }

    // ---- Refresh ----

    [Fact]
    public async Task Refresh_html_response_keeps_channels_and_favorites()
    {
        var id = AddPlaylist();
        Serve(PlUrl, Playlist());
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        _channels.SetFavorite("e1", true);
        var ok = Get(id);
        _clock.Now = T0.AddHours(1);
        Serve(PlUrl, "<html>nope</html>");

        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);

        var s = Get(id);
        Assert.Equal(1, _channels.CountAll());
        Assert.Equal(1, _channels.CountFavorites());
        Assert.NotNull(s.LastError);
        Assert.Equal(ok.LastSuccess, s.LastSuccess);
        Assert.Equal(T0.AddHours(1), s.LastAttempt);
    }

    [Fact]
    public async Task Refresh_empty_playlist_keeps_channels()
    {
        var id = AddPlaylist();
        Serve(PlUrl, Playlist());
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        Serve(PlUrl, "#EXTM3U\n");
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(1, _channels.CountAll());
        Assert.NotNull(Get(id).LastError);
    }

    [Fact]
    public async Task Refresh_failed_import_does_not_record_etag()
    {
        var id = AddPlaylist();
        Serve(PlUrl, "<html>", HttpStatusCode.OK, "\"v9\"");
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        Assert.Null(Get(id).ETag);
        Serve(PlUrl, Playlist(), HttpStatusCode.OK, "\"v9\"");
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(("\"v9\"", null), (Get(id).ETag, Get(id).LastError));
        Assert.Equal(1, _channels.CountAll());
    }

    [Fact]
    public async Task Refresh_not_modified_keeps_data_without_error()
    {
        var id = AddPlaylist();
        Serve(PlUrl, Playlist(), HttpStatusCode.OK, "\"v1\"");
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        var first = Get(id);
        _clock.Now = T0.AddHours(1);
        Serve(PlUrl, "", HttpStatusCode.NotModified);
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        var s = Get(id);
        Assert.Equal((null, T0.AddHours(1), T0.AddHours(1), 1), (s.LastError, s.LastSuccess, s.LastAttempt, _channels.CountAll()));
    }

    [Fact]
    public async Task Refresh_truncated_epg_keeps_programmes_and_records_error()
    {
        var pl = AddPlaylist();
        var epg = AddEpg();
        Serve(PlUrl, Playlist());
        Serve(EpgUrl, Xml(("e1", T0, "Old")));
        await _svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        await _svc.RefreshAsync(epg, TestContext.Current.CancellationToken);
        var good = Xml(("e1", T0, "New"), ("e1", T0.AddHours(1), "New2"));
        Serve(EpgUrl, good[..(good.Length * 2 / 3)]);

        await _svc.RefreshAsync(epg, TestContext.Current.CancellationToken);

        Assert.NotNull(Get(epg).LastError);
        Assert.Equal("Old", _programmes.GetNowNext("e1", T0.AddMinutes(5)).Now?.Title);
    }

    [Fact]
    public async Task Refresh_playlist_adds_url_tvg_source_once()
    {
        var pl = AddPlaylist();
        var manual = AddEpg("http://m.invalid/m.xml");
        Serve(PlUrl, Playlist());
        await _svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        await _svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        var epgs = _sources.List(SourceKind.Epg);
        Assert.Equal(2, epgs.Count);
        Assert.Equal(manual, epgs[0].Id);
        Assert.Equal((EpgUrl, pl), (epgs[1].Location, epgs[1].ParentPlaylistId));
    }

    [Fact]
    public async Task Refresh_playlist_updates_url_tvg_child_when_address_changes_and_keeps_it_when_gone()
    {
        var pl = AddPlaylist();
        Serve(PlUrl, Playlist());
        await _svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        var childId = _sources.List(SourceKind.Epg).Single().Id;
        Serve(PlUrl, Playlist("http://e2.invalid/new.xml"));
        await _svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        var child = _sources.List(SourceKind.Epg).Single();
        Assert.Equal((childId, "http://e2.invalid/new.xml"), (child.Id, child.Location));
        Serve(PlUrl, "#EXTM3U\n#EXTINF:-1,Chan\nhttp://s.invalid/1\n");
        await _svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        Assert.Equal("http://e2.invalid/new.xml", _sources.List(SourceKind.Epg).Single().Location);
    }

    [Fact]
    public async Task Refresh_due_refreshes_playlists_then_new_url_tvg_child_in_one_pass()
    {
        var pl = AddPlaylist();
        Serve(PlUrl, Playlist());
        Serve(EpgUrl, Xml(("e1", T0, "Show")));
        await _svc.RefreshDueAsync(TestContext.Current.CancellationToken);
        var child = _sources.List(SourceKind.Epg).Single();
        Assert.NotNull(child.LastSuccess);
        Assert.Equal(1, Get(pl).MatchedCount);
        Assert.Equal(new[] { PlUrl, EpgUrl }, _handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task Refresh_uses_max_catchup_days_for_retention()
    {
        var pl = AddPlaylist();
        var epg = AddEpg();
        Serve(PlUrl, Playlist(days: 10));
        Serve(EpgUrl, Xml(("e1", T0.AddDays(-9), "Archive"), ("e1", T0, "Now")));
        await _svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        await _svc.RefreshAsync(epg, TestContext.Current.CancellationToken);
        Assert.Contains(_programmes.GetGuide("e1", T0.AddDays(-10), T0.AddDays(-8)), p => p.Title == "Archive");
    }

    [Fact]
    public async Task Refresh_no_catchup_uses_one_day_retention()
    {
        var pl = AddPlaylist();
        var epg = AddEpg();
        Serve(PlUrl, Playlist());
        Serve(EpgUrl, Xml(("e1", T0.AddDays(-2), "Old"), ("e1", T0, "Now")));
        await _svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        await _svc.RefreshAsync(epg, TestContext.Current.CancellationToken);
        Assert.Empty(_programmes.GetGuide("e1", T0.AddDays(-3), T0.AddDays(-1)));
        Assert.Single(_programmes.GetGuide("e1", T0.AddMinutes(1), T0.AddMinutes(2)));
    }

    [Fact]
    public async Task Refresh_rebuilds_matches_and_raises_DataChanged()
    {
        var pl = AddPlaylist();
        var epg = AddEpg();
        Serve(PlUrl, Playlist());
        Serve(EpgUrl, Xml(("e1", T0, "Show")));
        var raised = 0;
        _svc.DataChanged += () => raised++;
        await _svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        await _svc.RefreshAsync(epg, TestContext.Current.CancellationToken);
        Assert.Equal(2, raised);
        Assert.Equal((1, 1), (Get(epg).MatchedCount, Get(pl).MatchedCount));
        Assert.Equal("Show", _programmes.GetNowNext("e1", T0.AddMinutes(1)).Now?.Title);
    }

    [Fact]
    public async Task Refresh_failure_does_not_raise_DataChanged()
    {
        var pl = AddPlaylist();
        Serve(PlUrl, "<html>");
        var raised = 0;
        _svc.DataChanged += () => raised++;
        await _svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Refresh_stored_error_is_redacted()
    {
        var url = "http://h.invalid/get.php?username=u&password=abc123";
        var pl = AddPlaylist(url);
        var h = new FakeHandler((Func<HttpRequestMessage, HttpResponseMessage>)(r => throw new HttpRequestException("failed " + r.RequestUri)));
        var svc = new RefreshService(_db, new SourceFetcher(new HttpClient(h), [], (_, _) => Task.CompletedTask, TmpDir), _clock,
            new FileLog(Path.Combine(_dir, "log.txt")));
        await svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        var err = Get(pl).LastError!;
        Assert.DoesNotContain("abc123", err);
        Assert.Contains("***", err);
        Assert.DoesNotContain("abc123", File.ReadAllText(Path.Combine(_dir, "log.txt")));
    }

    [Fact]
    public async Task Refresh_log_failure_does_not_mask_error_or_abort()
    {
        var pl = AddPlaylist();
        Serve(PlUrl, "<html>");
        var svc = Service(new FileLog(Path.Combine(_dir, "missing", "log.txt")));
        await svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        Assert.NotNull(Get(pl).LastError);
    }

    [Fact]
    public async Task Refresh_cancellation_propagates_and_records_no_error()
    {
        var pl = AddPlaylist();
        Serve(PlUrl, Playlist());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _svc.RefreshAsync(pl, cts.Token));
        Assert.Null(Get(pl).LastError);
    }

    [Fact]
    public async Task Refresh_serializes_concurrent_calls()
    {
        var a = AddPlaylist();
        var b = AddPlaylist("http://p2.invalid/b.m3u");
        Serve(PlUrl, Playlist());
        Serve("http://p2.invalid/b.m3u", Playlist(id: "e2"));
        await Task.WhenAll(_svc.RefreshAsync(a, TestContext.Current.CancellationToken), _svc.RefreshAsync(b, TestContext.Current.CancellationToken), _svc.RefreshDueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, _maxActive);
    }

    [Fact]
    public async Task Refresh_deletes_temp_file_on_success_and_failure()
    {
        var pl = AddPlaylist();
        Serve(PlUrl, Playlist());
        await _svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        Assert.Empty(Directory.GetFiles(TmpDir));
        Serve(PlUrl, "<html>");
        await _svc.RefreshAsync(pl, TestContext.Current.CancellationToken);
        Assert.Empty(Directory.GetFiles(TmpDir));
    }

    [Fact]
    public async Task Run_rebuilds_matches_at_startup_and_survives_failures_until_cancelled()
    {
        var pl = AddPlaylist("ftp://bad.invalid/x");
        _channels.Replace(pl, [new M3uEntry("e1", "Chan", "http://s.invalid/1", "e1", null, null, 0, "News", null, null, 0, null, null)]);
        var epg = AddEpg("ftp://bad.invalid/y");
        _programmes.ImportEpg(epg, new MemoryStream(Encoding.UTF8.GetBytes(Xml(("e1", T0, "Show")))), T0, 1);
        _sources.SetInterval(epg, 720, false); // only the playlist is attempted, and it fails
        var svc = Service(new FileLog(Path.Combine(_dir, "missing", "log.txt")));
        using var cts = new CancellationTokenSource();

        var run = svc.RunAsync(cts.Token);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.False(run.IsCompleted);
        Assert.Equal("Show", _programmes.GetNowNext("e1", T0.AddMinutes(1)).Now?.Title);
        Assert.NotNull(Get(pl).LastError);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Fetch_stalled_body_times_out_as_failure()
    {
        var h = new FakeHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverStream()) }));
        var f = new SourceFetcher(new HttpClient(h), [], (_, _) => Task.CompletedTask, TmpDir, idleTimeout: TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAsync<TimeoutException>(() => f.FetchAsync(Src(PlUrl), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(TmpDir) && Directory.GetFiles(TmpDir).Length > 0);
    }

    [Fact]
    public async Task Fetch_caller_cancellation_is_not_a_timeout()
    {
        var h = new FakeHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverStream()) }));
        var f = new SourceFetcher(new HttpClient(h), [], (_, _) => Task.CompletedTask, TmpDir);
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.FetchAsync(Src(PlUrl), cts.Token));
    }

    [Fact]
    public async Task Fetch_404_is_not_retried_but_429_is()
    {
        var delays = new List<TimeSpan>();
        var f = Fetcher(delays, 500_000_000, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<HttpRequestException>(() => f.FetchAsync(Src(PlUrl), TestContext.Current.CancellationToken));
        Assert.Empty(delays);
        Assert.Single(_handler.Requests);
        Serve(PlUrl, "", (HttpStatusCode)429);
        await Assert.ThrowsAsync<HttpRequestException>(() => f.FetchAsync(Src(PlUrl), TestContext.Current.CancellationToken));
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Refresh_big_drop_keeps_channels_favorites_and_validators()
    {
        var id = AddPlaylist();
        Serve(PlUrl, Many(10), HttpStatusCode.OK, "\"v1\"");
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        _channels.SetFavorite("c3", true);
        Serve(PlUrl, Many(4), HttpStatusCode.OK, "\"v2\"");
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        var s = Get(id);
        Assert.Equal((10, 1, "\"v1\""), (_channels.CountAll(), _channels.CountFavorites(), s.ETag));
        Assert.Contains("re-add", s.LastError);
        Assert.Contains("4", s.LastError);
        Assert.Contains("10", s.LastError);
    }

    [Fact]
    public async Task Refresh_drop_to_exactly_half_succeeds()
    {
        var id = AddPlaylist();
        Serve(PlUrl, Many(10));
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        Serve(PlUrl, Many(5));
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal((5, null), (_channels.CountAll(), Get(id).LastError));
    }

    [Fact]
    public async Task Refresh_small_list_may_shrink_freely()
    {
        var id = AddPlaylist();
        Serve(PlUrl, Many(9));
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        Serve(PlUrl, Many(1));
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal((1, null), (_channels.CountAll(), Get(id).LastError));
    }

    [Fact]
    public async Task Refresh_manual_request_gets_in_between_sources_of_a_pass()
    {
        const string a = "http://a.invalid/a.m3u", b = "http://b.invalid/b.m3u";
        AddPlaylist(a);
        AddPlaylist(b);
        var manual = AddPlaylist(PlUrl);
        _sources.SetInterval(manual, 720, false);
        Serve(a, Many(1)); Serve(b, Many(1)); Serve(PlUrl, Many(1));
        _holdUrl = a;
        var pass = _svc.RefreshDueAsync(TestContext.Current.CancellationToken);
        await _entered.Task;
        var manualRun = _svc.RefreshAsync(manual, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _hold.SetResult();
        await Task.WhenAll(pass, manualRun);
        Assert.Equal([a, PlUrl, b], _handler.Requests.Select(r => r.Url).Where(u => u != EpgUrl));
    }

    [Fact]
    public async Task Refresh_pass_continues_when_recording_a_failure_throws()
    {
        var bad = AddPlaylist("http://gone.invalid/x.m3u");
        AddPlaylist();
        Serve(PlUrl, Playlist());
        Sql($"CREATE TRIGGER boom BEFORE UPDATE OF last_error ON source WHEN NEW.id={bad} BEGIN SELECT RAISE(ABORT,'boom'); END");
        await _svc.RefreshDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, _channels.CountAll());
    }

    [Fact]
    public async Task Refresh_raises_DataChanged_when_a_step_after_apply_throws()
    {
        var id = AddPlaylist();
        Serve(PlUrl, Playlist());
        Sql($"CREATE TRIGGER boom BEFORE UPDATE OF last_success ON source WHEN NEW.id={id} BEGIN SELECT RAISE(ABORT,'boom'); END");
        var raised = 0;
        _svc.DataChanged += () => raised++;
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal((1, 1), (raised, _channels.CountAll()));
        Assert.NotNull(Get(id).LastError);
    }

    [Fact]
    public async Task Refresh_throwing_DataChanged_handler_does_not_stop_others_or_the_caller()
    {
        var id = AddPlaylist();
        Serve(PlUrl, Playlist());
        var raised = 0;
        _svc.DataChanged += () => throw new InvalidOperationException("handler");
        _svc.DataChanged += () => raised++;
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Refresh_304_counts_as_successful_check()
    {
        var id = AddPlaylist();
        Serve(PlUrl, Playlist(), HttpStatusCode.OK, "\"v1\"");
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        _sources.RecordAttempt(id, T0, "old failure");
        _clock.Now = T0.AddDays(3);
        Serve(PlUrl, "", HttpStatusCode.NotModified);
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        var s = Get(id);
        Assert.Equal((T0.AddDays(3), null, "\"v1\"", 1), (s.LastSuccess, s.LastError, s.ETag, _channels.CountAll()));
    }

    [Fact]
    public async Task Refresh_304_without_loaded_data_is_a_failure()
    {
        var id = AddPlaylist();
        Serve(PlUrl, "", HttpStatusCode.NotModified);
        await _svc.RefreshAsync(id, TestContext.Current.CancellationToken);
        var s = Get(id);
        Assert.Null(s.LastSuccess);
        Assert.NotNull(s.LastError);
    }

    [Fact]
    public async Task Refresh_pass_prunes_even_when_every_source_answers_304()
    {
        var epg = AddEpg();
        _programmes.ImportEpg(epg, new MemoryStream(Encoding.UTF8.GetBytes(Xml(("e1", T0.AddDays(-5), "Old"), ("e1", T0, "Now")))), T0, 7);
        _sources.RecordSuccess(epg, T0.AddDays(-1), "\"v\"", null, 0);
        Serve(EpgUrl, "", HttpStatusCode.NotModified);
        await _svc.RefreshDueAsync(TestContext.Current.CancellationToken);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM programme";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void IsDue_timestamp_in_the_future_is_due()
    {
        Assert.True(RefreshService.IsDue(Due(T0.AddHours(5), T0.AddHours(5)), T0, false));
        Assert.True(RefreshService.IsDue(Due(T0.AddHours(5), T0.AddHours(5)), T0, true));
    }

    // ---- IsDue ----

    private static Source Due(DateTimeOffset? attempt, DateTimeOffset? success, bool auto = true, int minutes = 60) =>
        new(1, SourceKind.Playlist, "x", "x", 0, minutes, auto, null, null, null, attempt, success, null, 0, 0, 0);

    [Fact]
    public void IsDue_startup_when_success_older_than_interval()
    {
        Assert.True(RefreshService.IsDue(Due(T0.AddMinutes(-1), T0.AddMinutes(-61)), T0, true));
        Assert.False(RefreshService.IsDue(Due(T0.AddMinutes(-61), T0.AddMinutes(-10)), T0, true));
    }

    [Fact]
    public void IsDue_running_when_attempt_older_than_interval()
    {
        Assert.False(RefreshService.IsDue(Due(T0.AddMinutes(-10), T0.AddDays(-5)), T0, false));
        Assert.True(RefreshService.IsDue(Due(T0.AddMinutes(-61), T0.AddMinutes(-5)), T0, false));
    }

    [Fact]
    public void IsDue_disabled_auto_refresh_false()
    {
        Assert.False(RefreshService.IsDue(Due(null, null, false), T0, true));
        Assert.False(RefreshService.IsDue(Due(null, null, false), T0, false));
    }

    [Fact]
    public void IsDue_never_refreshed_true()
    {
        Assert.True(RefreshService.IsDue(Due(null, null), T0, true));
        Assert.True(RefreshService.IsDue(Due(null, null), T0, false));
    }
}
