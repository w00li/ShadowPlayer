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
        // A timestamp in the future (clock change) must not stop refreshing.
        return last is null || last > now || now - last >= TimeSpan.FromMinutes(source.IntervalMinutes);
    }

    public Task RefreshAsync(long sourceId, CancellationToken ct) => RefreshLockedAsync(sourceId, null, ct);

    public Task RefreshDueAsync(CancellationToken ct) => RefreshDueAsync(false, ct);

    private async Task RefreshDueAsync(bool atStartup, CancellationToken ct)
    {
        // Playlists first, then EPG sources by position (re-listed, so a url-tvg child just added is included).
        // The gate is taken per source so a manual refresh can get in between sources.
        foreach (var kind in new[] { SourceKind.Playlist, SourceKind.Epg })
            foreach (var id in _sources.List(kind).Select(s => s.Id))
                await RefreshLockedAsync(id, atStartup, ct);
        await _gate.WaitAsync(ct);
        try { _programmes.Prune(clock.GetUtcNow(), Retention()); } // 304-answering sources never reach ImportEpg's own pruning
        catch (Exception ex) when (ex is not OperationCanceledException) { SafeLog("Pruning failed: " + Redactor.Redact(ex.Message)); }
        finally { _gate.Release(); }
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

    /// <param name="dueAtStartup">Null refreshes unconditionally; otherwise only when the source is due.</param>
    private async Task RefreshLockedAsync(long id, bool? dueAtStartup, CancellationToken ct)
    {
        bool changed;
        await _gate.WaitAsync(ct);
        try
        {
            var source = _sources.List(SourceKind.Playlist).Concat(_sources.List(SourceKind.Epg)).FirstOrDefault(s => s.Id == id);
            if (source is null || (dueAtStartup is { } startup && !IsDue(source, clock.GetUtcNow(), startup))) return;
            changed = await RefreshOneAsync(source, ct);
            if (changed)
                try { _programmes.RebuildMatches(); }
                catch (Exception ex) when (ex is not OperationCanceledException) { SafeLog("Rebuilding matches failed: " + Redactor.Redact(ex.Message)); }
        }
        finally { _gate.Release(); }
        if (changed) Raise();
    }

    private void Raise()
    {
        foreach (Action handler in DataChanged?.GetInvocationList() ?? [])
            try { handler(); }
            catch (Exception ex) { SafeLog("DataChanged handler failed: " + Redactor.Redact(ex.Message)); }
    }

    private int Retention() => _channels.MaxCatchupDays() is var d and > 0 ? d : 1;

    /// <returns>True when new data was applied (even if a later bookkeeping step failed).</returns>
    private async Task<bool> RefreshOneAsync(Source source, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        string? temp = null;
        var applied = false;
        try
        {
            var fetched = await fetcher.FetchAsync(source, ct);
            temp = fetched.TempFilePath;
            if (fetched.NotModified)
            {
                if (source.LastSuccess is null) throw new InvalidOperationException("Serwer nie zgłosił zmian, ale dane nie zostały jeszcze wczytane. Spróbujemy ponownie o następnej porze odświeżania.");
                _sources.RecordSuccess(source.Id, now, source.ETag, source.LastModified, source.SkippedCount);
                return false;
            }
            int skipped;
            if (source.Kind == SourceKind.Playlist)
            {
                var doc = ParsePlaylist(source, temp!);
                _channels.Replace(source.Id, doc.Entries);
                applied = true;
                SyncUrlTvg(source, doc.EpgUrl);
                skipped = doc.SkippedCount;
            }
            else
            {
                using var stream = File.OpenRead(temp!);
                skipped = _programmes.ImportEpg(source.Id, stream, now, Retention());
                applied = true;
            }
            _sources.RecordSuccess(source.Id, now, fetched.ETag, fetched.LastModified, skipped);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = Redactor.Redact(ex.Message);
            try { _sources.RecordAttempt(source.Id, now, message); }
            catch (Exception recordError) { SafeLog($"Recording the error of source {source.Id} failed: {Redactor.Redact(recordError.Message)}"); }
            SafeLog($"Refresh of source {source.Id} failed: {message}");
            return applied;
        }
        finally
        {
            if (temp is not null) try { File.Delete(temp); } catch (Exception) { }
        }
    }

    private static M3uDocument ParsePlaylist(Source source, string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        var doc = M3uParser.Parse(reader);
        if (doc.Entries.Count == 0) throw new InvalidDataException("Lista nie zawiera kanałów, więc zostały poprzednie dane.");
        // A truncated or "account expired" list is valid M3U; refuse to let it wipe a large list.
        if (source.ChannelCount >= 10 && doc.Entries.Count * 2 < source.ChannelCount)
            throw new InvalidDataException($"Lista ma teraz {doc.Entries.Count} pozycji, a poprzednio {source.ChannelCount}, więc jej nie zastosowano. " +
                "Usuń listę i dodaj ją ponownie, jeśli ma być mniejsza.");
        return doc;
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
            _sources.Add(SourceKind.Epg, url, Redactor.Redact(playlist.Name) + " (url-tvg)", playlist.Id);
    }

    // A failing log must never mask the original failure or abort a refresh.
    private void SafeLog(string message)
    {
        try { log.Write(message); }
        catch (Exception) { }
    }
}
