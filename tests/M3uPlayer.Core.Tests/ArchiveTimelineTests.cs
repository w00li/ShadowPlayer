using M3uPlayer.Core.Catchup;
using M3uPlayer.Core.Epg;
using M3uPlayer.Core.Media;
using Xunit;

namespace M3uPlayer.Core.Tests;

public class ArchiveTimelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Stop = Start.AddHours(1);
    private static readonly DateTimeOffset Later = Stop.AddHours(2); // programme finished
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Elapsed_adds_the_opened_at_offset_and_clamps_to_the_programme()
    {
        var t = new ArchiveTimeline(Start, Stop, S(600));
        Assert.Equal(S(630), t.Elapsed(S(30)));
        Assert.Equal(S(3600), t.Elapsed(S(99999)));
        Assert.Equal(S(0), new ArchiveTimeline(Start, Stop).Elapsed(S(-5)));
    }

    [Fact]
    public void Limit_is_the_whole_programme_once_it_has_ended()
    {
        Assert.Equal(S(3600), new ArchiveTimeline(Start, Stop).Limit(Later));
    }

    [Fact]
    public void Limit_of_a_programme_in_progress_is_now_minus_the_safety_margin()
    {
        var now = Start.AddMinutes(20);
        Assert.Equal(S(1200 - 60), new ArchiveTimeline(Start, Stop).Limit(now));
    }

    [Fact]
    public void Limit_never_goes_below_zero()
    {
        Assert.Equal(S(0), new ArchiveTimeline(Start, Stop).Limit(Start.AddSeconds(20)));
    }

    [Fact]
    public void Seek_when_seekable_moves_the_player_relative_to_where_the_stream_was_opened()
    {
        var p = new ArchiveTimeline(Start, Stop).SeekTo(S(900), Later, seekable: true);
        Assert.Equal((S(900), false, S(900)), (p.Offset, p.Reopen, p.PlayerPosition));
        var reopened = new ArchiveTimeline(Start, Stop, S(600)).SeekTo(S(900), Later, seekable: true);
        Assert.Equal((false, S(300)), (reopened.Reopen, reopened.PlayerPosition));
    }

    [Fact]
    public void Seek_clamps_to_the_limit_for_a_programme_in_progress()
    {
        var now = Start.AddMinutes(30);
        var p = new ArchiveTimeline(Start, Stop).SeekTo(S(3000), now, seekable: true);
        Assert.Equal(S(1800 - 60), p.Offset);
    }

    [Fact]
    public void Seek_clamps_below_zero()
    {
        Assert.Equal(S(0), new ArchiveTimeline(Start, Stop).SeekTo(S(-30), Later, seekable: true).Offset);
    }

    [Fact]
    public void Seek_without_seekable_reopens_at_start_plus_offset()
    {
        var p = new ArchiveTimeline(Start, Stop).SeekTo(S(900), Later, seekable: false);
        Assert.True(p.Reopen);
        Assert.Equal(Start.AddSeconds(900), p.NewStart);
        Assert.Equal(S(900), p.Offset);
    }

    [Fact]
    public void Seek_before_the_opened_at_point_has_to_reopen_even_when_seekable()
    {
        var p = new ArchiveTimeline(Start, Stop, S(600)).SeekTo(S(100), Later, seekable: true);
        Assert.True(p.Reopen);
        Assert.Equal(Start.AddSeconds(100), p.NewStart);
    }

    [Fact]
    public void Live_end_only_for_a_programme_in_progress_near_the_limit()
    {
        var t = new ArchiveTimeline(Start, Stop);
        var now = Start.AddMinutes(30);
        Assert.True(t.AtLiveEnd(S(1800 - 60), now));
        Assert.False(t.AtLiveEnd(S(600), now));
        Assert.False(t.AtLiveEnd(S(3600), Later));
    }

    [Fact]
    public void WithOpenedAt_keeps_the_programme()
    {
        var t = new ArchiveTimeline(Start, Stop).WithOpenedAt(S(42));
        Assert.Equal((Start, Stop, S(42)), (t.Start, t.Stop, t.OpenedAt));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(7325, "2:02:05")]
    [InlineData(-4, "0:00")]
    public void Clock_formats_minutes_and_hours(double seconds, string expected) =>
        Assert.Equal(expected, ArchiveTimeline.Clock(S(seconds)));

    [Fact]
    public void Overlay_after_an_archive_programme_ends_offers_the_way_back()
    {
        var o = OverlayInfo.For(PlayerState.Idle, false, null, "TVP", archive: true);
        Assert.Equal((true, "Program się skończył", true, "Wróć na żywo"), (o.Visible, o.Text, o.Button, o.ButtonText));
    }
}

public class ShortcutsTests
{
    [Theory]
    [InlineData(ShortcutKey.Space, FocusKind.Video, ShortcutAction.TogglePause)]
    [InlineData(ShortcutKey.Space, FocusKind.Other, ShortcutAction.TogglePause)]
    [InlineData(ShortcutKey.Space, FocusKind.Button, ShortcutAction.None)]
    [InlineData(ShortcutKey.Space, FocusKind.List, ShortcutAction.None)]
    [InlineData(ShortcutKey.Space, FocusKind.Text, ShortcutAction.None)]
    [InlineData(ShortcutKey.F, FocusKind.Button, ShortcutAction.ToggleFullscreen)]
    [InlineData(ShortcutKey.F, FocusKind.List, ShortcutAction.ToggleFullscreen)]
    [InlineData(ShortcutKey.F, FocusKind.Text, ShortcutAction.None)]
    [InlineData(ShortcutKey.M, FocusKind.Video, ShortcutAction.ToggleMute)]
    [InlineData(ShortcutKey.M, FocusKind.Text, ShortcutAction.None)]
    [InlineData(ShortcutKey.Up, FocusKind.Video, ShortcutAction.PreviousChannel)]
    [InlineData(ShortcutKey.Down, FocusKind.Button, ShortcutAction.NextChannel)]
    [InlineData(ShortcutKey.Down, FocusKind.List, ShortcutAction.None)]
    [InlineData(ShortcutKey.Up, FocusKind.Text, ShortcutAction.None)]
    [InlineData(ShortcutKey.Up, FocusKind.Slider, ShortcutAction.None)]
    public void Keys_follow_the_focus(ShortcutKey key, FocusKind focus, ShortcutAction expected) =>
        Assert.Equal(expected, Shortcuts.For(key, focus, fullscreen: false, archive: false));

    [Fact]
    public void Escape_leaves_fullscreen_only()
    {
        Assert.Equal(ShortcutAction.ExitFullscreen, Shortcuts.For(ShortcutKey.Escape, FocusKind.List, true, false));
        Assert.Equal(ShortcutAction.None, Shortcuts.For(ShortcutKey.Escape, FocusKind.List, false, false));
        Assert.Equal(ShortcutAction.None, Shortcuts.For(ShortcutKey.Escape, FocusKind.Text, true, false));
    }

    [Fact]
    public void Left_and_right_seek_only_in_archive()
    {
        Assert.Equal(ShortcutAction.None, Shortcuts.For(ShortcutKey.Left, FocusKind.Video, false, false));
        Assert.Equal(ShortcutAction.SeekBack, Shortcuts.For(ShortcutKey.Left, FocusKind.Video, false, true));
        Assert.Equal(ShortcutAction.SeekForward, Shortcuts.For(ShortcutKey.Right, FocusKind.Button, true, true));
        Assert.Equal(ShortcutAction.None, Shortcuts.For(ShortcutKey.Right, FocusKind.Slider, false, true));
    }
}
