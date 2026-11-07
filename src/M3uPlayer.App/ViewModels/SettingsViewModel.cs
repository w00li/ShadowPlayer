using M3uPlayer.Core.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using M3uPlayer.Core.Sources;
using M3uPlayer.Core.Storage;
using M3uPlayer.Core.Text;

namespace M3uPlayer.App.ViewModels;

public sealed record IntervalChoice(int Minutes)
{
    public static readonly IReadOnlyList<IntervalChoice> All = [.. Preferences.IntervalChoices.Select(m => new IntervalChoice(m))];
}

/// <summary>One source for display. Rows are updated in place so a background reload never rebuilds a focused control.</summary>
public sealed class SourceRow : INotifyPropertyChanged
{
    public SourceRow(Source s, int totalChannels, bool isRefreshing, bool showProgress, bool isFirst, bool isLast, string? parentName) =>
        Update(s, totalChannels, isRefreshing, showProgress, isFirst, isLast, parentName);

    public event PropertyChangedEventHandler? PropertyChanged;

    public Source Source { get; private set; } = null!;
    public long Id { get; private set; }
    public string Name { get; private set; } = "";
    public bool IsEpg { get; private set; }
    public bool IsRefreshing { get; private set; }
    public bool ShowProgress { get; private set; }
    public bool CanRefresh { get; private set; }
    public bool CanMoveUp { get; private set; }
    public bool CanMoveDown { get; private set; }
    public bool CanRemove { get; private set; }
    public string AutoAddedText { get; private set; } = "";
    public string Location { get; private set; } = "";
    public int IntervalIndex { get; private set; }
    public bool AutoRefresh { get; private set; }
    public string LastSuccessText { get; private set; } = "";
    public string ResultText { get; private set; } = "";
    public string SkippedText { get; private set; } = "";
    public string ErrorText { get; private set; } = "";
    public bool HasResult { get; private set; }
    public bool HasSkipped { get; private set; }
    public bool HasError { get; private set; }
    public bool HasAutoAdded { get; private set; }
    public string RemoveLabel => string.Format(Strings.Settings_Remove, Name);

    /// <summary>A failure of a local action (e.g. saving the schedule); shown instead of the source's own error until the next success.</summary>
    public string? Notice { get; private set; }

    public void Update(Source s, int totalChannels, bool isRefreshing, bool showProgress, bool isFirst, bool isLast, string? parentName)
    {
        Source = s;
        Id = s.Id;
        Name = Redactor.Redact(s.Name);
        IsEpg = s.Kind == SourceKind.Epg;
        IsRefreshing = isRefreshing;
        ShowProgress = showProgress;
        CanRefresh = !isRefreshing;
        CanMoveUp = !isFirst;
        CanMoveDown = !isLast;
        CanRemove = s.ParentPlaylistId is null;
        AutoAddedText = parentName is null ? "" : string.Format(Strings.Settings_AutoAdded, Redactor.Redact(parentName));
        Location = SourceInput.DisplayLocation(s.Location);
        IntervalIndex = Math.Max(0, IntervalChoice.All.ToList().FindIndex(c => c.Minutes == s.IntervalMinutes));
        AutoRefresh = s.AutoRefresh;
        LastSuccessText = s.LastSuccess is { } t ? string.Format(Strings.Settings_LastRefresh, t.ToLocalTime().ToString("g")) : Strings.Settings_NeverRefreshed;
        // For EPG rows MatchedCount counts matched playlist channels, but ChannelCount counts EPG channels, so the total comes from the playlists.
        ResultText = s.LastSuccess is null ? "" : IsEpg
            ? string.Format(Strings.Settings_Matched, s.MatchedCount, totalChannels)
            : string.Format(Strings.Settings_ChannelCount, s.ChannelCount, s.MatchedCount);
        SkippedText = s.SkippedCount > 0 ? string.Format(Strings.Settings_Skipped, s.SkippedCount) : "";
        HasResult = ResultText.Length > 0;
        HasSkipped = SkippedText.Length > 0;
        HasAutoAdded = AutoAddedText.Length > 0;
        ApplyError();
    }

    public void SetSchedule(int index, bool auto, string? notice)
    {
        IntervalIndex = index;
        AutoRefresh = auto;
        Notice = notice;
        ApplyError();
    }

    private void ApplyError()
    {
        ErrorText = Notice ?? (Source.LastError is { Length: > 0 } e ? string.Format(Strings.Settings_Error, Redactor.Redact(e)) : "");
        HasError = ErrorText.Length > 0;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed class SettingsViewModel
{
    private static readonly TimeSpan ProgressDelay = TimeSpan.FromMilliseconds(400);
    private readonly HashSet<long> _refreshing = [], _progress = [];
    private int _version, _writes;

    public ObservableCollection<SourceRow> Playlists { get; } = [];
    public ObservableCollection<SourceRow> Epgs { get; } = [];

    /// <summary>Call from the UI thread. Only the newest read is applied, and none while a write is in flight (its own reload follows).</summary>
    public async Task ReloadAsync()
    {
        var version = ++_version;
        var (playlists, epgs, total) = await Task.Run(() =>
            (App.Services.Sources.List(SourceKind.Playlist), App.Services.Sources.List(SourceKind.Epg), App.Services.Channels.CountAll()));
        if (version != _version || _writes > 0) return;
        var names = playlists.ToDictionary(p => p.Id, p => p.Name);
        Fill(Playlists, playlists, total, names);
        Fill(Epgs, epgs, total, names);
    }

    private void Fill(ObservableCollection<SourceRow> target, IReadOnlyList<Source> sources, int total, Dictionary<long, string> names)
    {
        var old = target.ToDictionary(r => r.Id);
        var wanted = new List<SourceRow>();
        for (var i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            var parent = s.ParentPlaylistId is { } p && names.TryGetValue(p, out var n) ? n : null;
            var refreshing = _refreshing.Contains(s.Id);
            var progress = _progress.Contains(s.Id);
            if (old.TryGetValue(s.Id, out var row)) row.Update(s, total, refreshing, progress, i == 0, i == sources.Count - 1, parent);
            else row = new SourceRow(s, total, refreshing, progress, i == 0, i == sources.Count - 1, parent);
            wanted.Add(row);
        }
        for (var i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && target[i] == wanted[i]) continue;
            var at = target.IndexOf(wanted[i]);
            if (at >= 0) target.Move(at, i); else target.Insert(i, wanted[i]);
        }
    }

    public IEnumerable<string> ExistingNames => Playlists.Concat(Epgs).Select(r => r.Name);

    public string SuggestName(SourceKind kind, string? filePath = null)
    {
        var prefix = kind == SourceKind.Epg ? "EPG" : Strings.Source_DefaultListName;
        return filePath is null ? SourceInput.NextDefaultName(prefix, ExistingNames) : SourceInput.DefaultNameForFile(filePath, prefix, ExistingNames);
    }

    public async Task AddAsync(SourceKind kind, string location, string name)
    {
        name = SourceInput.SanitizeName(name, SuggestName(kind));
        var id = await Task.Run(() => App.Services.Sources.Add(kind, location, name));
        await ReloadAsync();
        await RefreshAsync(id);
    }

    public async Task RefreshAsync(long id)
    {
        if (!_refreshing.Add(id)) return; // a second click while the first refresh runs
        await ReloadAsync();
        // Quick checks must not flicker a spinner: show it only if the refresh outlasts the delay.
        var reveal = Task.Delay(ProgressDelay).ContinueWith(_ => { }, TaskScheduler.FromCurrentSynchronizationContext());
        var work = Task.Run(() => App.Services.Refresh.RefreshAsync(id, App.Shutdown));
        try
        {
            if (await Task.WhenAny(work, reveal) == reveal && _refreshing.Contains(id)) { _progress.Add(id); await ReloadAsync(); }
            await work;
        }
        catch (OperationCanceledException) when (App.Shutdown.IsCancellationRequested) { }
        finally
        {
            _refreshing.Remove(id);
            _progress.Remove(id);
            await ReloadAsync();
        }
    }

    public async Task RemoveAsync(long id)
    {
        await Task.Run(() =>
        {
            App.Services.Sources.Remove(id);
            App.Services.Programmes.RebuildMatches();
        });
        await ReloadAsync();
        // The freed pages go back to the disk in the background; this can take seconds on a big database.
        Safe.Run(() => App.Services.Refresh.CompactAsync(CancellationToken.None));
    }

    public async Task MoveAsync(SourceRow row, int delta)
    {
        var list = row.IsEpg ? Epgs : Playlists;
        var index = list.IndexOf(row) + delta;
        if (index < 0 || index >= list.Count) return;
        await Task.Run(() =>
        {
            App.Services.Sources.Move(row.Id, index);
            App.Services.Programmes.RebuildMatches();
        });
        await ReloadAsync();
    }

    /// <summary>Shows the new values at once; if saving fails the old ones come back with an inline message.</summary>
    public async Task SetScheduleAsync(SourceRow row, int index, bool auto)
    {
        if (index == row.IntervalIndex && auto == row.AutoRefresh) return;
        var (oldIndex, oldAuto) = (row.IntervalIndex, row.AutoRefresh);
        row.SetSchedule(index, auto, null);
        _writes++;
        try { await Task.Run(() => App.Services.Sources.SetInterval(row.Id, IntervalChoice.All[index].Minutes, auto)); }
        catch (Exception ex)
        {
            App.Services.Log.Write("Saving the schedule failed: " + ex.Message);
            row.SetSchedule(oldIndex, oldAuto, Strings.Settings_SaveScheduleFailed);
            return;
        }
        finally { _writes--; }
        await ReloadAsync();
    }
}
