using System.Collections.ObjectModel;
using M3uPlayer.Core.Sources;
using M3uPlayer.Core.Storage;
using M3uPlayer.Core.Text;

namespace M3uPlayer.App.ViewModels;

public sealed record IntervalChoice(int Minutes, string Label)
{
    public static readonly IReadOnlyList<IntervalChoice> All =
    [
        new(60, "1 godzina"), new(180, "3 godziny"), new(360, "6 godzin"), new(720, "12 godzin"),
        new(1440, "24 godziny"), new(2880, "48 godzin"), new(10080, "7 dni"),
    ];
}

/// <summary>Immutable snapshot of one source for display; the view model rebuilds rows when anything changes.</summary>
public sealed class SourceRow
{
    public SourceRow(Source s, int totalChannels, bool isRefreshing, bool showProgress, bool isFirst, bool isLast, string? parentName)
    {
        Source = s;
        Id = s.Id;
        Name = s.Name;
        IsEpg = s.Kind == SourceKind.Epg;
        IsRefreshing = isRefreshing;
        ShowProgress = showProgress;
        CanRefresh = !isRefreshing;
        CanMoveUp = !isFirst;
        CanMoveDown = !isLast;
        IsAutoAdded = s.ParentPlaylistId is not null;
        CanRemove = !IsAutoAdded;
        AutoAddedText = parentName is null ? "" : $"Dodane automatycznie z listy „{parentName}”. Usuń listę, aby usunąć to źródło.";
        Location = SourceInput.DisplayLocation(s.Location);
        IntervalIndex = Math.Max(0, IntervalChoice.All.ToList().FindIndex(c => c.Minutes == s.IntervalMinutes));
        AutoRefresh = s.AutoRefresh;
        LastSuccessText = s.LastSuccess is { } t ? "Ostatnie odświeżenie: " + t.ToLocalTime().ToString("g") : "Jeszcze nie odświeżono";
        // For EPG rows MatchedCount counts matched playlist channels, but ChannelCount counts EPG channels, so the total comes from the playlists.
        ResultText = s.LastSuccess is null ? "" : IsEpg
            ? $"Dopasowano {s.MatchedCount} z {totalChannels} kanałów"
            : $"{s.ChannelCount} kanałów, z programem TV: {s.MatchedCount}";
        SkippedText = s.SkippedCount > 0 ? $"Pominięto {s.SkippedCount} uszkodzonych pozycji" : "";
        ErrorText = s.LastError is { Length: > 0 } e ? "Błąd: " + Redactor.Redact(e) : "";
        HasResult = ResultText.Length > 0;
        HasSkipped = SkippedText.Length > 0;
        HasError = ErrorText.Length > 0;
        HasAutoAdded = AutoAddedText.Length > 0;
    }

    public Source Source { get; }
    public long Id { get; }
    public string Name { get; }
    public bool IsEpg { get; }
    public bool IsRefreshing { get; }
    public bool ShowProgress { get; }
    public bool CanRefresh { get; }
    public bool CanMoveUp { get; }
    public bool CanMoveDown { get; }
    public bool IsAutoAdded { get; }
    public bool CanRemove { get; }
    public string AutoAddedText { get; }
    public string Location { get; }
    public int IntervalIndex { get; set; } // mutable: updated in place so the focused control is not rebuilt
    public bool AutoRefresh { get; set; }
    public string LastSuccessText { get; }
    public string ResultText { get; }
    public string SkippedText { get; }
    public string ErrorText { get; }
    public bool HasResult { get; }
    public bool HasSkipped { get; }
    public bool HasError { get; }
    public bool HasAutoAdded { get; }
    public string RemoveLabel => "Usuń " + Name;
}

public sealed class SettingsViewModel
{
    private static readonly TimeSpan ProgressDelay = TimeSpan.FromMilliseconds(400);
    private readonly HashSet<long> _refreshing = [], _progress = [];

    public ObservableCollection<SourceRow> Playlists { get; } = [];
    public ObservableCollection<SourceRow> Epgs { get; } = [];

    /// <summary>Call from the UI thread; the database work runs off it, the collections are filled back on it.</summary>
    public async Task ReloadAsync()
    {
        var (playlists, epgs, total) = await Task.Run(() =>
            (App.Services.Sources.List(SourceKind.Playlist), App.Services.Sources.List(SourceKind.Epg), App.Services.Channels.CountAll()));
        var names = playlists.ToDictionary(p => p.Id, p => p.Name);
        Fill(Playlists, playlists, total, names);
        Fill(Epgs, epgs, total, names);
    }

    private void Fill(ObservableCollection<SourceRow> target, IReadOnlyList<Source> sources, int total, Dictionary<long, string> names)
    {
        var rows = sources.Select((s, i) => new SourceRow(s, total, _refreshing.Contains(s.Id), _progress.Contains(s.Id), i == 0, i == sources.Count - 1,
            s.ParentPlaylistId is { } p && names.TryGetValue(p, out var n) ? n : null)).ToList();
        target.Clear();
        foreach (var r in rows) target.Add(r);
    }

    public IEnumerable<string> ExistingNames => Playlists.Concat(Epgs).Select(r => r.Name);

    public string SuggestName(SourceKind kind, string? filePath = null)
    {
        var prefix = kind == SourceKind.Epg ? "EPG" : "Lista";
        return filePath is null ? SourceInput.NextDefaultName(prefix, ExistingNames) : SourceInput.DefaultNameForFile(filePath, prefix, ExistingNames);
    }

    public async Task AddAsync(SourceKind kind, string location, string name)
    {
        var id = await Task.Run(() => App.Services.Sources.Add(kind, location, name));
        await ReloadAsync();
        await RefreshAsync(id);
    }

    public async Task RefreshAsync(long id)
    {
        _refreshing.Add(id);
        await ReloadAsync();
        // Quick checks must not flicker a spinner: show it only if the refresh outlasts the delay.
        var reveal = Task.Delay(ProgressDelay).ContinueWith(_ => { }, TaskScheduler.FromCurrentSynchronizationContext());
        var work = Task.Run(() => App.Services.Refresh.RefreshAsync(id, CancellationToken.None));
        try
        {
            if (await Task.WhenAny(work, reveal) == reveal && _refreshing.Contains(id)) { _progress.Add(id); await ReloadAsync(); }
            await work;
        }
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

    public Task SetScheduleAsync(SourceRow row, int minutes, bool auto) =>
        Task.Run(() => App.Services.Sources.SetInterval(row.Id, minutes, auto));
}
