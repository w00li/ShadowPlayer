using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace M3uPlayer.Core.Epg;

public record XmltvChannel(string Id, IReadOnlyList<string> DisplayNames);

public record XmltvProgramme(
    string ChannelId, DateTimeOffset Start, DateTimeOffset? Stop, string Title,
    string? SubTitle, string? Description, string? Category, string? IconUrl);

public static class XmltvReader
{
    /// <summary>Streams channels and programmes to the callbacks; returns the number of skipped programmes.</summary>
    public static int Read(Stream stream, Action<XmltvChannel> onChannel, Action<XmltvProgramme> onProgramme)
    {
        // shortcut: gzip sniffing seeks back, so only seekable streams work, upgrade to a prefix-replaying stream when a caller must read a network stream directly.
        if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));
        var b0 = stream.ReadByte();
        var b1 = stream.ReadByte();
        stream.Seek(-(b1 >= 0 ? 2 : b0 >= 0 ? 1 : 0), SeekOrigin.Current);
        using var input = b0 == 0x1F && b1 == 0x8B ? new GZipStream(stream, CompressionMode.Decompress, true) : stream;

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, CheckCharacters = false };
        using var r = XmlReader.Create(new ControlFilter(input), settings);
        var skipped = 0;
        r.MoveToContent();
        r.Read();
        while (!r.EOF)
        {
            if (r.NodeType == XmlNodeType.Element && r.Depth == 1 && r.Name is "channel" or "programme")
            {
                var isChannel = r.Name == "channel";
                var el = (XElement)XNode.ReadFrom(r);
                if (isChannel) { if (ToChannel(el) is { } c) onChannel(c); }
                else if (ToProgramme(el) is { } p) onProgramme(p);
                else skipped++;
            }
            else r.Read();
        }
        return skipped;
    }

    /// <summary>
    /// The parser rejects raw control bytes (illegal in XML 1.0) even with CheckCharacters off, and one stray byte would fail the whole guide; they become spaces.
    /// shortcut: byte-wise, so only for ASCII-compatible encodings; a UTF-16 stream (byte order mark) passes through untouched.
    /// </summary>
    private sealed class ControlFilter(Stream inner) : Stream
    {
        private bool _first = true, _wide;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            if (_first && n >= 2) { _first = false; _wide = buffer[0] == 0xFF && buffer[1] == 0xFE || buffer[0] == 0xFE && buffer[1] == 0xFF; }
            if (!_wide)
                for (var i = 0; i < n; i++)
                    if (buffer[i] < 0x20 && buffer[i] is not (0x09 or 0x0A or 0x0D)) buffer[i] = 0x20;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static XmltvChannel? ToChannel(XElement el)
    {
        var id = (string?)el.Attribute("id");
        if (string.IsNullOrEmpty(id)) return null;
        var names = el.Elements("display-name").Select(e => e.Value.Trim()).Where(n => n.Length > 0).ToList();
        return new XmltvChannel(id, names);
    }

    private static XmltvProgramme? ToProgramme(XElement el)
    {
        var channel = (string?)el.Attribute("channel");
        var title = Text(el, "title");
        if (string.IsNullOrEmpty(channel) || title is null || ParseTime((string?)el.Attribute("start")) is not { } start)
            return null;
        var icon = (string?)el.Element("icon")?.Attribute("src");
        var iconOk = Uri.TryCreate(icon, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
        return new XmltvProgramme(channel, start, ParseTime((string?)el.Attribute("stop")), title,
            Text(el, "sub-title"), Text(el, "desc"), Text(el, "category"), iconOk ? icon : null);
    }

    private static string? Text(XElement el, string name) =>
        el.Element(name)?.Value.Trim() is { Length: > 0 } v ? v : null;

    private static DateTimeOffset? ParseTime(string? s)
    {
        if (s is null) return null;
        s = s.Trim();
        if (s.Length < 14 || !DateTime.TryParseExact(s[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local)) return null;
        var rest = s[14..].Trim();
        var offset = TimeSpan.Zero;
        if (rest.Length > 0)
        {
            if (rest.Length != 5 || rest[0] is not ('+' or '-')
                || !int.TryParse(rest[1..3], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                || !int.TryParse(rest[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
                || m > 59 || h > 14) return null;
            offset = new TimeSpan(h, m, 0) * (rest[0] == '-' ? -1 : 1);
        }
        return new DateTimeOffset(local, offset);
    }
}
