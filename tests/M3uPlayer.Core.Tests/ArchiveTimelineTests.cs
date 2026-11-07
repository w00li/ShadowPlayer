using M3uPlayer.Core.Catchup;
using M3uPlayer.Core.Epg;
using M3uPlayer.Core.Media;
using M3uPlayer.Core.Storage;
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
        Assert.Equal(S(900), p.Offset);
    }

    [Fact]
    public void Seek_before_the_opened_at_point_has_to_reopen_even_when_seekable()
    {
        var p = new ArchiveTimeline(Start, Stop, S(600)).SeekTo(S(100), Later, seekable: true);
        Assert.True(p.Reopen);
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
    public void Overlay_never_says_the_programme_ended_because_playback_moves_on_by_itself()
    {
        var o = OverlayInfo.For(PlayerState.Idle, false, null, "TVP", archive: true);
        Assert.Equal((true, "TVP", true, "Oglądaj"), (o.Visible, o.Text, o.Button, o.ButtonText));
    }

    private static readonly CatchupInfo Archive10Days = new("fs", null, 10);
    private static Programme P(string title, int startHour, int stopHour) => new(title, null, null, null, null, Start.AddHours(startHour), Start.AddHours(stopHour));

    [Fact]
    public void Next_programme_is_the_one_after_the_current()
    {
        var guide = new[] { P("a", 0, 1), P("b", 1, 2), P("c", 2, 3) };
        Assert.Equal("b", ArchiveTimeline.NextProgramme(guide, Start, Archive10Days, Start.AddHours(5))?.Title);
    }

    [Fact]
    public void Next_programme_may_be_still_running() =>
        Assert.Equal("b", ArchiveTimeline.NextProgramme([P("a", 0, 1), P("b", 1, 2)], Start, Archive10Days, Start.AddMinutes(90))?.Title);

    [Fact]
    public void Next_programme_skips_a_gap_in_the_guide() =>
        Assert.Equal("c", ArchiveTimeline.NextProgramme([P("a", 0, 1), P("c", 3, 4)], Start, Archive10Days, Start.AddHours(5))?.Title);

    [Fact]
    public void No_next_programme_when_it_has_not_started() =>
        Assert.Null(ArchiveTimeline.NextProgramme([P("a", 0, 1), P("b", 1, 2)], Start, Archive10Days, Start.AddMinutes(59)));

    [Fact]
    public void No_next_programme_after_the_last_one() =>
        Assert.Null(ArchiveTimeline.NextProgramme([P("a", 0, 1)], Start, Archive10Days, Start.AddHours(5)));

    [Fact]
    public void No_next_programme_without_an_archive() =>
        Assert.Null(ArchiveTimeline.NextProgramme([P("a", 0, 1), P("b", 1, 2)], Start, new CatchupInfo(null, null, 0), Start.AddHours(5)));

    [Fact]
    public void Going_back_from_live_lands_a_step_behind_the_archive_edge()
    {
        var start = new DateTimeOffset(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);
        var t = new ArchiveTimeline(start, start.AddHours(1));
        // 30 minutes in: the archive reaches 29:00 (60 s margin), one 10 s step behind is 28:50.
        Assert.Equal(TimeSpan.FromSeconds(28 * 60 + 50), t.BehindLive(start.AddMinutes(30), TimeSpan.FromSeconds(10)));
        // 40 s in: not even the margin has passed, so from the start.
        Assert.Equal(TimeSpan.Zero, t.BehindLive(start.AddSeconds(40), TimeSpan.FromSeconds(10)));
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
    public void Left_goes_back_from_live_too_but_right_only_seeks_in_archive()
    {
        Assert.Equal(ShortcutAction.SeekBack, Shortcuts.For(ShortcutKey.Left, FocusKind.Video, false, false)); // from live into the archive
        Assert.Equal(ShortcutAction.None, Shortcuts.For(ShortcutKey.Right, FocusKind.Video, false, false));
        Assert.Equal(ShortcutAction.None, Shortcuts.For(ShortcutKey.Left, FocusKind.List, false, false));
        Assert.Equal(ShortcutAction.SeekBack, Shortcuts.For(ShortcutKey.Left, FocusKind.Video, false, true));
        Assert.Equal(ShortcutAction.SeekForward, Shortcuts.For(ShortcutKey.Right, FocusKind.Button, true, true));
        Assert.Equal(ShortcutAction.None, Shortcuts.For(ShortcutKey.Right, FocusKind.Slider, false, true));
    }

    [Theory]
    [InlineData(FocusKind.Video, ShortcutAction.RestartProgramme)]
    [InlineData(FocusKind.Button, ShortcutAction.RestartProgramme)]
    [InlineData(FocusKind.List, ShortcutAction.None)]   // Home moves to the first item there
    [InlineData(FocusKind.Slider, ShortcutAction.None)] // and to the start of the slider
    [InlineData(FocusKind.Text, ShortcutAction.None)]
    public void Home_restarts_the_programme(FocusKind focus, ShortcutAction expected)
    {
        Assert.Equal(expected, Shortcuts.For(ShortcutKey.Home, focus, false, false));
        Assert.Equal(expected, Shortcuts.For(ShortcutKey.Home, focus, true, true));
    }
}
