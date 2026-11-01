using Xunit;
using M3uPlayer.Core.Playlist;

namespace M3uPlayer.Core.Tests;

public class M3uParserTests
{
    private static M3uDocument P(string text) => M3uParser.Parse(new StringReader(text));
    private static M3uEntry One(string extinf, string url = "http://h.example.invalid/1") =>
        Assert.Single(P($"#EXTM3U\n{extinf}\n{url}\n").Entries);

    [Fact]
    public void Parse_reads_provider_format()
    {
        using var reader = new StreamReader(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.m3u"));
        var doc = M3uParser.Parse(reader);
        var e = doc.Entries[0];
        Assert.Equal("TVP 1", e.TvgId);
        Assert.Equal("TVP 1 HD", e.TvgName);
        Assert.Equal("http://logo.example.invalid/tvp1.png", e.Logo);
        Assert.Equal("Polska", e.Category);
        Assert.Equal("fs", e.CatchupType);
        Assert.Equal(10, e.CatchupDays);
        Assert.Equal("Sport, Live", doc.Entries[1].Category);
        Assert.Equal("TestAgent/1.0", doc.Entries[1].UserAgent);
    }

    [Fact]
    public void Parse_catchup_days_wins_over_timeshift() =>
        Assert.Equal(3, One("#EXTINF:-1 catchup-days=\"3\" timeshift=\"10\",A").CatchupDays);

    [Fact]
    public void Parse_reads_epg_url()
    {
        Assert.Equal("http://e/1", P("#EXTM3U url-tvg=\"http://e/1\"\n").EpgUrl);
        Assert.Equal("http://e/2", P("#EXTM3U x-tvg-url=\"http://e/2\"\n").EpgUrl);
    }

    [Fact]
    public void Parse_comma_in_attribute_keeps_name()
    {
        var e = One("#EXTINF:-1 group-title=\"Sport, Live\",Kanał 1");
        Assert.Equal("Kanał 1", e.Name);
        Assert.Equal("Sport, Live", e.Category);
    }

    [Fact]
    public void Parse_comma_in_name() =>
        Assert.Equal("Kino, Polska", One("#EXTINF:-1 tvg-id=\"a\",Kino, Polska").Name);

    [Fact]
    public void Parse_missing_group_gives_Inne() => Assert.Equal("Inne", One("#EXTINF:-1,A").Category);

    [Fact]
    public void Parse_handles_bom_and_crlf()
    {
        var doc = P("\uFEFF#EXTM3U\r\n#EXTINF:-1,A\r\nhttp://h/1\r\n");
        var e = Assert.Single(doc.Entries);
        Assert.Equal("A", e.Name);
        Assert.Equal("http://h/1", e.Url);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e300")]
    [InlineData("25")]
    [InlineData("-24.5")]
    public void Parse_tvg_shift_untrusted_value_becomes_zero(string raw) =>
        Assert.Equal(0, One($"#EXTINF:-1 tvg-shift=\"{raw}\",A").ShiftHours);

    [Fact]
    public void Parse_tvg_shift_in_hours() => Assert.Equal(-1.5, One("#EXTINF:-1 tvg-shift=\"-1.5\",A").ShiftHours);

    [Fact]
    public void Parse_vlcopt_allowlist()
    {
        var doc = P("#EXTM3U\n#EXTINF:-1,A\n#EXTVLCOPT:http-user-agent=UA\n#EXTVLCOPT:http-referrer=http://r/\n" +
                    "#EXTVLCOPT:sout=#file{dst=c:\\x}\nhttp://h/1\n");
        var e = Assert.Single(doc.Entries);
        Assert.Equal("UA", e.UserAgent);
        Assert.Equal("http://r/", e.Referrer);
        Assert.Equal("A", e.Name);
        Assert.Equal("http://h/1", e.Url);
    }

    [Fact]
    public void Parse_vlcopt_applies_to_next_entry_only()
    {
        var doc = P("#EXTM3U\n#EXTINF:-1,A\n#EXTVLCOPT:http-user-agent=UA\nhttp://h/1\n#EXTINF:-1,B\nhttp://h/2\n");
        Assert.Equal("UA", doc.Entries[0].UserAgent);
        Assert.Null(doc.Entries[1].UserAgent);
    }

    [Fact]
    public void Parse_rejects_unsafe_schemes()
    {
        var doc = P("#EXTM3U\n#EXTINF:-1,A\nfile:///c/x.ts\n#EXTINF:-1,B\nscreen://\n#EXTINF:-1,C\ndshow://\n" +
                    "#EXTINF:-1,D\nC:\\x.ts\n#EXTINF:-1,E\nhttp://h/1\n#EXTINF:-1,F\nHTTPS://h/2\n#EXTINF:-1,G\nrtsp://h/3\n" +
                    "#EXTINF:-1,H\nrtp://h/4\n#EXTINF:-1,I\nudp://@239.0.0.1:1234\n");
        Assert.Equal(4, doc.SkippedCount);
        Assert.Equal(5, doc.Entries.Count);
    }

    [Fact]
    public void Parse_extinf_without_url_is_skipped_and_counted()
    {
        var doc = P("#EXTM3U\n#EXTINF:-1,A\n#EXTINF:-1,B\nhttp://h/1\n#EXTINF:-1,C\n");
        Assert.Equal("B", Assert.Single(doc.Entries).Name);
        Assert.Equal(2, doc.SkippedCount);
    }

    [Fact]
    public void Parse_blank_name_is_skipped_and_counted()
    {
        var doc = P("#EXTM3U\n#EXTINF:-1 tvg-id=\"x\", \nhttp://h/1\n");
        Assert.Empty(doc.Entries);
        Assert.Equal(1, doc.SkippedCount);
    }

    [Fact]
    public void Parse_missing_header_throws() => Assert.Throws<FormatException>(() => P("<html>"));

    [Fact]
    public void Parse_key_uses_tvg_id_then_name()
    {
        Assert.Equal("TVP 1", One("#EXTINF:-1 tvg-id=\"TVP 1\",Other").Key);
        Assert.Equal("Name", One("#EXTINF:-1 tvg-id=\"\",Name").Key);
    }

    [Fact]
    public void Parse_duplicate_keys_get_suffix()
    {
        var doc = P("#EXTM3U\n#EXTINF:-1 tvg-id=\"X\",A\nhttp://h/1\n#EXTINF:-1 tvg-id=\"X\",B\nhttp://h/2\n");
        Assert.Equal(new[] { "X", "X#2" }, doc.Entries.Select(e => e.Key));
    }

    [Fact]
    public void Parse_non_numeric_values_default_to_zero_or_null()
    {
        var e = One("#EXTINF:-1 catchup=\"\" catchup-days=\"abc\" tvg-shift=\"x\",A");
        Assert.Null(e.CatchupType);
        Assert.Equal(0, e.CatchupDays);
        Assert.Equal(0, e.ShiftHours);
    }
}
