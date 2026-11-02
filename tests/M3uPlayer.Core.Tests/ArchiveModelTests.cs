using M3uPlayer.Core.Catchup;
using M3uPlayer.Core.Epg;
using M3uPlayer.Core.Media;
using M3uPlayer.Core.Storage;
using Xunit;

namespace M3uPlayer.Core.Tests;

public class ArchiveModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddHours(5);
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    private static ArchiveModel Playing()
    {
        var m = new ArchiveModel();
        m.Begin(new ArchiveTimeline(Start, Start.AddHours(1)), "Film", Now);
        m.SetPosition(S(1200), Now); // a leftover of the previous stream
        return m;
    }

    [Fact]
    public void Begin_shows_the_start_and_ignores_a_stale_position_from_the_previous_stream()
    {
        var m = Playing();
        Assert.Equal(S(0), m.Elapsed);
        m.SetPosition(S(1), Now);
        Assert.Equal(S(1), m.Elapsed);
    }

    [Fact]
    public void Reopen_ignores_old_positions_until_one_near_the_new_start_arrives()
    {
        var m = Playing();
        m.SetPosition(S(1), Now);
        m.Reopened(S(600), Now);
        m.SetPosition(S(900), Now); // queued before the reopen: 600 + 900 would jump forward
        Assert.Equal(S(600), m.Elapsed);
        m.SetPosition(S(2), Now);
        Assert.Equal(S(602), m.Elapsed);
        m.SetPosition(S(40), Now); // after the first match everything is accepted again
        Assert.Equal(S(640), m.Elapsed);
    }

    [Fact]
    public void Seek_in_the_stream_holds_the_target_until_the_player_reaches_it()
    {
        var m = Playing();
        m.SetPosition(S(1), Now);
        m.Seeked(S(300), Now);
        Assert.Equal(S(300), m.Elapsed);
        m.SetPosition(S(5), Now); // the player has not applied the seek yet
        Assert.Equal(S(300), m.Elapsed);
        m.SetPosition(S(301), Now);
        Assert.Equal(S(301), m.Elapsed);
    }

    [Fact]
    public void A_player_that_never_reaches_the_target_is_believed_after_the_grace_period()
    {
        var m = Playing();
        m.SetPosition(S(1), Now);
        m.Seeked(S(300), Now);
        m.SetPosition(S(5), Now.AddSeconds(6));
        Assert.Equal(S(5), m.Elapsed);
    }

    [Fact]
    public void Clear_leaves_live_mode()
    {
        var m = Playing();
        m.Clear();
        Assert.False(m.Active);
    }
}

public class OverlayResumeTests
{
    [Fact]
    public void Archive_stopped_by_leaving_the_screen_offers_to_continue()
    {
        var o = OverlayInfo.For(PlayerState.Idle, false, null, "TVP", archive: true, resumable: true);
        Assert.Equal((true, "Wstrzymano", true, "Oglądaj dalej"), (o.Visible, o.Text, o.Button, o.ButtonText));
    }
}

public class SessionRestoreTests
{
    private static Channel Ch(string category, bool fav = false) =>
        new("k", 1, "n", "http://example.invalid/", null, category, 0, new CatchupInfo(null, null, 0), null, null, fav);

    private static readonly HashSet<string> Known = ["c:News", "c:Kids"];

    [Theory]
    [InlineData(null, "*all")]
    [InlineData("*fav", "*fav")]
    [InlineData("c:News", "c:News")]
    [InlineData("c:Gone", "*all")]
    public void Category_must_still_exist(string? saved, string expected) =>
        Assert.Equal(expected, SessionRestore.StartCategory(saved, Known, null));

    [Fact]
    public void Category_that_no_longer_holds_the_channel_means_all()
    {
        Assert.Equal("c:News", SessionRestore.StartCategory("c:News", Known, Ch("News")));
        Assert.Equal("*all", SessionRestore.StartCategory("c:Kids", Known, Ch("News")));
        Assert.Equal("*all", SessionRestore.StartCategory("*fav", Known, Ch("News", fav: false)));
        Assert.Equal("*fav", SessionRestore.StartCategory("*fav", Known, Ch("News", fav: true)));
    }
}
