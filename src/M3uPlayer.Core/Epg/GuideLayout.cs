namespace M3uPlayer.Core.Epg;

/// <summary>How the guide card shares its height: the programme list comes first, the description gets a share and scrolls.</summary>
public static class GuideLayout
{
    public const double RowHeight = 36, DetailMin = 72, DetailMax = 190, DetailShare = 0.4, PictureMinCard = 260;

    public static double DetailMaxHeight(double cardHeight) => Math.Clamp(cardHeight * DetailShare, DetailMin, DetailMax);

    /// <summary>On a short card the programme picture would push the text and the replay button out of view.</summary>
    public static bool RoomForPicture(double cardHeight) => cardHeight >= PictureMinCard;

    /// <summary>Programmes of the past kept in view above the current one: two when the list has room, fewer on a short list, so the current one is never cut off.</summary>
    public static int PastRowsAbove(double listHeight) => Math.Clamp(((int)(listHeight / RowHeight) - 1) / 2, 0, 2);
}
