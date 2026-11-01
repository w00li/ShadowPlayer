using Xunit;
using M3uPlayer.Core.Media;

namespace M3uPlayer.Core.Tests;

public class PlaybackMonitorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double s) => T0.AddSeconds(s);

    [Fact]
    public void Playing_that_never_advances_stalls_and_is_never_stable()
    {
        var m = new PlaybackMonitor(live: true);
        m.OnOpening(At(0));
        m.OnPlaying(At(0));
        for (var s = 1; s < 15; s++) Assert.Equal(MonitorVerdict.None, m.Tick(At(s)));
        Assert.Equal(MonitorVerdict.Stalled, m.Tick(At(15)));
    }

    [Fact]
    public void Steady_progress_reports_Stable_once_at_10s()
    {
        var m = new PlaybackMonitor(live: true);
        m.OnOpening(At(0));
        m.OnPlaying(At(0));
        var seen = new List<(int, MonitorVerdict)>();
        for (var s = 1; s <= 30; s++)
        {
            m.OnTime(At(s), s * 1000);
            var v = m.Tick(At(s));
            if (v != MonitorVerdict.None) seen.Add((s, v));
        }
        Assert.Equal([(11, MonitorVerdict.Stable)], seen); // progress began at 1 s, so the 10 s window closes at 11 s
    }

    [Fact]
    public void Paused_never_stalls()
    {
        var m = new PlaybackMonitor(live: true);
        m.OnOpening(At(0));
        m.OnPlaying(At(0));
        m.OnTime(At(1), 1000);
        m.OnPaused();
        Assert.Equal(MonitorVerdict.None, m.Tick(At(120)));
    }

    [Fact]
    public void Buffering_gap_under_15s_does_not_stall()
    {
        var m = new PlaybackMonitor(live: true);
        m.OnOpening(At(0));
        m.OnPlaying(At(0));
        m.OnTime(At(1), 1000);
        Assert.Equal(MonitorVerdict.None, m.Tick(At(15)));
        m.OnTime(At(15), 2000);
        Assert.NotEqual(MonitorVerdict.Stalled, m.Tick(At(29)));
    }

    [Fact]
    public void Opening_over_15s_stalls_for_live_and_archive()
    {
        foreach (var live in new[] { true, false })
        {
            var m = new PlaybackMonitor(live);
            m.OnOpening(At(0));
            Assert.Equal(MonitorVerdict.None, m.Tick(At(14)));
            Assert.Equal(MonitorVerdict.Stalled, m.Tick(At(15)));
        }
    }

    [Fact]
    public void Archive_playing_without_progress_does_not_stall()
    {
        var m = new PlaybackMonitor(live: false);
        m.OnOpening(At(0));
        m.OnPlaying(At(0));
        Assert.Equal(MonitorVerdict.None, m.Tick(At(100)));
    }

    [Fact]
    public void Repeated_stalls_exhaust_the_reconnect_policy()
    {
        var policy = new ReconnectPolicy();
        var m = new PlaybackMonitor(live: true);
        var now = 0.0;
        var delays = new List<TimeSpan?>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            m.OnOpening(At(now));
            m.OnPlaying(At(now));
            for (var s = 1; s <= 15; s++)
            {
                var v = m.Tick(At(now + s));
                Assert.NotEqual(MonitorVerdict.Stable, v);
                if (v == MonitorVerdict.Stalled) { delays.Add(policy.NextDelay()); break; }
            }
            now += 20;
        }
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8), null], delays);
    }
}
