using System.Threading.Channels;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using LibVLCSharp.Platforms.Windows;
using M3uPlayer.Core.Media;

namespace M3uPlayer.App.Player;

public sealed class VlcPlayer : IPlayer, IDisposable
{
    private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    // LibVLC calls such as Stop block, and deadlock when made from its own event threads, so every one runs here.
    private readonly Channel<Action> _jobs = Channel.CreateUnbounded<Action>(new() { SingleReader = true });
    private readonly Timer _watchdog;

    private VideoView? _view;
    private LibVLC? _lib;
    private MediaPlayer? _mp;
    private Media? _media; // worker thread only

    private PlayRequest? _req;
    private ReconnectPolicy _policy = new();
    private CancellationTokenSource _retry = new();
    private int _gen;
    private PlayerState _state = PlayerState.Idle;
    private DateTime? _playingSince;
    private DateTime _lastAdvance;
    private long _lastTime;
    private int _volume = 100;
    private bool _muted, _disposed;

    public VlcPlayer()
    {
        _ = Task.Run(RunWorker);
        _watchdog = new Timer(_ => Tick(), null, 1000, 1000);
    }

    public string? LastError { get; private set; }
    public event Action<PlayerState>? StateChanged;
    public event Action<TimeSpan>? PositionChanged;

    public void Attach(VideoView view)
    {
        _view = view;
        view.Initialized += OnInitialized;
    }

    private void OnInitialized(object? sender, InitializedEventArgs e)
    {
        // Must be created here with the swap chain options, or video opens in its own window.
        var lib = new LibVLC(e.SwapChainOptions);
        var mp = new MediaPlayer(lib);
        mp.Playing += (_, _) => OnPlaying();
        mp.Paused += (_, _) => { lock (_gate) _playingSince = null; SetState(PlayerState.Paused); };
        mp.EndReached += (_, _) => OnEnded();
        mp.EncounteredError += (_, _) => OnError();
        mp.TimeChanged += (_, a) => OnTime(a.Time);
        int gen;
        lock (_gate)
        {
            if (_disposed) { mp.Dispose(); lib.Dispose(); return; }
            _lib = lib;
            _mp = mp;
            mp.Volume = _volume;
            mp.Mute = _muted;
            gen = _req is null ? -1 : _gen;
        }
        _view!.MediaPlayer = mp;
        if (gen >= 0) Enqueue(() => Start(gen)); // a Play made before Initialized: only the latest request is kept
    }

    public void Play(PlayRequest r)
    {
        int gen;
        lock (_gate)
        {
            if (_disposed) return;
            gen = ++_gen;
            _retry.Cancel();
            _req = r;
            _policy = new ReconnectPolicy();
            LastError = null;
            _playingSince = null;
        }
        SetState(PlayerState.Opening);
        Enqueue(() => Start(gen)); // a no-op until Initialized has created the player
    }

    public void Stop()
    {
        lock (_gate)
        {
            _gen++;
            _retry.Cancel();
            _req = null;
            LastError = null;
            _playingSince = null;
        }
        SetState(PlayerState.Idle);
        Enqueue(() => _mp?.Stop());
    }

    public void Pause() => Enqueue(() => _mp?.SetPause(true));

    public void Resume() => Enqueue(() => _mp?.SetPause(false));

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            var v = _volume;
            Enqueue(() => { if (_mp is { } m) m.Volume = v; });
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            Enqueue(() => { if (_mp is { } m) m.Mute = value; });
        }
    }

    public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, _mp?.Time ?? 0));
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(0, _mp?.Length ?? 0));
    public bool IsSeekable => _mp?.IsSeekable ?? false;

    public void Seek(TimeSpan position) => Enqueue(() => { if (_mp is { } m) m.Time = (long)position.TotalMilliseconds; });

    public IReadOnlyList<TrackInfo> AudioTracks => Tracks(_mp?.AudioTrackDescription);
    public IReadOnlyList<TrackInfo> SubtitleTracks => Tracks(_mp?.SpuDescription);

    public void SelectAudio(int id) => Enqueue(() => _mp?.SetAudioTrack(id));
    public void SelectSubtitle(int id) => Enqueue(() => _mp?.SetSpu(id));

    private static IReadOnlyList<TrackInfo> Tracks(TrackDescription[]? d) =>
        d is null ? [] : [.. d.Select(t => new TrackInfo(t.Id, t.Name ?? ""))];

    private void Start(int gen)
    {
        PlayRequest? r;
        lock (_gate) r = gen == _gen ? _req : null; // superseded by a newer Play or Stop
        if (r is null || _mp is null || _lib is null) return;

        _mp.Stop();
        _media?.Dispose();
        _media = new Media(_lib, r.Url, FromType.FromLocation);
        _media.AddOption(":network-caching=1500");
        if (r.UserAgent is not null) _media.AddOption($":http-user-agent={r.UserAgent}");
        if (r.Referrer is not null) _media.AddOption($":http-referrer={r.Referrer}");
        lock (_gate)
        {
            if (gen != _gen) return;
            _lastAdvance = DateTime.UtcNow;
            _lastTime = -1;
        }
        _mp.Play(_media);
    }

    private void OnPlaying()
    {
        lock (_gate)
        {
            if (_state is PlayerState.Idle or PlayerState.Failed or PlayerState.Reconnecting) return; // stale event from a stopped stream
            _playingSince = DateTime.UtcNow;
            _lastAdvance = DateTime.UtcNow;
        }
        SetState(PlayerState.Playing);
    }

    private void OnTime(long ms)
    {
        lock (_gate)
        {
            if (ms == _lastTime) return;
            _lastTime = ms;
            _lastAdvance = DateTime.UtcNow;
        }
        PositionChanged?.Invoke(TimeSpan.FromMilliseconds(ms));
    }

    private void OnEnded()
    {
        bool live;
        lock (_gate) live = _req?.IsLive ?? false;
        if (live) Drop();
        else SetState(PlayerState.Idle, onlyFrom: [PlayerState.Playing, PlayerState.Paused, PlayerState.Opening]);
    }

    private void OnError()
    {
        bool live;
        lock (_gate) live = _req?.IsLive ?? false;
        if (live) Drop();
        else Fail("Nie udało się odtworzyć strumienia.");
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            if (_state is PlayerState.Idle or PlayerState.Failed) return;
            LastError = message;
            _playingSince = null;
        }
        SetState(PlayerState.Failed);
    }

    // Live stream ended, errored or stalled: wait out the next delay, then start over, until the policy gives up.
    private void Drop()
    {
        TimeSpan? delay;
        int gen;
        CancellationToken token;
        lock (_gate)
        {
            if (_state is not (PlayerState.Playing or PlayerState.Opening or PlayerState.Paused)) return; // already handling a drop
            delay = _policy.NextDelay();
            gen = _gen;
            _playingSince = null;
            if (delay is null) LastError = "Nie udało się połączyć z kanałem. Spróbuj ponownie później.";
            else { LastError = null; _retry.Cancel(); _retry = new(); }
            token = _retry.Token;
        }
        if (delay is null)
        {
            SetState(PlayerState.Failed);
            Enqueue(() => _mp?.Stop());
            return;
        }
        SetState(PlayerState.Reconnecting);
        _ = Task.Delay(delay.Value, token).ContinueWith(_ =>
        {
            lock (_gate) { if (gen != _gen || _disposed) return; }
            SetState(PlayerState.Opening);
            Enqueue(() => Start(gen));
        }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void Tick()
    {
        bool stalled;
        lock (_gate)
        {
            if (_state != PlayerState.Playing) return;
            var now = DateTime.UtcNow;
            // Only a connection that held for StableAfter restarts the delays; connect-then-die must keep escalating.
            if (_playingSince is { } since && now - since >= StableAfter) { _policy.MarkStable(); _playingSince = null; }
            stalled = (_req?.IsLive ?? false) && now - _lastAdvance >= StallAfter;
        }
        if (stalled) Drop();
    }

    private bool SetState(PlayerState s, PlayerState[]? onlyFrom = null)
    {
        lock (_gate)
        {
            if (_state == s || (onlyFrom is not null && !onlyFrom.Contains(_state))) return false;
            _state = s;
        }
        StateChanged?.Invoke(s);
        return true;
    }

    private void Enqueue(Action job) => _jobs.Writer.TryWrite(job);

    private async Task RunWorker()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync())
        {
            try { job(); }
            catch (Exception) { Fail("Wystąpił błąd odtwarzacza."); } // the message carries no URL
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _gen++;
            _retry.Cancel();
            _req = null;
        }
        _watchdog.Dispose();
        if (_view is { } v) { v.Initialized -= OnInitialized; v.MediaPlayer = null; }
        Enqueue(() =>
        {
            _mp?.Stop();
            _media?.Dispose();
            _mp?.Dispose();
            _lib?.Dispose();
        });
        _jobs.Writer.TryComplete();
    }
}
