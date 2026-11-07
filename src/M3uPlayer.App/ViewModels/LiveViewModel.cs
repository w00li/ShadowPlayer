using M3uPlayer.Core.Localization;
using System.Collections.ObjectModel;
using M3uPlayer.App.Player;
using M3uPlayer.Core.Catchup;
using M3uPlayer.Core.Epg;
using M3uPlayer.Core.Media;
using M3uPlayer.Core.Storage;
using M3uPlayer.Core.Text;
using Microsoft.UI.Dispatching;

namespace M3uPlayer.App.ViewModels;

/// <summary>State of the "Na żywo" screen. Touches no controls; everything that reaches the UI goes through the dispatcher queue.</summary>
public sealed partial class LiveViewModel : Observable, IDisposable
{
    public const string ChannelSetting = "live.channel", CategorySetting = "live.category", VolumeSetting = "live.volume",
        PaneCollapsedSetting = "live.categoriesCollapsed", MutedSetting = "live.muted";

    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250), SpinnerDelay = TimeSpan.FromMilliseconds(400),
        VisibleDelay = TimeSpan.FromMilliseconds(120), TickEvery = TimeSpan.FromSeconds(30), VolumeSaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherQueue _dq;
    private readonly IPlayer _player;
    private readonly ChannelStore _channels;
    private readonly ProgrammeStore _programmes;
    private readonly Database _db;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _tz = TimeZoneInfo.Local;
    private readonly DispatcherQueueTimer _spinnerTimer, _visibleTimer, _tickTimer, _volumeTimer;
    private int _reloadVersion;
    private CancellationTokenSource _nowNextCts = new(), _searchCts = new();
    private IReadOnlyList<ChannelRow> _visible = [];
    private PlayerState _state = PlayerState.Idle;
    private bool _spinnerArmed, _disposed;
    private string _searchText = "";
    private bool _categoriesCollapsed;

    public LiveViewModel(DispatcherQueue dq, IPlayer player, ChannelStore channels, ProgrammeStore programmes, Database db, TimeProvider? time = null)
    {
        (_dq, _player, _channels, _programmes, _db, _time) = (dq, player, channels, programmes, db, time ?? TimeProvider.System);
        Guide = new GuideModel(programmes, _time, _tz);
        _spinnerTimer = Timer(SpinnerDelay, false, () => { _spinnerArmed = true; UpdateOverlay(); });
        _visibleTimer = Timer(VisibleDelay, false, () => Safe.Run(RefreshVisibleAsync));
        _tickTimer = Timer(TickEvery, true, () => Safe.Run(OnTickAsync));
        _volumeTimer = Timer(VolumeSaveDelay, false, () => Save(VolumeSetting, _player.Volume.ToString()));
        _player.StateChanged += OnPlayerState;
        _player.PositionChanged += OnPlayerPosition;
    }

    private DispatcherQueueTimer Timer(TimeSpan interval, bool repeat, Action tick)
    {
        var t = _dq.CreateTimer();
        t.Interval = interval;
        t.IsRepeating = repeat;
        t.Tick += (_, _) => { if (!repeat) t.Stop(); tick(); };
        return t;
    }

    public ObservableCollection<CategoryItem> Categories { get; } = [];
    public BulkCollection<ChannelRow> Channels { get; } = new();
    public GuideModel Guide { get; }
    public VideoOverlay Overlay { get; } = new();

    public CategoryItem? SelectedCategory { get; private set; }
    public ChannelRow? SelectedChannel { get; private set; }

    public ArchiveModel Archive { get; } = new();
    public bool IsLiveMode => !Archive.Active;

    public bool ShowLayout { get; private set; }
    public bool ShowEmpty { get; private set; }
    public string ListMessage { get; private set; } = "";
    public bool HasListMessage => ListMessage.Length > 0;
    public bool CanClearSearch { get; private set; }
    public string GuideTitle => SelectedChannel?.Name ?? "";

    // ---- player controls ----
    public double Volume
    {
        get => _player.Volume;
        set
        {
            var v = (int)Math.Round(Math.Clamp(value, 0, 100));
            if (v == _player.Volume) return;
            _player.Volume = v;
            if (v > 0 && _player.Muted) SetMuted(false);
            _volumeTimer.Stop(); // saving waits until the slider rests
            _volumeTimer.Start();
            Raise(nameof(Volume));
        }
    }

    /// <summary>Writes a volume change that is still waiting for the slider to rest (the app is closing or the screen is left).</summary>
    public void FlushVolume()
    {
        if (!_volumeTimer.IsRunning) return;
        _volumeTimer.Stop();
        try { _db.SetSetting(VolumeSetting, _player.Volume.ToString()); }
        catch (Exception ex) { App.Services.Log.Write("Saving the volume failed: " + ex.Message); }
    }

    public bool Muted => _player.Muted;
    public void ToggleMute() => SetMuted(!_player.Muted);

    private void SetMuted(bool on)
    {
        _player.Muted = on;
        Save(MutedSetting, on ? "1" : "0");
        Raise();
    }

    private bool ShowsPlay => _state is PlayerState.Paused or PlayerState.Idle or PlayerState.Failed;
    public string PlayGlyph => ShowsPlay ? "\uE768" : "\uE769";
    public string PlayName => ShowsPlay ? Strings.Live_Play : Strings.Live_Pause;
    public string PlayTip => PlayName + " (Spacja)";
    public string MuteGlyph => Muted ? "\uE74F" : "\uE767";
    public string MuteName => Muted ? Strings.Live_Unmute : Strings.Live_Mute;
    public string MuteTip => MuteName + " (M)";

    public void TogglePause()
    {
        switch (_state)
        {
            case PlayerState.Paused: _player.Resume(); break;
            case PlayerState.Playing or PlayerState.Opening or PlayerState.Reconnecting: _player.Pause(); break;
            default: Retry(); break;
        }
    }

    public bool CategoriesCollapsed
    {
        get => _categoriesCollapsed;
        set
        {
            if (!Set(ref _categoriesCollapsed, value, nameof(CategoriesCollapsed))) return;
            Save(PaneCollapsedSetting, value ? "1" : "0");
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? "", nameof(SearchText))) return;
            _searchCts.Cancel();
            _searchCts.Dispose();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;
            Safe.Run(async () =>
            {
                if (_searchText.Trim().Length > 0) await Task.Delay(SearchDelay, ct);
                await ReloadAsync();
            });
        }
    }

    /// <summary>Raised on the UI thread after the channel list was replaced (the page restores the selection).</summary>
    public event Action? ChannelsReplaced;
    public event Action? SelectedChannelChanged;

    // ---- lifecycle ----
    /// <summary>
    /// The page is shown. Done once the channel list and the selection are current, so the caller may resume playback after it:
    /// a channel that was removed meanwhile must not start again.
    /// </summary>
    public async Task StartAsync()
    {
        _tickTimer.Start();
        var first = !_restored;
        _restored = true;
        if (first)
        {
            await RestoreAsync();
            await ReloadAsync();
            SelectRestoredChannel();
            return;
        }
        // DataChanged is not heard while the page is away: rows, selection and guide all have to be re-read.
        await ReloadAsync();
        await RevalidateSelectionAsync();
        if (SelectedChannel is { } row) Safe.Run(() => Guide.LoadAsync(row.Channel));
    }

    private async Task RevalidateSelectionAsync()
    {
        if (SelectedChannel is not { } selected) return;
        var fresh = await Task.Run(() => _channels.Find(selected.Key));
        if (_disposed) return;
        if (fresh is not null)
        {
            // Not in the shown category: still needs the current address (a token may have rotated).
            var row = Channels.FirstOrDefault(r => r.Key == fresh.Key) ?? new ChannelRow(fresh, false);
            row.Update(fresh, true);
            SelectedChannel = row;
            return;
        }
        _resume = _archiveResumable = false;
        _player.Stop();
        Archive.Clear();
        SelectedChannel = null;
        Guide.Clear();
        foreach (var r in Channels) r.SetPlaying(false);
        Raise();
        UpdateOverlay();
        SelectedChannelChanged?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        DisplayAwake.Set(false);
        _player.StateChanged -= OnPlayerState;
        _player.PositionChanged -= OnPlayerPosition;
        _volumeTimer.Stop();
        _tickTimer.Stop();
        _spinnerTimer.Stop();
        _visibleTimer.Stop();
        _nowNextCts.Cancel();
        _searchCts.Cancel();
    }

    public async Task ReloadAsync()
    {
        var version = ++_reloadVersion;
        var query = _searchText.Trim();
        var category = SelectedCategory?.Key ?? _startCategory;
        var now = _time.GetUtcNow();
        var data = await Task.Run(() =>
        {
            var cats = _channels.Categories();
            var (all, fav) = (_channels.CountAll(), _channels.CountFavorites());
            var nameMatches = new HashSet<string>();
            IReadOnlyList<Channel> list;
            if (query.Length > 0)
            {
                nameMatches = _channels.Search(query).Select(c => c.Key).ToHashSet();
                var byProgramme = _programmes.SearchProgrammeChannels(query, now).ToHashSet();
                list = _channels.All().Where(c => nameMatches.Contains(c.Key) || byProgramme.Contains(c.Key)).ToList();
            }
            else list = category switch
            {
                CategoryItem.AllKey => _channels.All(),
                CategoryItem.FavoritesKey => _channels.Favorites(),
                _ => _channels.ByCategory(category[2..]),
            };
            return (cats, all, fav, list, nameMatches);
        });
        if (version != _reloadVersion || _disposed) return;

        ApplyCategories(data.cats, data.all, data.fav);
        var searching = query.Length > 0;
        bool ByProgrammeOnly(string key) => searching && !data.nameMatches.Contains(key);
        var playingKey = SelectedChannel?.Key;
        var same = data.list.Count == Channels.Count
            && data.list.Select((c, i) => Channels[i].Key == c.Key && Channels[i].ProgrammeMatch == ByProgrammeOnly(c.Key)).All(x => x);
        if (same)
            for (var i = 0; i < data.list.Count; i++) Channels[i].Update(data.list[i], data.list[i].Key == playingKey);
        else
        {
            var old = Channels.ToDictionary(r => r.Key);
            var rows = data.list.Select(c =>
            {
                var row = new ChannelRow(c, ByProgrammeOnly(c.Key));
                row.Update(c, c.Key == playingKey);
                if (old.TryGetValue(c.Key, out var o)) row.CopyEpgFrom(o); // no blank flash until the visible batch answers
                return row;
            }).ToList();
            Channels.ReplaceAll(rows);
            if (playingKey is not null && rows.FirstOrDefault(r => r.Key == playingKey) is { } current) SelectedChannel = current;
            ChannelsReplaced?.Invoke();
        }

        (ShowLayout, ShowEmpty) = (data.all > 0, data.all == 0);
        CanClearSearch = searching;
        ListMessage = data.list.Count > 0 || data.all == 0 ? ""
            : searching ? string.Format(Strings.Live_NoResults, query)
            : category == CategoryItem.FavoritesKey ? Strings.Live_NoFavorites
            : Strings.Live_EmptyCategory;
        Raise();
        ScheduleVisible();
    }

    private void ApplyCategories(IReadOnlyList<CategoryCount> cats, int all, int fav)
    {
        var wanted = new List<(string Key, string Name, int Count)> { (CategoryItem.AllKey, Strings.Live_All, all), (CategoryItem.FavoritesKey, Strings.Live_Favorites, fav) };
        wanted.AddRange(cats.Select(c => ("c:" + c.Name, c.Name, c.Count)));
        if (Categories.Select(c => c.Key).SequenceEqual(wanted.Select(w => w.Key)))
            for (var i = 0; i < wanted.Count; i++) Categories[i].SetCount(wanted[i].Count);
        else
        {
            Categories.Clear();
            foreach (var w in wanted) Categories.Add(new CategoryItem(w.Key, w.Name, w.Count));
        }
        var selected = Categories.FirstOrDefault(c => c.Key == (SelectedCategory?.Key ?? _startCategory)) ?? Categories[0];
        if (!ReferenceEquals(selected, SelectedCategory)) { SelectedCategory = selected; Raise(nameof(SelectedCategory)); }
    }

    public void SelectCategory(CategoryItem category)
    {
        if (SelectedCategory?.Key == category.Key) return;
        SelectedCategory = category;
        Raise(nameof(SelectedCategory));
        Save(CategorySetting, category.Key);
        CancelBatch();
        Safe.Run(ReloadAsync);
    }

    public void ClearSearch() => SearchText = "";

    // ---- restoring the last session ----
    private bool _restored;
    private string _startCategory = SessionRestore.AllKey;
    private string? _restoreChannel;

    // Volume and mute go to the player now, so the first play already has them. Nothing starts playing.
    private async Task RestoreAsync()
    {
        var saved = await Task.Run(() =>
        {
            var key = _db.GetSetting(ChannelSetting);
            return (Category: _db.GetSetting(CategorySetting), Channel: key is null ? null : _channels.Find(key),
                Known: _channels.Categories().Select(c => "c:" + c.Name).ToHashSet(), Volume: _db.GetSetting(VolumeSetting),
                Muted: _db.GetSetting(MutedSetting), Collapsed: _db.GetSetting(PaneCollapsedSetting));
        });
        if (_disposed) return;
        _startCategory = SessionRestore.StartCategory(saved.Category, saved.Known, saved.Channel); // a category or channel that is gone means "Wszystkie"
        _restoreChannel = saved.Channel?.Key;
        if (int.TryParse(saved.Volume, out var v)) _player.Volume = v;
        _player.Muted = saved.Muted == "1";
        if (saved.Collapsed == "1") { _categoriesCollapsed = true; Raise(nameof(CategoriesCollapsed)); }
        Raise();
    }

    private void SelectRestoredChannel()
    {
        var key = _restoreChannel;
        _restoreChannel = null;
        if (key is null || SelectedChannel is not null || Channels.FirstOrDefault(r => r.Key == key) is not { } row) return;
        SelectedChannel = row;
        Raise();
        UpdateOverlay();
        SelectedChannelChanged?.Invoke();
        Safe.Run(() => Guide.LoadAsync(row.Channel));
    }

    // ---- playing ----
    private bool IsActive => _state is PlayerState.Opening or PlayerState.Playing or PlayerState.Reconnecting or PlayerState.Paused;

    public void Play(ChannelRow row)
    {
        if (SelectedChannel?.Key == row.Key && IsActive && IsLiveMode) return; // a click on what already plays must not restart it
        _archiveResumable = false;
        Archive.Clear(); // choosing a channel always means its live stream
        SelectedChannel = row;
        StartPlayback(row);
        Save(ChannelSetting, row.Key);
        Raise();
        UpdateOverlay();
        SelectedChannelChanged?.Invoke();
        Safe.Run(() => Guide.LoadAsync(row.Channel));
    }

    private void StartPlayback(ChannelRow row)
    {
        var c = row.Channel;
        _spinnerArmed = false;
        _player.Play(new PlayRequest(c.Url, c.UserAgent, c.Referrer, true));
        foreach (var r in Channels) r.SetPlaying(r.Key == row.Key);
    }

    /// <summary>After a failure, or after the screen stopped playback: start the selected channel again. In archive: continue it, or go live when it ended.</summary>
    public void Retry()
    {
        if (SelectedChannel is null) return;
        if (Archive.Active && (_state != PlayerState.Idle || _archiveResumable)) OpenArchive(Archive.Elapsed);
        else GoLive();
    }

    public void SelectNextChannel() => Step(1);
    public void SelectPreviousChannel() => Step(-1);

    private void Step(int delta)
    {
        var at = SelectedChannel is null ? -1 : Channels.ToList().FindIndex(r => r.Key == SelectedChannel.Key);
        var next = ChannelStep.Index(Channels.Count, at, delta);
        if (next >= 0) Play(Channels[next]);
    }

    public void ToggleFavorite(string key)
    {
        var row = Channels.FirstOrDefault(r => r.Key == key);
        if (row is null) return;
        var on = !row.IsFavorite;
        row.SetFavorite(on);
        if (!on && SelectedCategory?.Key == CategoryItem.FavoritesKey && SearchText.Trim().Length == 0)
            Channels.Remove(row); // keep the other rows and the scroll position; the reload below then sees the same keys
        Safe.Run(async () =>
        {
            await Task.Run(() => _channels.SetFavorite(key, on));
            await ReloadAsync();
        });
    }

    private void OnPlayerState(PlayerState s) => _dq.TryEnqueue(() => { if (!_disposed) ApplyState(s); });

    private void ApplyState(PlayerState s)
    {
        _state = s;
        DisplayAwake.Set(s == PlayerState.Playing);
        _spinnerTimer.Stop();
        if (s == PlayerState.Opening) { if (!_spinnerArmed) _spinnerTimer.Start(); }
        else _spinnerArmed = false;
        // Stopping for the screen sets _ignorePositions first, and a failed open never reaches Idle: this is the real end of a programme.
        if (s == PlayerState.Idle && Archive.Active && !_archiveResumable && !_ignorePositions) { PlayNextProgramme(); return; }
        UpdateOverlay();
        Raise(); // the play/pause button
    }

    private void UpdateOverlay() => Overlay.Update(_state, _spinnerArmed, _player.LastError, SelectedChannel?.Name, Archive.Active, _archiveResumable);

    // ---- EPG on visible rows ----
    public void ReportVisible(IEnumerable<ChannelRow> rows)
    {
        _visible = rows.ToList();
        ScheduleVisible();
    }

    private void CancelBatch()
    {
        _nowNextCts.Cancel();
        _nowNextCts.Dispose();
        _nowNextCts = new CancellationTokenSource();
    }

    private void ScheduleVisible()
    {
        CancelBatch(); // scrolling or a new filter makes the running batch obsolete
        _visibleTimer.Stop();
        _visibleTimer.Start();
    }

    private async Task RefreshVisibleAsync()
    {
        CancelBatch();
        var ct = _nowNextCts.Token;
        var rows = _visible.ToList();
        if (rows.Count == 0) return;
        var now = _time.GetUtcNow();
        var query = TextNormalizer.ForSearch(_searchText).Trim();
        var keys = rows.Select(r => r.Key).ToList();
        var matchKeys = rows.Where(r => r.ProgrammeMatch).Select(r => r.Key).ToList();
        var (nn, matches) = await Task.Run(() =>
        {
            var batch = _programmes.GetNowNext(keys, now);
            var found = new Dictionary<string, string>();
            if (query.Length > 0)
                foreach (var k in matchKeys)
                {
                    if (ct.IsCancellationRequested) break;
                    var p = _programmes.GetGuide(k, now, now.AddDays(14)).FirstOrDefault(p => TextNormalizer.ForSearch(p.Title).Contains(query));
                    if (p is not null) found[k] = LiveText.SearchMatch(p, now, _tz);
                }
            return (batch, found);
        });
        if (ct.IsCancellationRequested || _disposed) return;
        foreach (var r in rows) r.SetNowNext(nn[r.Key], now, _tz, matches.GetValueOrDefault(r.Key));
    }

    private async Task OnTickAsync()
    {
        var now = _time.GetUtcNow();
        Guide.SetTime(now);
        if (Guide.DayChanged(now) && SelectedChannel is { } row) await Guide.LoadAsync(row.Channel, keepSelection: true); // "Dziś" must not stay on yesterday
        await RefreshVisibleAsync();
    }

    /// <summary>New data arrived (a refresh finished): everything on screen is re-read.</summary>
    public async Task OnDataChangedAsync()
    {
        await ReloadAsync();
        if (SelectedChannel is { } row) await Guide.LoadAsync(row.Channel);
    }

    // ---- plumbing ----
    // Writes run one after another, so two quick changes of the same setting cannot land in the wrong order.
    private Task _saves = Task.CompletedTask;

    private void Save(string key, string value)
    {
        var previous = _saves;
        _saves = SaveAfterAsync(previous, key, value);
    }

    private async Task SaveAfterAsync(Task previous, string key, string value)
    {
        await previous;
        try { await Task.Run(() => _db.SetSetting(key, value)); }
        catch (Exception ex) { App.Services.Log.Write("Saving a setting failed: " + ex.Message); }
    }
}
