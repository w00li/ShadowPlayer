using System.IO.Compression;
using System.Text;
using System.Xml;
using Xunit;
using M3uPlayer.Core.Epg;

namespace M3uPlayer.Core.Tests;

public class XmltvReaderTests
{
    private static Stream S(string xml) => new MemoryStream(new UTF8Encoding(false).GetBytes(xml));

    private static (List<XmltvChannel> Channels, List<XmltvProgramme> Programmes, int Skipped) R(Stream s)
    {
        var c = new List<XmltvChannel>();
        var p = new List<XmltvProgramme>();
        var skipped = XmltvReader.Read(s, c.Add, p.Add);
        return (c, p, skipped);
    }

    private static (List<XmltvChannel> Channels, List<XmltvProgramme> Programmes, int Skipped) R(string xml) => R(S(xml));

    private static XmltvProgramme One(string attrs, string body = "<title>T</title>") =>
        Assert.Single(R($"<tv><programme {attrs} channel=\"c\">{body}</programme></tv>").Programmes);

    [Fact]
    public void Read_parses_channel_with_multiple_display_names()
    {
        using var s = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.xml"));
        var ch = Assert.Single(R(s).Channels);
        Assert.Equal("tvp1.example", ch.Id);
        Assert.Equal(new[] { "TVP 1", "TVP 1 HD" }, ch.DisplayNames);
    }

    [Fact]
    public void Read_parses_all_programme_fields()
    {
        using var s = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.xml"));
        var p = Assert.Single(R(s).Programmes);
        Assert.Equal("tvp1.example", p.ChannelId);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero), p.Start.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), p.Stop!.Value.ToUniversalTime());
        Assert.Equal("Wiadomosci", p.Title);
        Assert.Equal("Wydanie glowne", p.SubTitle);
        Assert.Equal("Opis programu", p.Description);
        Assert.Equal("News", p.Category);
        Assert.Equal("http://img.example.invalid/w.png", p.IconUrl);
    }

    [Fact]
    public void Read_mixed_offsets()
    {
        var plus = One("start=\"20261101120000 +0100\"");
        var utc = One("start=\"20261101120000 +0000\"");
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero), plus.Start.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), utc.Start.ToUniversalTime());
    }

    [Fact]
    public void Read_missing_offset_means_utc_and_negative_offset_works()
    {
        Assert.Equal(TimeSpan.Zero, One("start=\"20261101120000\"").Start.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 14, 30, 0, TimeSpan.Zero),
            One("start=\"20261101120000 -0230\"").Start.ToUniversalTime());
    }

    [Fact]
    public void Read_detects_gzip_by_content()
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes("<tv><channel id=\"a\"><display-name>A</display-name></channel></tv>");
            gz.Write(bytes);
        }
        ms.Position = 0;
        Assert.Equal("a", Assert.Single(R(ms).Channels).Id);
    }

    [Fact]
    public void Read_plain_xml_without_gzip_works() =>
        Assert.Equal("a", Assert.Single(R("<tv><channel id=\"a\"><display-name>A</display-name></channel></tv>").Channels).Id);

    [Fact]
    public void Read_first_title_and_category_win()
    {
        var p = One("start=\"20261101120000\"",
            "<title>First</title><title>Second</title><category>Cat1</category><category>Cat2</category>");
        Assert.Equal("First", p.Title);
        Assert.Equal("Cat1", p.Category);
    }

    [Fact]
    public void Read_missing_stop_gives_null_stop() =>
        Assert.Null(One("start=\"20261101120000\"").Stop);

    [Fact]
    public void Read_bad_stop_gives_null_stop_not_skip() =>
        Assert.Null(One("start=\"20261101120000\" stop=\"junk\"").Stop);

    [Fact]
    public void Read_bad_start_is_skipped_and_counted()
    {
        var r = R("<tv><programme start=\"wrong\" channel=\"c\"><title>X</title></programme>" +
                  "<programme start=\"20261101120000\" channel=\"c\"><title>Y</title></programme></tv>");
        Assert.Equal(1, r.Skipped);
        Assert.Equal("Y", Assert.Single(r.Programmes).Title);
    }

    [Fact]
    public void Read_missing_title_is_skipped_and_counted()
    {
        var r = R("<tv><programme start=\"20261101120000\" channel=\"c\"></programme>" +
                  "<programme start=\"20261101120000\" channel=\"c\"><title></title></programme>" +
                  "<programme start=\"20261101120000\" channel=\"c\"><title>Y</title></programme></tv>");
        Assert.Equal(2, r.Skipped);
        Assert.Single(r.Programmes);
    }

    [Fact]
    public void Read_empty_category_and_non_http_icon_give_null()
    {
        var p = One("start=\"20261101120000\"", "<title>T</title><category></category><icon src=\"file:///c:/x.png\"/>");
        Assert.Null(p.Category);
        Assert.Null(p.IconUrl);
        Assert.Null(One("start=\"20261101120000\"", "<title>T</title><icon src=\"\"/>").IconUrl);
        Assert.Equal("https://h.example.invalid/i.png",
            One("start=\"20261101120000\"", "<title>T</title><icon src=\"https://h.example.invalid/i.png\"/>").IconUrl);
    }

    [Fact]
    public void Read_truncated_xml_throws() =>
        Assert.Throws<XmlException>(() => R("<tv><channel id=\"a\"><display-name>A</display"));

    [Fact]
    public void Read_rejects_dtd() =>
        Assert.Throws<XmlException>(() =>
            R("<!DOCTYPE tv [<!ENTITY x \"y\">]><tv><channel id=\"a\"><display-name>&x;</display-name></channel></tv>"));

    [Fact]
    public void Read_utf8_polish_and_cyrillic()
    {
        var names = Assert.Single(R("<tv><channel id=\"a\"><display-name>TVP 3 Gdańsk</display-name>" +
                                    "<display-name>ViDocTV 4К</display-name></channel></tv>").Channels).DisplayNames;
        Assert.Equal(new[] { "TVP 3 Gdańsk", "ViDocTV 4К" }, names);
    }
}
