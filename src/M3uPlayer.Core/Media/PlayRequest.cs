namespace M3uPlayer.Core.Media;

public record PlayRequest(string Url, string? UserAgent, string? Referrer, bool IsLive)
{
    // The URL carries credentials, so the generated ToString must not leak it into logs or the debugger.
    public override string ToString() => $"PlayRequest {{ IsLive = {IsLive} }}";

    /// <summary>A header value safe to pass as a media option: null when empty or containing any control character.</summary>
    public static string? HeaderValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ? null : value;
}
