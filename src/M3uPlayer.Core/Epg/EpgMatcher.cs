using M3uPlayer.Core.Text;

namespace M3uPlayer.Core.Epg;

public class EpgMatcher
{
    private readonly Dictionary<string, string> _byId = new();
    private readonly Dictionary<string, string> _byIdIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Id, string Quality)>> _byBase = new();

    public EpgMatcher(IReadOnlyList<XmltvChannel> channels)
    {
        foreach (var ch in channels)
        {
            _byId.TryAdd(ch.Id, ch.Id);
            _byIdIgnoreCase.TryAdd(ch.Id, ch.Id);
            foreach (var name in ch.DisplayNames.Append(ch.Id))
            {
                var p = TextNormalizer.ParseChannelName(name);
                if (p.Base.Length == 0) continue;
                if (!_byBase.TryGetValue(p.Base, out var list)) _byBase[p.Base] = list = [];
                if (!list.Exists(c => c.Id == ch.Id)) list.Add((ch.Id, p.Quality));
            }
        }
    }

    public string? Match(string? tvgId, string name, string? tvgName)
    {
        if (!string.IsNullOrEmpty(tvgId))
        {
            if (_byId.TryGetValue(tvgId, out var id) || _byIdIgnoreCase.TryGetValue(tvgId, out id)) return id;
        }
        return ByName(name) ?? (string.IsNullOrEmpty(tvgName) ? null : ByName(tvgName));
    }

    private string? ByName(string name)
    {
        var p = TextNormalizer.ParseChannelName(name);
        if (p.Base.Length == 0 || !_byBase.TryGetValue(p.Base, out var list)) return null;
        var same = list.FindIndex(c => c.Quality == p.Quality);
        return list[same < 0 ? 0 : same].Id;
    }
}
