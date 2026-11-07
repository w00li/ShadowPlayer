using System.ComponentModel;

namespace M3uPlayer.Core.Catchup;

/// <summary>The archive programme being watched and where in it the viewer is. Null timeline means live.</summary>
public sealed class ArchiveModel : INotifyPropertyChanged
{
    // After a reopen or a seek the player still reports the old position for a while (events already queued, seek not yet applied).
    // Those are ignored until one lands near the new position, or the grace period runs out.
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(3), Grace = TimeSpan.FromSeconds(5);
    private TimeSpan _expected;
    private DateTimeOffset? _giveUpAt;
    private bool _endReported;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ArchiveTimeline? Timeline { get; private set; }
    public bool Active => Timeline is not null;
    public string Caption { get; private set; } = "";
    public TimeSpan Elapsed { get; private set; }
    public double Maximum { get; private set; } = 1;
    public double Value => Elapsed.TotalSeconds;
    public string ElapsedText => ArchiveTimeline.Clock(Elapsed);
    public string TotalText => Timeline is { } t ? ArchiveTimeline.Clock(t.Total) : "";
    public bool AtLiveEnd { get; private set; }
    public string AutomationName => $"Pozycja w programie: {ElapsedText} z {TotalText}";

    public void Begin(ArchiveTimeline t, string title, DateTimeOffset now)
    {
        Timeline = t;
        Caption = "Archiwum: " + title;
        Maximum = Math.Max(1, t.Total.TotalSeconds);
        Expect(TimeSpan.Zero, now);
        Show(t.OpenedAt, now);
    }

    public void Reopened(TimeSpan offset, DateTimeOffset now)
    {
        if (Timeline is { } t) Timeline = t.WithOpenedAt(offset);
        Expect(TimeSpan.Zero, now);
        Show(offset, now);
    }

    /// <summary>A seek inside the running stream: show the target now and wait for the player to get there.</summary>
    public void Seeked(TimeSpan playerPosition, DateTimeOffset now)
    {
        if (Timeline is not { } t) return;
        Expect(playerPosition, now);
        Show(t.Elapsed(playerPosition), now);
    }

    public void Clear()
    {
        (_giveUpAt, _endReported) = (null, false);
        if (Timeline is null) return;
        (Timeline, Elapsed, AtLiveEnd) = (null, TimeSpan.Zero, false);
        Raise();
    }

    /// <summary>True once when the stream passes the programme's end: a shift archive does not stop there but plays on up to "now".</summary>
    public bool SetPosition(TimeSpan playerPosition, DateTimeOffset now)
    {
        if (Timeline is not { } t) return false;
        if (_giveUpAt is { } until)
        {
            if (now < until && (playerPosition - _expected).Duration() > Tolerance) return false;
            _giveUpAt = null;
        }
        var elapsed = t.Elapsed(playerPosition);
        if ((long)elapsed.TotalSeconds != (long)Elapsed.TotalSeconds || t.AtLiveEnd(elapsed, now) != AtLiveEnd) Show(elapsed, now);
        if (_endReported || t.OpenedAt + playerPosition < t.Total) return false;
        return _endReported = true;
    }

    private void Expect(TimeSpan playerPosition, DateTimeOffset now) => (_expected, _giveUpAt, _endReported) = (playerPosition, now + Grace, false);

    private void Show(TimeSpan elapsed, DateTimeOffset now)
    {
        Elapsed = elapsed;
        AtLiveEnd = Timeline?.AtLiveEnd(elapsed, now) ?? false;
        Raise();
    }

    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
