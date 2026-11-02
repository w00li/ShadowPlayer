using M3uPlayer.Core.Storage;

namespace M3uPlayer.Core.Epg;

/// <summary>A day header (Programme null) or a programme (Header null) in a guide list.</summary>
public record GuideRow(string? Header, Programme? Programme);

public static class GuideFormat
{
    // Fixed tables: the labels must not depend on the machine's culture.
    private static readonly string[] Days = ["niedziela", "poniedziałek", "wtorek", "środa", "czwartek", "piątek", "sobota"];

    private static readonly string[] Months =
    [
        "stycznia", "lutego", "marca", "kwietnia", "maja", "czerwca", "lipca", "sierpnia", "września", "października", "listopada", "grudnia",
    ];

    public static string Time(DateTimeOffset t, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(t, tz).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public static string DayLabel(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo tz)
    {
        var day = TimeZoneInfo.ConvertTime(t, tz);
        var diff = (day.Date - TimeZoneInfo.ConvertTime(now, tz).Date).Days;
        return diff switch
        {
            0 => "Dziś",
            1 => "Jutro",
            -1 => "Wczoraj",
            _ => $"{Days[(int)day.DayOfWeek]}, {day.Day} {Months[day.Month - 1]}",
        };
    }

    /// <summary>Programmes in the given order with a header before the first one of each local day.</summary>
    public static IReadOnlyList<GuideRow> Rows(IReadOnlyList<Programme> programmes, DateTimeOffset now, TimeZoneInfo tz)
    {
        var rows = new List<GuideRow>(programmes.Count + 8);
        DateTime? day = null;
        foreach (var p in programmes)
        {
            var d = TimeZoneInfo.ConvertTime(p.Start, tz).Date;
            if (d != day) { rows.Add(new GuideRow(DayLabel(p.Start, now, tz), null)); day = d; }
            rows.Add(new GuideRow(null, p));
        }
        return rows;
    }

    /// <summary>0 to 1; a programme with no length counts as not started before its time and finished after.</summary>
    public static double Progress(DateTimeOffset start, DateTimeOffset stop, DateTimeOffset now)
    {
        if (stop <= start) return now >= stop && now > start ? 1 : 0;
        return Math.Clamp((now - start).TotalSeconds / (stop - start).TotalSeconds, 0, 1);
    }
}
