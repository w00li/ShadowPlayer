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

    [Theory]
    [InlineData(4, "Today")]
    [InlineData(7, "Saturday, 7 November")]
    public void DayLabel_follows_the_current_culture(int day, string expected)
    {
        using var _ = TestCulture.Use("en");
        Assert.Equal(expected, GuideFormat.DayLabel(new DateTimeOffset(2026, 11, day, 10, 0, 0, TimeSpan.FromHours(2)), Now, Tz));
    }

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

    private static TimeZoneInfo Warsaw()
    {
        foreach (var id in new[] { "Central European Standard Time", "Europe/Warsaw" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        throw new InvalidOperationException("No Warsaw time zone on this machine.");
    }

    [Fact]
    public void Time_and_Rows_follow_the_warsaw_rules_across_the_autumn_change()
    {
        var tz = Warsaw();
        // 2026-10-25: clocks go back from 03:00 to 02:00 in Warsaw (UTC+2 to UTC+1).
        var before = new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero); // 00:30 local, Sunday the 25th
        var after = new DateTimeOffset(2026, 10, 25, 23, 30, 0, TimeSpan.Zero); // 00:30 local on the 26th: UTC+1 now
        Assert.Equal("00:30", GuideFormat.Time(before, tz));
        Assert.Equal("00:30", GuideFormat.Time(after, tz));
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var rows = GuideFormat.Rows([P("a", before), P("b", before.AddHours(1)), P("c", after)], now, tz);
        Assert.Equal(["Dziś", null, null, "Jutro", null], rows.Select(r => r.Header).ToArray());
    }

    [Fact]
    public void DayChanged_is_true_only_when_the_local_date_differs()
    {
        var late = new DateTimeOffset(2026, 11, 4, 21, 59, 0, TimeSpan.Zero); // 22:59 local
        var tz = Warsaw();
        Assert.False(GuideFormat.DayChanged(late, late.AddMinutes(30), tz)); // 23:29 local
        Assert.True(GuideFormat.DayChanged(late, late.AddMinutes(61), tz));  // 00:00 local, next day
        Assert.True(GuideFormat.DayChanged(late.AddDays(1), late, tz));
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
