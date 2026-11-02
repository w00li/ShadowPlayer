using System.Collections.ObjectModel;
using M3uPlayer.App.Player;
using M3uPlayer.Core.Epg;
using M3uPlayer.Core.Media;
using M3uPlayer.Core.Storage;
using M3uPlayer.Core.Text;
using Microsoft.UI.Dispatching;

namespace M3uPlayer.App.ViewModels;

/// <summary>State of the "Na żywo" screen. Touches no controls; everything that reaches the UI goes through the dispatcher queue.</summary>
public sealed class LiveViewModel : Observable, IDisposable
{
    public const string ChannelSetting = "live.channel", CategorySetting = "live.category", VolumeSetting = "live.volume",
        PaneCollapsedSetting = "live.categoriesCollapsed";

    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250), SpinnerDelay = TimeSpan.FromMilliseconds(400),
        VisibleDelay = TimeSpan.FromMilliseconds(120), TickEvery = TimeSpan.FromSeconds(30);

    private readonly DispatcherQueue _dq;
    private readonly IPlayer _player;
    private readonly ChannelStore _channels;
    private readonly ProgrammeStore _programmes;
    private readonly Database _db;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _tz = TimeZoneInfo.Local;
    private readonly DispatcherQueueTimer _spinnerTimer, _visibleTimer, _tickTimer;
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
        _player.StateChanged += OnPlayerState;
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

    /// <summary>The archive task adds the other mode; for now the screen only plays live.</summary>
    public bool IsLiveMode => true;

    public bool ShowLayout { get; private set; }
    public bool ShowEmpty { get; private set; }
    public string ListMessage { get; private set; } = "";
    public bool HasListMessage => ListMessage.Length > 0;
    public bool CanClearSearch { get; private set; }
    public string GuideTitle => SelectedChannel?.Name ?? "";

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
    public void Start()
    {
        _tickTimer.Start();
        Safe.Run(ReloadAsync);
    }

    public void Resume() => _tickTimer.Start();

    public void Dispose()
    {
        _disposed = true;
        _player.StateChanged -= OnPlayerState;
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
        var category = SelectedCategory?.Key ?? CategoryItem.AllKey;
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
            : searching ? $"Brak wyników dla „{query}”"
            : category == CategoryItem.FavoritesKey ? "Nie masz jeszcze ulubionych. Najedź na kanał i kliknij gwiazdkę, a pojawi się tutaj."
            : "W tej kategorii nie ma kanałów.";
        Raise();
        ScheduleVisible();
    }

    private void ApplyCategories(IReadOnlyList<CategoryCount> cats, int all, int fav)
    {
        var wanted = new List<(string Key, string Name, int Count)> { (CategoryItem.AllKey, "Wszystkie", all), (CategoryItem.FavoritesKey, "Ulubione", fav) };
        wanted.AddRange(cats.Select(c => ("c:" + c.Name, c.Name, c.Count)));
        if (Categories.Select(c => c.Key).SequenceEqual(wanted.Select(w => w.Key)))
            for (var i = 0; i < wanted.Count; i++) Categories[i].SetCount(wanted[i].Count);
        else
        {
            Categories.Clear();
            foreach (var w in wanted) Categories.Add(new CategoryItem(w.Key, w.Name, w.Count));
        }
        var selected = Categories.FirstOrDefault(c => c.Key == (SelectedCategory?.Key ?? CategoryItem.AllKey)) ?? Categories[0];
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

    // ---- playing ----
    private bool IsActive => _state is PlayerState.Opening or PlayerState.Playing or PlayerState.Reconnecting or PlayerState.Paused;

    public void Play(ChannelRow row)
    {
        if (SelectedChannel?.Key == row.Key && IsActive) return; // a click on what already plays must not restart it
        SelectedChannel = row;
        StartPlayback(row);
        foreach (var r in Channels) r.SetPlaying(r.Key == row.Key);
        Save(ChannelSetting, row.Key);
        Save(VolumeSetting, _player.Volume.ToString());
        Raise();
        UpdateOverlay();
        SelectedChannelChanged?.Invoke();
        Safe.Run(() => Guide.LoadAsync(row.Key));
    }

    private void StartPlayback(ChannelRow row)
    {
        var c = row.Channel;
        _spinnerArmed = false;
        _player.Play(new PlayRequest(c.Url, c.UserAgent, c.Referrer, true));
    }

    /// <summary>After a failure, or after the screen stopped playback: start the selected channel again.</summary>
    public void Retry()
    {
        if (SelectedChannel is { } row) StartPlayback(row);
    }

    public void SelectNextChannel() => Step(1);
    public void SelectPreviousChannel() => Step(-1);

    private void Step(int delta)
    {
        var at = SelectedChannel is null ? -1 : Channels.ToList().FindIndex(r => r.Key == SelectedChannel.Key);
        var next = ChannelStep.Index(Channels.Count, at, delta);
        if (next >= 0) Play(Channels[next]);
    }

    private bool _resume;

    /// <summary>The page really leaves (navigation): the video surface goes away, so playback stops; it resumes when the page comes back.</summary>
    public void LeaveScreen()
    {
        _tickTimer.Stop();
        _resume = IsActive && _state != PlayerState.Paused;
        _player.Stop();
    }

    /// <summary>The page is shown again; the "no autoplay" rule is only for app start.</summary>
    public void ReturnToScreen()
    {
        if (!_resume) return;
        _resume = false;
        Retry();
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
        _spinnerTimer.Stop();
        if (s == PlayerState.Opening) { if (!_spinnerArmed) _spinnerTimer.Start(); }
        else _spinnerArmed = false;
        UpdateOverlay();
    }

    private void UpdateOverlay() => Overlay.Update(_state, _spinnerArmed, _player.LastError, SelectedChannel?.Name);

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
        Guide.SetTime(_time.GetUtcNow());
        await RefreshVisibleAsync();
    }

    /// <summary>New data arrived (a refresh finished): everything on screen is re-read.</summary>
    public async Task OnDataChangedAsync()
    {
        await ReloadAsync();
        if (SelectedChannel is { } row) await Guide.LoadAsync(row.Key);
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
