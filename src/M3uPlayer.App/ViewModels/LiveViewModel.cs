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
    private int _reloadVersion, _guideVersion;
    private CancellationTokenSource _nowNextCts = new(), _searchCts = new();
    private IReadOnlyList<ChannelRow> _visible = [];
    private PlayerState _state = PlayerState.Idle;
    private bool _spinnerArmed, _disposed;
    private string _searchText = "";
    private bool _categoriesCollapsed;

    public LiveViewModel(DispatcherQueue dq, IPlayer player, ChannelStore channels, ProgrammeStore programmes, Database db, TimeProvider? time = null)
    {
        (_dq, _player, _channels, _programmes, _db, _time) = (dq, player, channels, programmes, db, time ?? TimeProvider.System);
        _spinnerTimer = Timer(SpinnerDelay, false, () => { _spinnerArmed = true; UpdateOverlay(); });
        _visibleTimer = Timer(VisibleDelay, false, () => Run(RefreshVisibleAsync));
        _tickTimer = Timer(TickEvery, true, () => Run(OnTickAsync));
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
    public BulkCollection<GuideItem> Guide { get; } = new();

    public CategoryItem? SelectedCategory { get; private set; }
    public ChannelRow? SelectedChannel { get; private set; }
    public GuideItem? SelectedProgramme { get; private set; }

    /// <summary>The archive task adds the other mode; for now the screen only plays live.</summary>
    public bool IsLiveMode => true;

    public bool ShowLayout { get; private set; }
    public bool ShowEmpty { get; private set; }
    public string ListMessage { get; private set; } = "";
    public bool HasListMessage => ListMessage.Length > 0;
    public bool CanClearSearch { get; private set; }
    public string GuideTitle => SelectedChannel?.Name ?? "";
    public string GuideMessage { get; private set; } = "Wybierz kanał, aby zobaczyć ramówkę.";
    public bool HasGuideMessage => GuideMessage.Length > 0;

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
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;
            Run(async () =>
            {
                if (_searchText.Trim().Length > 0) await Task.Delay(SearchDelay, ct);
                await ReloadAsync();
            });
        }
    }

    // ---- video overlay ----
    public bool OverlayVisible { get; private set; } = true;
    public string OverlayText { get; private set; } = "Wybierz kanał z listy";
    /// <summary>A stale frame of the previous channel stays visible while a new one opens; text needs a solid backdrop.</summary>
    public double OverlayDim => _state == PlayerState.Opening ? 0.45 : 1;
    public bool ShowSpinner { get; private set; }
    public bool ShowButton { get; private set; }
    public string ButtonText { get; private set; } = "";

    // ---- programme details ----
    public bool DetailVisible { get; private set; }
    public string DetailTitle { get; private set; } = "";
    public string DetailMeta { get; private set; } = "";
    public string DetailDescription { get; private set; } = "";
    public string? DetailIconUrl { get; private set; }

    /// <summary>Raised on the UI thread after the channel list was replaced (the page restores the selection).</summary>
    public event Action? ChannelsReplaced;
    public event Action? SelectedChannelChanged;
    /// <summary>The guide was loaded; the argument is the current programme to scroll to and select.</summary>
    public event Action<GuideItem?>? GuideLoaded;

    // ---- lifecycle ----
    public void Start()
    {
        _tickTimer.Start();
        Run(ReloadAsync);
    }

    public void Pause() => _tickTimer.Stop();

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

    /// <summary>Called when the page leaves the screen: its video surface goes away, so playback stops and waits for the user.</summary>
    public void StopPlayback() => _player.Stop();

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
        _nowNextCts.Cancel();
        Run(ReloadAsync);
    }

    public void ClearSearch() => SearchText = "";

    // ---- playing ----
    public void Play(ChannelRow row)
    {
        var active = _state is PlayerState.Opening or PlayerState.Playing or PlayerState.Reconnecting or PlayerState.Paused;
        if (SelectedChannel?.Key == row.Key && active) return; // a click on what already plays must not restart it
        SelectedChannel = row;
        StartPlayback(row);
        foreach (var r in Channels) r.SetPlaying(r.Key == row.Key);
        Save(ChannelSetting, row.Key);
        Save(VolumeSetting, _player.Volume.ToString());
        Raise();
        SelectedChannelChanged?.Invoke();
        Run(() => LoadGuideAsync(row));
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
        if (Channels.Count == 0) return;
        var at = SelectedChannel is null ? -1 : Channels.ToList().FindIndex(r => r.Key == SelectedChannel.Key);
        var next = at < 0 ? (delta > 0 ? 0 : Channels.Count - 1) : at + delta;
        if (next >= 0 && next < Channels.Count) Play(Channels[next]);
    }

    public void ToggleFavorite(string key)
    {
        var row = Channels.FirstOrDefault(r => r.Key == key);
        if (row is null) return;
        var on = !row.IsFavorite;
        row.SetFavorite(on);
        if (!on && SelectedCategory?.Key == CategoryItem.FavoritesKey && SearchText.Trim().Length == 0)
            Channels.Remove(row); // keep the other rows and the scroll position; the reload below then sees the same keys
        Run(async () =>
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

    private void UpdateOverlay()
    {
        var channel = SelectedChannel;
        (OverlayVisible, OverlayText, ShowSpinner, ShowButton, ButtonText) = _state switch
        {
            PlayerState.Opening => (_spinnerArmed, "", true, false, ""),
            PlayerState.Reconnecting => (true, "Łączenie ponownie…", true, false, ""),
            PlayerState.Failed => (true, _player.LastError ?? "Nie udało się odtworzyć kanału.", false, true, "Spróbuj ponownie"),
            PlayerState.Idle when channel is not null => (true, channel.Name, false, true, "Oglądaj"),
            PlayerState.Idle => (true, "Wybierz kanał z listy", false, false, ""),
            _ => (false, "", false, false, ""),
        };
        Raise();
    }

    // ---- EPG on visible rows ----
    public void ReportVisible(IEnumerable<ChannelRow> rows)
    {
        _visible = rows.ToList();
        ScheduleVisible();
    }

    private void ScheduleVisible()
    {
        _nowNextCts.Cancel(); // scrolling or a new filter makes the running batch obsolete
        _visibleTimer.Stop();
        _visibleTimer.Start();
    }

    private async Task RefreshVisibleAsync()
    {
        _nowNextCts.Cancel();
        var cts = _nowNextCts = new CancellationTokenSource();
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
                    if (cts.IsCancellationRequested) break;
                    var p = _programmes.GetGuide(k, now, now.AddDays(14)).FirstOrDefault(p => TextNormalizer.ForSearch(p.Title).Contains(query));
                    if (p is null) continue;
                    var day = GuideFormat.DayLabel(p.Start, now, _tz);
                    found[k] = $"Znaleziono: {(day == GuideFormat.DayLabel(now, now, _tz) ? "" : TimeZoneInfo.ConvertTime(p.Start, _tz).ToString("d.MM") + " ")}{GuideFormat.Time(p.Start, _tz)} {p.Title}";
                }
            return (batch, found);
        });
        if (cts.IsCancellationRequested || _disposed) return;
        foreach (var r in rows) r.SetNowNext(nn[r.Key], now, _tz, matches.GetValueOrDefault(r.Key));
    }

    private async Task OnTickAsync()
    {
        var now = _time.GetUtcNow();
        foreach (var g in Guide) g.SetTime(now);
        await RefreshVisibleAsync();
    }

    /// <summary>New data arrived (a refresh finished): everything on screen is re-read.</summary>
    public async Task OnDataChangedAsync()
    {
        await ReloadAsync();
        if (SelectedChannel is { } row) await LoadGuideAsync(row);
    }

    // ---- guide ----
    private async Task LoadGuideAsync(ChannelRow row)
    {
        var version = ++_guideVersion;
        var now = _time.GetUtcNow();
        var days = Math.Clamp(row.Channel.Catchup.Days, 0, 30);
        var programmes = await Task.Run(() => _programmes.GetGuide(row.Key, now.AddDays(-days), now.AddDays(30)));
        if (version != _guideVersion || _disposed) return;
        var items = GuideFormat.Rows(programmes, now, _tz).Select(r => new GuideItem(r, _tz)).ToList();
        foreach (var i in items) i.SetTime(now);
        var current = items.FirstOrDefault(i => i.IsCurrent);
        Guide.ReplaceAll(items);
        GuideMessage = items.Count == 0 ? "Brak ramówki dla tego kanału." : "";
        SelectProgramme(current);
        GuideLoaded?.Invoke(current);
    }

    public void SelectProgramme(GuideItem? item)
    {
        if (item is { IsHeader: true }) return;
        SelectedProgramme = item;
        var p = item?.Programme;
        DetailVisible = p is not null;
        DetailTitle = p?.Title ?? "";
        DetailMeta = p is null ? "" : string.Join(" · ", new[]
        {
            $"{GuideFormat.DayLabel(p.Start, _time.GetUtcNow(), _tz)}, {GuideFormat.Time(p.Start, _tz)}–{GuideFormat.Time(p.Stop, _tz)}", p.Category,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        DetailDescription = p is null ? "" : string.Join("\n", new[] { p.SubTitle, p.Description }.Where(s => !string.IsNullOrWhiteSpace(s)));
        DetailIconUrl = p?.IconUrl;
        Raise();
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

    private async void Run(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await App.ReportAsync(ex); }
    }
}
