namespace M3uPlayer.Core.Catchup;

public record SeekPlan(TimeSpan Offset, bool Reopen, TimeSpan PlayerPosition);

/// <summary>
/// Position within an archive programme. OpenedAt is how far into the programme the current stream starts: zero for a stream
/// opened at the programme start, more after a re-open with a shifted start.
/// </summary>
public sealed record ArchiveTimeline(DateTimeOffset Start, DateTimeOffset Stop, TimeSpan OpenedAt = default)
{
    /// <summary>The archive of a programme still in progress lags behind the clock; never seek closer to "now" than this.</summary>
    public static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(60), LiveEndWindow = TimeSpan.FromSeconds(10);

    public TimeSpan Total => Stop - Start;

    public ArchiveTimeline WithOpenedAt(TimeSpan openedAt) => this with { OpenedAt = openedAt };

    public TimeSpan Elapsed(TimeSpan playerPosition) => Bound(OpenedAt + playerPosition, Total);

    /// <summary>The furthest point that may be played: the end of a finished programme, else "now" minus the safety margin.</summary>
    public TimeSpan Limit(DateTimeOffset now) => now >= Stop ? Total : Bound(now - Start - SafetyMargin, Total);

    public bool AtLiveEnd(TimeSpan elapsed, DateTimeOffset now) => now < Stop && elapsed >= Limit(now) - LiveEndWindow;

    public SeekPlan SeekTo(TimeSpan target, DateTimeOffset now, bool seekable)
    {
        var offset = Bound(target, Limit(now));
        // A stream opened later than the target cannot rewind to it, so it has to be opened again.
        var reopen = !seekable || offset < OpenedAt;
        return new SeekPlan(offset, reopen, reopen ? TimeSpan.Zero : offset - OpenedAt);
    }

    private static TimeSpan Bound(TimeSpan value, TimeSpan max) => value < TimeSpan.Zero ? TimeSpan.Zero : value > max ? max : value;

    public static string Clock(TimeSpan t)
    {
        var s = (long)Math.Max(0, t.TotalSeconds);
        return s >= 3600 ? FormattableString.Invariant($"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}") : FormattableString.Invariant($"{s / 60}:{s % 60:00}");
    }
}
