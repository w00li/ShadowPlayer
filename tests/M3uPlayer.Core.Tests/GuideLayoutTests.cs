using M3uPlayer.Core.Epg;
using Xunit;

namespace M3uPlayer.Core.Tests;

public class GuideLayoutTests
{
    [Theory]
    [InlineData(210, 84)]   // 900 x 600 window: the description takes 40%, the list keeps the rest
    [InlineData(100, 72)]   // never below what shows a title and a line
    [InlineData(800, 190)]  // tall windows: as before
    public void Description_never_takes_more_than_its_share(double card, double expected) =>
        Assert.Equal(expected, GuideLayout.DetailMaxHeight(card));

    [Theory]
    [InlineData(259, false)]
    [InlineData(260, true)]
    public void Picture_shows_only_with_room(double card, bool expected) =>
        Assert.Equal(expected, GuideLayout.RoomForPicture(card));

    [Theory]
    [InlineData(100, 0)]  // two rows: the current programme goes to the top
    [InlineData(140, 1)]  // three rows
    [InlineData(400, 2)]  // plenty of room: two programmes of the past, as before
    public void Past_rows_above_the_current_one_fit_the_list(double listHeight, int expected) =>
        Assert.Equal(expected, GuideLayout.PastRowsAbove(listHeight));
}
