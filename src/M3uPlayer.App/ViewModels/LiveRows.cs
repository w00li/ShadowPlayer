using M3uPlayer.Core.Playlist;
using M3uPlayer.Core.Localization;
using M3uPlayer.Core.Catchup;
using M3uPlayer.Core.Epg;
using M3uPlayer.Core.Storage;
using M3uPlayer.Core.Text;

namespace M3uPlayer.App.ViewModels;

public sealed class CategoryItem(string key, string name, int count) : Observable
{
    public const string AllKey = SessionRestore.AllKey, FavoritesKey = SessionRestore.FavoritesKey;

    public string Key { get; } = key;
    public string Name { get; } = name == M3uParser.DefaultCategory ? Strings.Category_Other : name;
    public int Count { get; private set; } = count;
    public string CountText => Count.ToString();
    public string AutomationName => $"{Name}, {Count}";

    public void SetCount(int count)
    {
        if (count == Count) return;
        Count = count;
        Raise();
    }
}

/// <summary>One row of the channel list. Updated in place, so recycled containers and focus survive a refresh.</summary>
public sealed class ChannelRow : Observable
{
    public ChannelRow(Channel channel, bool programmeMatch)
    {
        Channel = channel;
        ProgrammeMatch = programmeMatch;
        Initials = ChannelInitials.From(channel.Name);
    }

    public Channel Channel { get; private set; }
    public string Key => Channel.Key;
    public string Name => Channel.Name;

    public override string ToString() => AutomationName; // the list item's accessible name
    public string Initials { get; }
    public string? LogoUrl => Channel.Logo;

    /// <summary>Found by a programme title only, not by its name (search).</summary>
    public bool ProgrammeMatch { get; }

    public bool IsFavorite { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool Hover { get; set; }
    public bool StarFocus { get; set; }

    public bool HasNow { get; private set; }
    public bool NoEpg { get; private set; }
    public bool HasNext { get; private set; }
    public string NowTitle { get; private set; } = "";
    public string NextText { get; private set; } = "";
    public double Progress { get; private set; }

    public double StarOpacity => IsFavorite || Hover || StarFocus ? 1 : 0;
    public string StarGlyph => IsFavorite ? "" : "";
    public string StarTip => IsFavorite ? Strings.Live_RemoveFavorite : Strings.Live_AddFavorite;
    public string StarName => $"{StarTip}: {Name}";

    public string AutomationName =>
        $"{Name}{(HasNow ? ". Teraz: " + NowTitle : NoEpg ? ". Brak EPG" : "")}{(IsPlaying ? ". Odtwarzany" : "")}";

    public void Update(Channel channel, bool playing)
    {
        Channel = channel;
        IsFavorite = channel.IsFavorite;
        IsPlaying = playing;
        Raise();
    }

    public void CopyEpgFrom(ChannelRow o)
    {
        (HasNow, NoEpg, HasNext, NowTitle, NextText, Progress) = (o.HasNow, o.NoEpg, o.HasNext, o.NowTitle, o.NextText, o.Progress);
    }

    public void SetFavorite(bool on) { IsFavorite = on; Channel = Channel with { IsFavorite = on }; Raise(); }
    public void SetPlaying(bool on) { if (IsPlaying != on) { IsPlaying = on; Raise(); } }
    public void Refresh() => Raise();

    public void SetNowNext(NowNext nn, DateTimeOffset now, TimeZoneInfo tz, string? matchText)
    {
        HasNow = nn.Now is not null;
        HasNext = nn.Next is not null || matchText is not null;
        NoEpg = nn.Now is null && nn.Next is null && matchText is null;
        NowTitle = nn.Now?.Title ?? "";
        Progress = nn.Now is { } p ? GuideFormat.Progress(p.Start, p.Stop, now) : 0;
        NextText = matchText ?? (nn.Next is { } n ? string.Format(Strings.Live_Next, GuideFormat.Time(n.Start, tz), n.Title) : "");
        Raise();
    }
}

/// <summary>A day header or a programme in the guide list.</summary>
public sealed class GuideItem : Observable
{
    private readonly CatchupInfo _catchup;

    public GuideItem(GuideRow row, TimeZoneInfo tz, CatchupInfo catchup)
    {
        _catchup = catchup;
        Programme = row.Programme;
        Header = row.Header ?? "";
        IsHeader = row.Header is not null;
        TimeText = Programme is { } p ? GuideFormat.Time(p.Start, tz) : "";
    }

    public Programme? Programme { get; }
    public string Header { get; }
    public bool IsHeader { get; }
    public bool IsProgramme => !IsHeader;
    public double RowHeight => IsHeader ? 36 : 0;
    public string TimeText { get; }
    public string Title => Programme?.Title ?? "";
    public string? SubTitle => Programme?.SubTitle;
    public bool IsCurrent { get; private set; }
    public bool IsPast { get; private set; }
    public double Progress { get; private set; }
    public Windows.UI.Text.FontWeight TitleWeight => IsCurrent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    /// <summary>Started already and the archive reaches back to it.</summary>
    public bool CanReplay { get; private set; }
    public bool ReplayMarker => CanReplay && IsPast;
    public double TextOpacity => IsPast && !CanReplay ? 0.55 : 1;
    public override string ToString() => AutomationName;
    public string AutomationName => IsHeader ? Header : $"{TimeText} {Title}{(IsCurrent ? Strings.Live_Now : IsPast ? Strings.Live_Past : "")}{(ReplayMarker ? Strings.Live_Replayable : "")}";

    public void SetTime(DateTimeOffset now)
    {
        if (Programme is not { } p) return;
        var (current, past, progress) = (p.Start <= now && now < p.Stop, p.Stop <= now, GuideFormat.Progress(p.Start, p.Stop, now));
        var replay = CatchupUrlBuilder.IsAvailable(_catchup, p.Start, now);
        if (current == IsCurrent && past == IsPast && replay == CanReplay && Math.Abs(progress - Progress) < 0.005) return;
        (IsCurrent, IsPast, Progress, CanReplay) = (current, past, progress, replay);
        Raise();
    }
}
