using System.Text;
using M3uPlayer.Core.Diagnostics;
using M3uPlayer.Core.Playlist;
using M3uPlayer.Core.Storage;
using M3uPlayer.Core.Text;

namespace M3uPlayer.Core.Refresh;

/// <summary>
/// Refreshes sources into the stores. A failed fetch, parse or import records a redacted error and leaves the old data;
/// ETag/LastModified are saved only after the data is applied, so a later 304 can never skip a failed import.
/// </summary>
public class RefreshService(Database db, SourceFetcher fetcher, TimeProvider clock, FileLog log)
{
    private readonly SourceStore _sources = new(db);
    private readonly ChannelStore _channels = new(db);
    private readonly ProgrammeStore _programmes = new(db);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action? DataChanged;

    public static bool IsDue(Source source, DateTimeOffset now, bool atStartup)
    {
        if (!source.AutoRefresh) return false;
        var last = atStartup ? source.LastSuccess : source.LastAttempt;
        return last is null || now - last >= TimeSpan.FromMinutes(source.IntervalMinutes);
    }

    public async Task RefreshAsync(long sourceId, CancellationToken ct)
    {
        var changed = false;
        await _gate.WaitAsync(ct);
        try
        {
            var source = _sources.List(SourceKind.Playlist).Concat(_sources.List(SourceKind.Epg)).FirstOrDefault(s => s.Id == sourceId);
            if (source is not null && (changed = await RefreshOneAsync(source, ct))) _programmes.RebuildMatches();
        }
        finally { _gate.Release(); }
        if (changed) DataChanged?.Invoke();
    }

    public async Task RefreshDueAsync(CancellationToken ct) => await RefreshDueAsync(false, ct);

    private async Task RefreshDueAsync(bool atStartup, CancellationToken ct)
    {
        var changed = 0;
        await _gate.WaitAsync(ct);
        try
        {
            // Playlists first, then EPG sources by position (re-listed, so a url-tvg child just added is included).
            foreach (var kind in new[] { SourceKind.Playlist, SourceKind.Epg })
                foreach (var s in _sources.List(kind))
                    if (IsDue(s, clock.GetUtcNow(), atStartup) && await RefreshOneAsync(s, ct)) changed++;
            if (changed > 0) _programmes.RebuildMatches();
        }
        finally { _gate.Release(); }
        for (var i = 0; i < changed; i++) DataChanged?.Invoke();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // A crash between Replace and RebuildMatches leaves channels without matches; this heals it.
        await Guarded(async () =>
        {
            await _gate.WaitAsync(ct);
            try { _programmes.RebuildMatches(); }
            finally { _gate.Release(); }
        }, ct);
        await Guarded(() => RefreshDueAsync(true, ct), ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(ct)) await Guarded(() => RefreshDueAsync(false, ct), ct);
    }

    private async Task Guarded(Func<Task> work, CancellationToken ct)
    {
        try { await work(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { SafeLog("Refresh loop failed: " + Redactor.Redact(ex.Message)); }
    }

    /// <returns>True when new data was applied.</returns>
    private async Task<bool> RefreshOneAsync(Source source, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        string? temp = null;
        try
        {
            var fetched = await fetcher.FetchAsync(source, ct);
            temp = fetched.TempFilePath;
            if (fetched.NotModified)
            {
                _sources.RecordAttempt(source.Id, now, null);
                return false;
            }
            var skipped = source.Kind == SourceKind.Playlist ? ImportPlaylist(source, temp!) : ImportEpg(source, temp!, now);
            _sources.RecordSuccess(source.Id, now, fetched.ETag, fetched.LastModified, skipped);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = Redactor.Redact(ex.Message);
            _sources.RecordAttempt(source.Id, now, message);
            SafeLog($"Refresh of source {source.Id} failed: {message}");
            return false;
        }
        finally
        {
            if (temp is not null) try { File.Delete(temp); } catch (IOException) { }
        }
    }

    private int ImportPlaylist(Source source, string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        var doc = M3uParser.Parse(reader);
        if (doc.Entries.Count == 0) throw new InvalidDataException("The playlist contains no channels.");
        _channels.Replace(source.Id, doc.Entries);
        SyncUrlTvg(source, doc.EpgUrl);
        return doc.SkippedCount;
    }

    private int ImportEpg(Source source, string path, DateTimeOffset now)
    {
        var days = _channels.MaxCatchupDays();
        using var stream = File.OpenRead(path);
        return _programmes.ImportEpg(source.Id, stream, now, days == 0 ? 1 : days);
    }

    // url-tvg may list several addresses; only the first is followed.
    private void SyncUrlTvg(Source playlist, string? header)
    {
        var url = header?.Split(',')[0].Trim();
        if (url is not { Length: > 0 } || !SourceFetcher.IsHttp(url)) return;
        var epgs = _sources.List(SourceKind.Epg);
        if (epgs.FirstOrDefault(e => e.ParentPlaylistId == playlist.Id) is { } child)
        {
            if (child.Location != url) _sources.SetLocation(child.Id, url);
        }
        else if (epgs.All(e => e.Location != url))
            _sources.Add(SourceKind.Epg, url, playlist.Name + " (url-tvg)", playlist.Id);
    }

    // A failing log must never mask the original failure or abort a refresh.
    private void SafeLog(string message)
    {
        try { log.Write(message); }
        catch (Exception) { }
    }
}
