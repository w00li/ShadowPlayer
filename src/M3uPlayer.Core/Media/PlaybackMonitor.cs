namespace M3uPlayer.Core.Media;

public enum MonitorVerdict { None, Stable, Stalled }

/// <summary>
/// Watches one playback attempt. Stable means position really advanced across a 10 s window;
/// Stalled means no open within 15 s, or (live only) no position change for 15 s while playing.
/// Pure: the caller supplies the time and serialises access.
/// </summary>
public class PlaybackMonitor(bool live)
{
    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(15);

    private enum Phase { Idle, Opening, Playing, Paused }
    private Phase _phase;
    private DateTime _since;        // start of Opening, or the last position change while Playing
    private DateTime? _progressFrom; // first position change of this attempt
    private DateTime _lastProgress;   // latest position change; unlike _since, a resume does not move it
    private long _lastMs = -1;
    private bool _stableReported;

    public void OnOpening(DateTime now)
    {
        _phase = Phase.Opening;
        _since = now;
        _progressFrom = null;
        _lastMs = -1;
        _stableReported = false;
    }

    public void OnPlaying(DateTime now)
    {
        if (_phase == Phase.Paused) _progressFrom = null; // a pause must not count as progress
        _phase = Phase.Playing;
        _since = now;
    }

    public void OnTime(DateTime now, long ms)
    {
        if (_phase != Phase.Playing || ms == _lastMs) return;
        _lastMs = ms;
        _since = now;
        _progressFrom ??= now;
        _lastProgress = now;
    }

    public void OnPaused() => _phase = Phase.Paused;

    public MonitorVerdict Tick(DateTime now)
    {
        switch (_phase)
        {
            case Phase.Opening when now - _since >= StallAfter:
            case Phase.Playing when live && now - _since >= StallAfter:
                _phase = Phase.Idle;
                return MonitorVerdict.Stalled;
            case Phase.Playing when !_stableReported && _progressFrom is { } from && _lastProgress - from >= StableAfter:
                _stableReported = true;
                return MonitorVerdict.Stable;
            default:
                return MonitorVerdict.None;
        }
    }
}
