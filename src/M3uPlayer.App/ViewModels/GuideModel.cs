using M3uPlayer.Core.Epg;
using M3uPlayer.Core.Storage;

namespace M3uPlayer.App.ViewModels;

/// <summary>The guide of the selected channel and the details of the selected programme.</summary>
public sealed class GuideModel(ProgrammeStore programmes, TimeProvider time, TimeZoneInfo tz) : Observable
{
    private const int MaxDaysBack = 30; // the store prunes by retention, so this only bounds the query
    private int _version;

    public BulkCollection<GuideItem> Items { get; } = new();
    public GuideItem? SelectedProgramme { get; private set; }
    public string Message { get; private set; } = "Wybierz kanał, aby zobaczyć ramówkę.";
    public bool HasMessage => Message.Length > 0;

    public bool DetailVisible { get; private set; }
    public string DetailTitle { get; private set; } = "";
    public string DetailMeta { get; private set; } = "";
    public string DetailDescription { get; private set; } = "";
    public string? DetailIconUrl { get; private set; }

    /// <summary>"Oglądaj od początku": shown for programmes that have started, enabled when the archive reaches them.</summary>
    public bool ReplayVisible { get; private set; }
    public bool CanReplay { get; private set; }
    public string ReplayNote { get; private set; } = "";
    public bool HasReplayNote => ReplayNote.Length > 0;
    private string _notice = "";

    /// <summary>Raised on the UI thread with the current programme (or null) once the items are replaced.</summary>
    public event Action<GuideItem?>? Loaded;

    public async Task LoadAsync(Channel channel)
    {
        var version = ++_version;
        var now = time.GetUtcNow();
        var rows = await Task.Run(() => programmes.GetGuide(channel.Key, now.AddDays(-MaxDaysBack), now.AddDays(30)));
        if (version != _version) return;
        var items = GuideFormat.Rows(rows, now, tz).Select(r => new GuideItem(r, tz, channel.Catchup)).ToList();
        foreach (var i in items) i.SetTime(now);
        var current = items.FirstOrDefault(i => i.IsCurrent);
        Items.ReplaceAll(items);
        Message = items.Count == 0 ? "Brak ramówki dla tego kanału." : "";
        Select(current);
        Loaded?.Invoke(current);
    }

    public void SetTime(DateTimeOffset now)
    {
        foreach (var g in Items) g.SetTime(now);
        var before = (ReplayVisible, CanReplay, ReplayNote);
        UpdateReplay();
        if (before != (ReplayVisible, CanReplay, ReplayNote)) Raise();
    }

    /// <summary>A message under the programme details, e.g. when the archive address could not be made.</summary>
    public void ShowNotice(string text)
    {
        _notice = text;
        UpdateReplay();
        Raise();
    }

    private void UpdateReplay()
    {
        var g = SelectedProgramme;
        ReplayVisible = g is { Programme: not null } && (g.IsPast || g.IsCurrent);
        CanReplay = g?.CanReplay ?? false;
        ReplayNote = _notice.Length > 0 ? _notice : ReplayVisible && !CanReplay ? "Archiwum niedostępne dla tego programu" : "";
    }

    public void Select(GuideItem? item)
    {
        if (item is { IsHeader: true }) return;
        SelectedProgramme = item;
        _notice = "";
        UpdateReplay();
        var p = item?.Programme;
        DetailVisible = p is not null;
        DetailTitle = p?.Title ?? "";
        DetailMeta = p is null ? "" : string.Join(" · ", new[]
        {
            $"{GuideFormat.DayLabel(p.Start, time.GetUtcNow(), tz)}, {GuideFormat.Time(p.Start, tz)}–{GuideFormat.Time(p.Stop, tz)}", p.Category,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        DetailDescription = p is null ? "" : string.Join("\n", new[] { p.SubTitle, p.Description }.Where(s => !string.IsNullOrWhiteSpace(s)));
        DetailIconUrl = p?.IconUrl;
        Raise();
    }
}
