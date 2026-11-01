using System.Threading.Channels;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using LibVLCSharp.Platforms.Windows;
using M3uPlayer.Core.Diagnostics;
using M3uPlayer.Core.Media;

namespace M3uPlayer.App.Player;

/// <summary>
/// Every LibVLC call runs on one worker thread (Stop blocks, and deadlocks when called from LibVLC's own event threads).
/// State lives under _gate; events from LibVLC are accepted only while _accepting is true, which Play, Stop and every
/// retry clear and the worker sets again once the previous stream's Stop has returned, so a dying stream cannot
/// speak for the next request. Subscribers are invoked while _gate is held (it is reentrant): they must not block.
/// </summary>
public sealed class VlcPlayer(FileLog? log = null) : IPlayer, IDisposable
{
    private readonly object _gate = new();
    private readonly Channel<Action> _jobs = Channel.CreateUnbounded<Action>(new() { SingleReader = true });
    private Timer? _watchdog;
    private Task? _worker;

    private VideoView? _view;
    private LibVLC? _lib;
    private MediaPlayer? _mp;
    private Media? _media; // worker thread only

    private PlayRequest? _req;
    private ReconnectPolicy _policy = new();
    private PlaybackMonitor _monitor = new(false);
    private CancellationTokenSource _retry = new();
    private int _gen;
    private PlayerState _state = PlayerState.Idle;
    private bool _accepting, _userPaused, _needsRestart, _disposed;
    private int _volume = 100;
    private bool _muted;
    private long _posMs, _lenMs;
    private bool _seekable;
    private IReadOnlyList<TrackInfo> _audio = [], _subs = [];

    public string? LastError { get; private set; }
    public event Action<PlayerState>? StateChanged;
    public event Action<TimeSpan>? PositionChanged;

    private static DateTime Now => DateTime.UtcNow;

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_worker is not null || _disposed) return;
            _worker = Task.Run(RunWorker);
            _watchdog = new Timer(_ => Tick(), null, 1000, 1000);
        }
    }

    /// <summary>Call before the view initializes. Calling again with a new view (page navigation) retires the old player.</summary>
    public void Attach(VideoView view)
    {
        EnsureStarted();
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(_view, view)) return; // the same view keeps its working player
            if (_view is { } old)
            {
                old.Initialized -= OnInitialized;
                old.MediaPlayer = null;
            }
            RetirePlayerLocked();
            _view = view;
        }
        view.Initialized += OnInitialized;
        // A view that is already loaded will not raise Initialized again; its swap chain options are readable only then.
        string[] options;
        try { options = view.SwapChainOptions; }
        catch (InvalidOperationException) { return; }
        Initialize(view, options);
    }

    private void RetirePlayerLocked()
    {
        _accepting = false;
        var (mp, lib) = (_mp, _lib);
        _mp = null;
        _lib = null;
        if (mp is null && lib is null) return;
        Enqueue(() =>
        {
            mp?.Stop();
            _media?.Dispose();
            _media = null;
            mp?.Dispose();
            lib?.Dispose();
        });
    }

    private void OnInitialized(object? sender, InitializedEventArgs e) => Initialize((VideoView)sender!, e.SwapChainOptions);

    private void Initialize(VideoView view, string[] swapChainOptions)
    {
        // Must be created with the swap chain options, or video opens in its own window.
        var lib = new LibVLC(swapChainOptions);
        var mp = new MediaPlayer(lib);
        mp.Playing += (_, _) => OnPlaying(mp);
        mp.EndReached += (_, _) => OnEnded(mp);
        mp.EncounteredError += (_, _) => OnError(mp);
        mp.TimeChanged += (_, a) => OnTime(mp, a.Time);
        mp.LengthChanged += (_, a) => Cache(mp, () => _lenMs = a.Length);
        mp.SeekableChanged += (_, a) => Cache(mp, () => _seekable = a.Seekable != 0);
        mp.ESAdded += (_, _) => Enqueue(RefreshTracks);
        mp.ESDeleted += (_, _) => Enqueue(RefreshTracks);
        int gen;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(view, _view)) { mp.Dispose(); lib.Dispose(); return; }
            RetirePlayerLocked(); // a repeated Initialized must not leave the first player running
            _lib = lib;
            _mp = mp;
            view.MediaPlayer = mp;
            Enqueue(() => { mp.Volume = _volume; mp.Mute = _muted; });
            if (_req is null || _state is PlayerState.Idle or PlayerState.Failed) return; // nothing to replay
            if (_userPaused) { _needsRestart = true; return; } // the new player has no media; Resume starts it
            gen = ++_gen; // resume the current request on the new view
            _monitor.OnOpening(Now);
            SetLocked(PlayerState.Opening);
        }
        Enqueue(() => Start(gen), gen);
    }

    public void Play(PlayRequest r)
    {
        EnsureStarted();
        int gen;
        lock (_gate)
        {
            if (_disposed) return;
            gen = ++_gen;
            ResetForNewRequestLocked();
            _req = r;
            _policy = new ReconnectPolicy();
            _monitor = new PlaybackMonitor(r.IsLive);
            _monitor.OnOpening(Now);
            SetLocked(PlayerState.Opening);
        }
        Enqueue(() => Start(gen), gen); // a no-op until Initialized has created the player; OnInitialized restarts it
    }

    public void Stop()
    {
        int gen;
        lock (_gate)
        {
            if (_disposed) return;
            gen = ++_gen;
            ResetForNewRequestLocked();
            _req = null;
            SetLocked(PlayerState.Idle);
        }
        Enqueue(() => { if (Current(gen)) Mp()?.Stop(); }); // a Play made since then has its own Stop
    }

    private void ResetForNewRequestLocked()
    {
        _retry.Cancel();
        _retry = new();
        _accepting = false;
        _userPaused = _needsRestart = false;
        LastError = null;
        _posMs = _lenMs = 0;
        _seekable = false;
        _audio = _subs = [];
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_disposed || _req is null || _state is PlayerState.Idle or PlayerState.Failed or PlayerState.Paused) return;
            if (_state is PlayerState.Reconnecting or PlayerState.Opening) _needsRestart = true; // no open stream to resume
            _userPaused = true;
            _retry.Cancel();
            _retry = new();
            _monitor.OnPaused();
            SetLocked(PlayerState.Paused);
        }
        Enqueue(() => Mp()?.SetPause(true));
    }

    public void Resume()
    {
        int gen = -1;
        lock (_gate)
        {
            if (_disposed || !_userPaused) return;
            _userPaused = false;
            if (_needsRestart)
            {
                // the input ended or was never there while paused: start it again, keeping the retry budget
                _needsRestart = false;
                gen = ++_gen;
                _accepting = false;
                _monitor.OnOpening(Now);
                SetLocked(PlayerState.Opening);
            }
            else
            {
                _monitor.OnPlaying(Now);
                SetLocked(PlayerState.Playing);
            }
        }
        if (gen >= 0) Enqueue(() => Start(gen), gen);
        else Enqueue(() => Mp()?.SetPause(false));
    }

    public int Volume
    {
        get => _volume;
        set
        {
            var v = _volume = Math.Clamp(value, 0, 100);
            Enqueue(() => { if (Mp() is { } m) m.Volume = v; });
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            Enqueue(() => { if (Mp() is { } m) m.Mute = value; });
        }
    }

    // Getters return values cached from LibVLC events so a subscriber reading them never calls LibVLC from its own callback.
    public TimeSpan Position { get { lock (_gate) return TimeSpan.FromMilliseconds(Math.Max(0, _posMs)); } }
    public TimeSpan Duration { get { lock (_gate) return TimeSpan.FromMilliseconds(Math.Max(0, _lenMs)); } }
    public bool IsSeekable { get { lock (_gate) return _seekable; } }
    public IReadOnlyList<TrackInfo> AudioTracks { get { lock (_gate) return _audio; } }
    public IReadOnlyList<TrackInfo> SubtitleTracks { get { lock (_gate) return _subs; } }

    public void Seek(TimeSpan position) => Enqueue(() => { if (Mp() is { } m) m.Time = (long)position.TotalMilliseconds; });
    public void SelectAudio(int id) => Enqueue(() => Mp()?.SetAudioTrack(id));
    public void SelectSubtitle(int id) => Enqueue(() => Mp()?.SetSpu(id));

    private void RefreshTracks()
    {
        if (Mp() is not { } m) return;
        var audio = Tracks(m.AudioTrackDescription, keepOff: false);
        var subs = Tracks(m.SpuDescription, keepOff: true);
        lock (_gate) { if (Accept(m)) (_audio, _subs) = (audio, subs); } // never an old stream's tracks
    }

    // LibVLC lists a "Disable" pseudo-entry (Id -1): a useful "off" for subtitles, meaningless for audio.
    private static IReadOnlyList<TrackInfo> Tracks(TrackDescription[]? d, bool keepOff) =>
        d is null ? [] : [.. d.Where(t => keepOff || t.Id >= 0).Select(t => new TrackInfo(t.Id, t.Id < 0 ? "Wyłączone" : t.Name ?? ""))];

    private MediaPlayer? Mp() { lock (_gate) return _mp; }
    private bool Current(int gen) { lock (_gate) return gen == _gen && !_disposed; }

    private void Start(int gen)
    {
        PlayRequest? r;
        MediaPlayer? mp;
        LibVLC? lib;
        lock (_gate)
        {
            if (gen != _gen || _disposed) return; // superseded by a newer Play or Stop
            (r, mp, lib) = (_req, _mp, _lib);
        }
        if (r is null || mp is null || lib is null) return;

        mp.Stop();
        _media?.Dispose();
        var media = _media = new Media(lib, r.Url, FromType.FromLocation);
        media.AddOption(":network-caching=1500");
        if (PlayRequest.HeaderValue(r.UserAgent) is { } ua) media.AddOption($":http-user-agent={ua}");
        if (PlayRequest.HeaderValue(r.Referrer) is { } rf) media.AddOption($":http-referrer={rf}");
        lock (_gate)
        {
            if (gen != _gen || !ReferenceEquals(mp, _mp)) return;
            _accepting = true; // from here on, events belong to this request
        }
        mp.Play(media);
    }

    private bool Accept(MediaPlayer mp) => !_disposed && _accepting && ReferenceEquals(mp, _mp);

    private void Cache(MediaPlayer mp, Action set) { lock (_gate) { if (Accept(mp)) set(); } }

    private void OnPlaying(MediaPlayer mp)
    {
        lock (_gate)
        {
            if (!Accept(mp)) return;
            if (_userPaused) { Enqueue(() => Mp()?.SetPause(true)); return; } // the pause arrived before the stream opened
            if (_state is PlayerState.Idle or PlayerState.Failed or PlayerState.Reconnecting) return;
            _monitor.OnPlaying(Now);
            SetLocked(PlayerState.Playing);
        }
        Enqueue(RefreshTracks);
    }

    private void OnTime(MediaPlayer mp, long ms)
    {
        lock (_gate)
        {
            if (!Accept(mp) || _userPaused) return;
            _posMs = ms;
            _monitor.OnTime(Now, ms);
            Raise(PositionChanged, TimeSpan.FromMilliseconds(ms));
        }
    }

    private void OnEnded(MediaPlayer mp)
    {
        lock (_gate)
        {
            if (!Accept(mp)) return;
            if (_userPaused) { _needsRestart = true; return; } // a non-pausable input stops on pause
            if (_req?.IsLive ?? false) { DropLocked(); return; }
            if (_state is PlayerState.Playing or PlayerState.Opening)
            {
                _accepting = false;
                SetLocked(PlayerState.Idle);
            }
        }
    }

    private void OnError(MediaPlayer mp)
    {
        lock (_gate)
        {
            if (!Accept(mp)) return;
            if (_userPaused) { _needsRestart = true; return; }
            if (_req?.IsLive ?? false) DropLocked();
            else FailLocked("Nie udało się odtworzyć strumienia.");
        }
    }

    private void FailLocked(string message)
    {
        if (_state is PlayerState.Idle or PlayerState.Failed) return;
        _accepting = false;
        _userPaused = _needsRestart = false;
        LastError = message;
        var gen = _gen;
        SetLocked(PlayerState.Failed);
        Enqueue(() => { if (Current(gen)) Mp()?.Stop(); });
    }

    // Live stream ended, errored or stalled: wait out the next delay, then start over, until the policy gives up.
    private void DropLocked()
    {
        if (_state is not (PlayerState.Playing or PlayerState.Opening)) return; // already handling a drop
        if (_userPaused) { _needsRestart = true; return; }
        _accepting = false; // also swallows the EndReached that usually follows an error
        if (_policy.NextDelay() is not { } delay)
        {
            FailLocked("Nie udało się połączyć z kanałem. Spróbuj ponownie później.");
            return;
        }
        LastError = null;
        _retry.Cancel();
        _retry = new();
        var (gen, token) = (_gen, _retry.Token);
        SetLocked(PlayerState.Reconnecting);
        _ = Task.Delay(delay, token).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (gen != _gen || _disposed || _userPaused) return;
                _monitor.OnOpening(Now);
                SetLocked(PlayerState.Opening);
            }
            Enqueue(() => Start(gen), gen);
        }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_disposed || _userPaused || _state is not (PlayerState.Opening or PlayerState.Playing)) return;
            switch (_monitor.Tick(Now))
            {
                case MonitorVerdict.Stable: _policy.MarkStable(); break;
                case MonitorVerdict.Stalled when _req?.IsLive ?? false: DropLocked(); break;
                case MonitorVerdict.Stalled: FailLocked("Przekroczono czas oczekiwania na strumień."); break;
            }
        }
    }

    private void SetLocked(PlayerState s)
    {
        if (_disposed || _state == s) return;
        _state = s;
        Raise(StateChanged, s);
    }

    // One failing subscriber must not break the others, the player's state or the worker.
    private void Raise<T>(Action<T>? handler, T value)
    {
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList().Cast<Action<T>>())
        {
            try { d(value); }
            catch (Exception ex) { Log($"Subscriber threw {ex.GetType().Name}"); }
        }
    }

    private void Log(string message)
    {
        try { log?.Write(message); } catch { /* logging must never take the player down */ }
    }

    /// <param name="startGen">For a Start job: a failure marks that request Failed, if it is still current.</param>
    private void Enqueue(Action job, int? startGen = null)
    {
        _jobs.Writer.TryWrite(() =>
        {
            try { job(); }
            catch (Exception ex)
            {
                Log($"Player job failed: {ex.GetType().Name}: {ex.Message}"); // FileLog redacts any URL
                if (startGen is { } g)
                    lock (_gate) { if (g == _gen && !_disposed) FailLocked("Wystąpił błąd odtwarzacza."); }
            }
        });
    }

    private async Task RunWorker()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync())
        {
            try { job(); }
            catch (Exception ex) { Log($"Player worker: {ex.GetType().Name}"); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _gen++;
            _retry.Cancel();
            _req = null;
            if (_view is { } v) { v.Initialized -= OnInitialized; v.MediaPlayer = null; }
            RetirePlayerLocked();
            _disposed = true;
        }
        _watchdog?.Dispose();
        _jobs.Writer.TryComplete();
    }
}
