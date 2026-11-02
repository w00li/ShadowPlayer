using System.Globalization;
using M3uPlayer.Core.Media;
using M3uPlayer.Core.Storage;

namespace M3uPlayer.Core.Epg;

/// <summary>What the video area shows for a player state.</summary>
public record OverlayInfo(bool Visible, string Text, bool Spinner, bool Button, string ButtonText, double Dim)
{
    /// <param name="spinnerArmed">Opening has lasted long enough that the spinner may show.</param>
    /// <param name="archive">Playing a past programme: Idle then means it ended.</param>
    public static OverlayInfo For(PlayerState state, bool spinnerArmed, string? lastError, string? channelName, bool archive = false) => state switch
    {
        PlayerState.Opening => new(spinnerArmed, "", true, false, "", 0.45), // the previous channel's last frame is still behind
        PlayerState.Reconnecting => new(true, "Łączenie ponownie…", true, false, "", 1),
        PlayerState.Failed => new(true, lastError ?? "Nie udało się odtworzyć kanału.", false, true, "Spróbuj ponownie", 1),
        PlayerState.Idle when archive => new(true, "Program się skończył", false, true, "Wróć na żywo", 1),
        PlayerState.Idle when channelName is not null => new(true, channelName, false, true, "Oglądaj", 1),
        PlayerState.Idle => new(true, "Wybierz kanał z listy", false, false, "", 1),
        _ => new(false, "", false, false, "", 1),
    };
}

public static class LiveText
{
    /// <summary>The line under a channel found by a programme title; the day is shown only when it is not today.</summary>
    public static string SearchMatch(Programme p, DateTimeOffset now, TimeZoneInfo tz)
    {
        var start = TimeZoneInfo.ConvertTime(p.Start, tz);
        var today = start.Date == TimeZoneInfo.ConvertTime(now, tz).Date;
        var day = today ? "" : start.ToString("d.MM", CultureInfo.InvariantCulture) + " ";
        return $"Znaleziono: {day}{GuideFormat.Time(p.Start, tz)} {p.Title}";
    }
}

public static class ChannelStep
{
    /// <summary>Index after moving <paramref name="delta"/> from <paramref name="current"/> (-1 when nothing is selected); -1 when there is no such channel.</summary>
    public static int Index(int count, int current, int delta)
    {
        if (count == 0) return -1;
        var next = current < 0 ? (delta > 0 ? 0 : count - 1) : current + delta;
        return next >= 0 && next < count ? next : -1;
    }
}
