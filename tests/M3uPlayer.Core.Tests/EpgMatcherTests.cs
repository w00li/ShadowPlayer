using Xunit;
using M3uPlayer.Core.Epg;

namespace M3uPlayer.Core.Tests;

public class EpgMatcherTests
{
    private static XmltvChannel Ch(string id, params string[] names) => new(id, names);

    [Fact]
    public void Match_exact_tvg_id() =>
        Assert.Equal("TVP 1", new EpgMatcher([Ch("TVP 1", "TVP 1")]).Match("TVP 1", "x", null));

    [Fact]
    public void Match_tvg_id_ignores_case() =>
        Assert.Equal("TVP 1", new EpgMatcher([Ch("TVP 1", "TVP 1")]).Match("tvp 1", "x", null));

    [Fact]
    public void Match_by_name_when_id_unknown() =>
        Assert.Equal("ss1", new EpgMatcher([Ch("ss1", "Stan Sport 1")]).Match("stansport1", "Stan Sport 1", null));

    [Fact]
    public void Match_by_tvg_name_when_name_fails() =>
        Assert.Equal("ss1", new EpgMatcher([Ch("ss1", "Stan Sport 1")]).Match(null, "Nothing", "Stan Sport 1"));

    [Fact]
    public void Match_by_epg_id_as_name() =>
        Assert.Equal("NBA TV", new EpgMatcher([Ch("NBA TV", "Other")]).Match(null, "NBA TV", null));

    [Fact]
    public void Match_ignores_quality_tag() =>
        Assert.Equal("TVP 2", new EpgMatcher([Ch("TVP 2", "TVP 2")]).Match(null, "TVP 2 HD", null));

    [Fact]
    public void Match_prefers_same_quality() =>
        Assert.Equal("ViDocTV 4К", new EpgMatcher([Ch("ViDoc TV", "ViDoc TV"), Ch("ViDocTV 4К", "ViDocTV 4К")]).Match(null, "ViDocTV 4k", null));

    [Fact]
    public void Match_falls_back_to_first_in_file() =>
        Assert.Equal("a", new EpgMatcher([Ch("a", "Foo HD"), Ch("b", "Foo SD")]).Match(null, "Foo 4k", null));

    [Fact]
    public void Match_no_candidate_returns_null() =>
        Assert.Null(new EpgMatcher([Ch("a", "Foo")]).Match("zzz", "Bar", "Baz"));

    [Fact]
    public void Match_empty_base_never_matches_by_name() =>
        Assert.Null(new EpgMatcher([Ch("a", "---")]).Match(null, "HD", "---"));

    [Fact]
    public void Match_same_quality_uses_any_display_name_of_channel() =>
        Assert.Equal("x", new EpgMatcher([Ch("x", "Foo", "Foo HD"), Ch("y", "Foo HD")]).Match(null, "Foo HD", null));

    [Fact]
    public void Match_same_quality_uses_any_display_name_of_channel_reverse() =>
        Assert.Equal("x", new EpgMatcher([Ch("x", "Foo HD", "Foo"), Ch("y", "Foo")]).Match(null, "Foo", null));
}
