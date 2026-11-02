using M3uPlayer.Core.Epg;
using M3uPlayer.Core.Storage;
using M3uPlayer.Core.Text;
using Xunit;

namespace M3uPlayer.Core.Tests;

public class GuideFormatTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(2), "t", "t");
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 20, 0, 0, TimeSpan.FromHours(2)); // Wednesday

    private static Programme P(string title, DateTimeOffset start, int minutes = 30) =>
        new(title, null, null, null, null, start, start.AddMinutes(minutes));

    [Theory]
    [InlineData(4, "Dziś")]
    [InlineData(5, "Jutro")]
    [InlineData(3, "Wczoraj")]
    [InlineData(6, "piątek, 6 listopada")]
    [InlineData(1, "niedziela, 1 listopada")]
    public void DayLabel_uses_polish_names(int day, string expected) =>
        Assert.Equal(expected, GuideFormat.DayLabel(new DateTimeOffset(2026, 11, day, 10, 0, 0, TimeSpan.FromHours(2)), Now, Tz));

    [Fact]
    public void DayLabel_uses_local_day_not_utc_day()
    {
        // 23:30 UTC on the 3rd is 01:30 local on the 4th: "Dziś".
        Assert.Equal("Dziś", GuideFormat.DayLabel(new DateTimeOffset(2026, 11, 3, 23, 30, 0, TimeSpan.Zero), Now, Tz));
    }

    [Fact]
    public void Time_is_local_hh_mm() =>
        Assert.Equal("01:05", GuideFormat.Time(new DateTimeOffset(2026, 11, 3, 23, 5, 0, TimeSpan.Zero), Tz));

    [Fact]
    public void Rows_insert_a_header_whenever_the_local_day_changes()
    {
        var rows = GuideFormat.Rows(
            [P("a", Now.AddHours(-22)), P("b", Now.AddHours(-1)), P("c", Now.AddHours(3)), P("d", Now.AddHours(6))], Now, Tz);
        Assert.Equal(["Wczoraj", null, "Dziś", null, null, "Jutro", null],
            rows.Select(r => r.Header).ToArray());
        Assert.Equal(["a", "b", "c", "d"], rows.Where(r => r.Programme is not null).Select(r => r.Programme!.Title).ToArray());
        Assert.Empty(GuideFormat.Rows([], Now, Tz));
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(30, 0.5)]
    [InlineData(60, 1.0)]
    [InlineData(-5, 0.0)]
    [InlineData(90, 1.0)]
    public void Progress_is_clamped(int minutesIn, double expected) =>
        Assert.Equal(expected, GuideFormat.Progress(Now, Now.AddHours(1), Now.AddMinutes(minutesIn)), 6);

    [Fact]
    public void Progress_of_zero_length_programme_is_zero_or_full_without_throwing()
    {
        Assert.Equal(0.0, GuideFormat.Progress(Now, Now, Now.AddMinutes(-1)));
        Assert.Equal(1.0, GuideFormat.Progress(Now, Now, Now.AddMinutes(1)));
        Assert.Equal(0.0, GuideFormat.Progress(Now, Now.AddMinutes(-5), Now));
    }

    [Theory]
    [InlineData("TVP Info", "TI")]
    [InlineData("Polsat", "PO")]
    [InlineData("  ", "?")]
    [InlineData("", "?")]
    [InlineData("!!! tvn 24", "T2")]
    [InlineData("łódź tv", "ŁT")]
    [InlineData("a", "A")]
    public void Initials(string name, string expected) => Assert.Equal(expected, ChannelInitials.From(name));
}
