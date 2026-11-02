using M3uPlayer.Core.Catchup;

namespace M3uPlayer.App.ViewModels;

/// <summary>The archive programme being watched and where in it the viewer is. Null timeline means live.</summary>
public sealed class ArchiveModel : Observable
{
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
        Show(t.OpenedAt, now);
    }

    public void Reopened(TimeSpan offset, DateTimeOffset now)
    {
        if (Timeline is { } t) Timeline = t.WithOpenedAt(offset);
        Show(offset, now);
    }

    public void Clear()
    {
        if (Timeline is null) return;
        (Timeline, Elapsed, AtLiveEnd) = (null, TimeSpan.Zero, false);
        Raise();
    }

    public void SetPosition(TimeSpan playerPosition, DateTimeOffset now)
    {
        if (Timeline is not { } t) return;
        var elapsed = t.Elapsed(playerPosition);
        if ((long)elapsed.TotalSeconds != (long)Elapsed.TotalSeconds || t.AtLiveEnd(elapsed, now) != AtLiveEnd) Show(elapsed, now);
    }

    private void Show(TimeSpan elapsed, DateTimeOffset now)
    {
        Elapsed = elapsed;
        AtLiveEnd = Timeline?.AtLiveEnd(elapsed, now) ?? false;
        Raise();
    }
}
