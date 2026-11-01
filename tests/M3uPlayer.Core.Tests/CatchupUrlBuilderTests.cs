using Xunit;
using M3uPlayer.Core.Catchup;

namespace M3uPlayer.Core.Tests;

public class CatchupUrlBuilderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Stop = Start.AddHours(1);
    private static readonly string N = CatchupUrlBuilder.FlussonicPlaylistName;

    private static string? B(string? type, string live, string? source = null, DateTimeOffset? now = null) =>
        CatchupUrlBuilder.Build(new CatchupInfo(type, source, 10), live, Start, Stop, now ?? Stop.AddHours(1));

    [Fact]
    public void Build_fs() =>
        Assert.Equal($"http://h/ch/{N}-1793556000-3600.m3u8?token=T", B("fs", "http://h/ch/mono.m3u8?token=T"));

    [Fact]
    public void Build_flussonic_same_as_fs() =>
        Assert.Equal(B("fs", "http://h/ch/mono.m3u8?token=T"), B("Flussonic", "http://h/ch/mono.m3u8?token=T"));

    [Fact]
    public void Build_fs_keeps_ts_extension_and_rejects_no_segment()
    {
        Assert.Equal($"http://h/ch/{N}-1793556000-3600.ts", B("fs", "http://h/ch/x.ts"));
        Assert.Null(B("fs", "http://h"));
    }

    [Fact]
    public void Build_shift()
    {
        Assert.Equal("http://h/ch.m3u8?utc=1793556000&lutc=1793563200", B("shift", "http://h/ch.m3u8"));
        Assert.Equal("http://h/ch.m3u8?a=1&utc=1793556000&lutc=1793563200", B("shift", "http://h/ch.m3u8?a=1"));
    }

    [Fact]
    public void Build_default_replaces_placeholders() =>
        Assert.Equal("http://h/2026/11/01/180000?s=1793556000&e=1793559600&d=3600&x={foo}",
            B("default", "http://live", "http://h/{Y}/{m}/{d}/{H}{M}{S}?s={utc}&e={utcend}&d={duration}&x={foo}"));

    [Fact]
    public void Build_default_aliases_and_missing_source()
    {
        Assert.Equal("http://h/1793556000/1793559600", B("default", "http://live", "http://h/${start}/${end}"));
        Assert.Null(B("default", "http://live"));
        Assert.Null(B("append", "http://live"));
    }

    [Fact]
    public void Build_append() =>
        Assert.Equal("http://live?utc=1793556000", B("append", "http://live", "?utc={utc}"));

    [Fact]
    public void Build_xc()
    {
        Assert.Equal("http://h/timeshift/u/p/60/2026-11-01:18-00/5.m3u8", B("xc", "http://h/live/u/p/5.m3u8"));
        Assert.Equal("http://h/timeshift/u/p/60/2026-11-01:18-00/5.ts", B("xc", "http://h/u/p/5.ts"));
    }

    [Fact]
    public void Build_xc_rounds_minutes_up() =>
        Assert.Equal("http://h/timeshift/u/p/61/2026-11-01:18-00/5.m3u8",
            CatchupUrlBuilder.Build(new CatchupInfo("xc", null, 10), "http://h/live/u/p/5.m3u8", Start, Stop.AddSeconds(1), Stop));

    [Fact]
    public void Build_xc_on_foreign_url_returns_null() => Assert.Null(B("xc", "http://h/ch/mono.m3u8"));

    [Fact]
    public void Build_unknown_type_returns_null()
    {
        Assert.Null(B("bogus", "http://h/ch.m3u8"));
        Assert.Null(B(null, "http://h/ch.m3u8"));
    }

    private static bool Avail(string? type, int days, DateTimeOffset s, DateTimeOffset now) =>
        CatchupUrlBuilder.IsAvailable(new CatchupInfo(type, null, days), s, s.AddHours(1), now);

    [Fact] public void IsAvailable_within_days_true() => Assert.True(Avail("fs", 3, Start, Start.AddDays(2)));
    [Fact] public void IsAvailable_older_than_days_false() => Assert.False(Avail("fs", 3, Start, Start.AddDays(4)));
    [Fact] public void IsAvailable_no_type_false() => Assert.False(Avail(null, 3, Start, Start.AddHours(2)));
    [Fact] public void IsAvailable_zero_days_false() => Assert.False(Avail("fs", 0, Start, Start.AddHours(2)));
    [Fact] public void IsAvailable_future_programme_false() => Assert.False(Avail("fs", 3, Start, Start.AddMinutes(-1)));
    [Fact] public void IsAvailable_programme_in_progress_true() => Assert.True(Avail("fs", 3, Start, Start.AddMinutes(30)));
}
