using M3uPlayer.Core.Epg;
using M3uPlayer.Core.Media;
using M3uPlayer.Core.Storage;
using Xunit;

namespace M3uPlayer.Core.Tests;

public class LiveLogicTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(2), "t", "t");
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 20, 0, 0, TimeSpan.FromHours(2));

    private static Programme P(DateTimeOffset start, string title) => new(title, null, null, null, null, start, start.AddHours(1));

    [Fact]
    public void SearchMatch_today_has_time_only()
    {
        Assert.Equal("Znaleziono: 21:00 Wiadomości", LiveText.SearchMatch(P(Now.AddHours(1), "Wiadomości"), Now, Tz));
    }

    [Fact]
    public void SearchMatch_other_day_has_short_date_in_invariant_format()
    {
        Assert.Equal("Znaleziono: 6.11 21:00 Film", LiveText.SearchMatch(P(Now.AddDays(2).AddHours(1), "Film"), Now, Tz));
    }

    [Theory]
    [InlineData(PlayerState.Idle, false, null, null, true, "Wybierz kanał z listy", false, false, "")]
    [InlineData(PlayerState.Idle, false, null, "TVP", true, "TVP", false, true, "Oglądaj")]
    [InlineData(PlayerState.Opening, false, null, "TVP", false, "", true, false, "")]
    [InlineData(PlayerState.Opening, true, null, "TVP", true, "", true, false, "")]
    [InlineData(PlayerState.Reconnecting, false, null, "TVP", true, "Łączenie ponownie…", true, false, "")]
    [InlineData(PlayerState.Failed, false, "Błąd X", "TVP", true, "Błąd X", false, true, "Spróbuj ponownie")]
    [InlineData(PlayerState.Failed, false, null, "TVP", true, "Nie udało się odtworzyć kanału.", false, true, "Spróbuj ponownie")]
    [InlineData(PlayerState.Playing, false, null, "TVP", false, "", false, false, "")]
    [InlineData(PlayerState.Paused, false, null, "TVP", false, "", false, false, "")]
    public void Overlay_maps_state(PlayerState state, bool armed, string? error, string? channel, bool visible, string text, bool spinner, bool button, string buttonText)
    {
        var o = OverlayInfo.For(state, armed, error, channel);
        Assert.Equal((visible, text, spinner, button, buttonText), (o.Visible, o.Text, o.Spinner, o.Button, o.ButtonText));
    }

    [Fact]
    public void Overlay_dims_only_while_opening() =>
        Assert.True(OverlayInfo.For(PlayerState.Opening, true, null, "x").Dim < OverlayInfo.For(PlayerState.Failed, false, "e", "x").Dim);

    [Theory]
    [InlineData(0, -1, 1, -1)]
    [InlineData(5, -1, 1, 0)]
    [InlineData(5, -1, -1, 4)]
    [InlineData(5, 2, 1, 3)]
    [InlineData(5, 4, 1, -1)]
    [InlineData(5, 0, -1, -1)]
    public void ChannelStep_index(int count, int current, int delta, int expected) =>
        Assert.Equal(expected, ChannelStep.Index(count, current, delta));
}
