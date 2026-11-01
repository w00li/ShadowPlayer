using System.Diagnostics;
using System.Net;
using M3uPlayer.Core.Diagnostics;
using M3uPlayer.Core.Epg;
using M3uPlayer.Core.Playlist;
using M3uPlayer.Core.Refresh;
using M3uPlayer.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace M3uPlayer.Core.Tests;

/// <summary>Runs the real refresh path on the git-ignored samples/ files. Prints counts only, never addresses or file names.</summary>
public sealed class SamplesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"m3u-s-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string? SamplesDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "samples")) && File.Exists(Path.Combine(d.FullName, "M3uPlayer.slnx")))
                return Path.Combine(d.FullName, "samples");
        return null;
    }

    [Fact]
    public async Task Real_playlist_and_epg_refresh_end_to_end()
    {
        var samples = SamplesDir();
        var playlist = samples is null ? null : Directory.GetFiles(samples, "*.m3u").SingleOrDefault();
        var epgFile = samples is null ? null : Path.Combine(samples, "epg", "pl.gz");
        if (playlist is null || !File.Exists(epgFile)) { Assert.Skip("samples/ has no playlist or EPG."); return; }

        M3uDocument doc;
        using (var r = new StreamReader(playlist)) doc = M3uParser.Parse(r);
        DateTimeOffset? first = null, last = null;
        using (var fs = File.OpenRead(epgFile))
            XmltvReader.Read(fs, _ => { }, p =>
            {
                if (first is null || p.Start < first) first = p.Start;
                var end = p.Stop ?? p.Start;
                if (last is null || end > last) last = end;
            });
        Assert.NotNull(first);

        Directory.CreateDirectory(_dir);
        var db = new Database(Path.Combine(_dir, "s.db"));
        var sources = new SourceStore(db);
        var clock = new TestClock(first!.Value); // the sample file is dated, so the real clock would expire all of it
        var offline = new HttpClient(new FakeHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("offline"))));
        var svc = new RefreshService(db, new SourceFetcher(offline, [], (_, _) => Task.CompletedTask, Path.Combine(_dir, "tmp")),
            clock, new FileLog(Path.Combine(_dir, "log.txt")));
        var pl = sources.Add(SourceKind.Playlist, playlist, "P");
        var epg = sources.Add(SourceKind.Epg, epgFile, "E");

        var watch = Stopwatch.StartNew();
        await svc.RefreshAsync(pl, TestContext.Current.CancellationToken); // not RefreshDue: the playlist's url-tvg child must not be fetched
        await svc.RefreshAsync(epg, TestContext.Current.CancellationToken);
        watch.Stop();

        var p0 = sources.List(SourceKind.Playlist).Single();
        var e0 = sources.List(SourceKind.Epg).Single(s => s.Id == epg);
        Assert.Null(p0.LastError);
        Assert.Null(e0.LastError);
        Assert.Equal(doc.Entries.Count, new ChannelStore(db).CountAll());
        Assert.Equal(doc.Entries.Count, p0.ChannelCount);
        Assert.True(p0.MatchedCount >= doc.Entries.Count * 0.9, $"matched {p0.MatchedCount} of {doc.Entries.Count}");

        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM programme";
        var programmes = (long)cmd.ExecuteScalar()!;
        Assert.True(programmes > 0);

        var programmeStore = new ProgrammeStore(db);
        var mid = first.Value + (last!.Value - first.Value) / 2;
        var matched = new ChannelStore(db).All().Take(400).Where(ch =>
            programmeStore.GetGuide(ch.Key, first.Value, last.Value).Count > 0).ToList();
        var anyNow = matched.Any(ch => programmeStore.GetNowNext(ch.Key, mid).Now is not null);
        Assert.True(matched.Count > 0 && anyNow);

        TestContext.Current.SendDiagnosticMessage(
            $"SAMPLES entries={doc.Entries.Count} skipped={doc.SkippedCount} matched={p0.MatchedCount}/{p0.ChannelCount} " +
            $"programmes={programmes} skippedProgrammes={e0.SkippedCount} epgChannels={e0.ChannelCount} " +
            $"sampledWithGuide={matched.Count}/{Math.Min(400, doc.Entries.Count)} elapsedMs={watch.ElapsedMilliseconds}");
    }
}
