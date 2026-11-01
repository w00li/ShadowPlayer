using System.Globalization;
using M3uPlayer.Core.Storage;

namespace M3uPlayer.Core.Refresh;

public record FetchResult(bool NotModified, string? TempFilePath, string? ETag, string? LastModified);

/// <summary>Downloads a source (URL or local file) to a temp file. The caller owns, and must delete, the returned file.</summary>
public class SourceFetcher(HttpClient http, IReadOnlyList<TimeSpan> retryDelays, Func<TimeSpan, CancellationToken, Task> delay,
    string tempDir, long maxBytes = 500L * 1024 * 1024)
{
    public static bool IsHttp(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    public async Task<FetchResult> FetchAsync(Source source, CancellationToken ct)
    {
        if (IsHttp(source.Location)) return await FetchHttpAsync(source, ct);
        if (Path.IsPathRooted(source.Location) && !source.Location.Contains("://")) return await FetchFileAsync(source, ct);
        throw new InvalidOperationException("Unsupported source location.");
    }

    private async Task<FetchResult> FetchHttpAsync(Source source, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await DownloadAsync(source, ct); }
            // A timeout surfaces as TaskCanceledException while ct is not cancelled; that is retryable like any network error.
            catch (Exception ex) when (attempt < retryDelays.Count && !ct.IsCancellationRequested
                && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                await delay(retryDelays[attempt], ct);
            }
        }
    }

    private async Task<FetchResult> DownloadAsync(Source source, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Location);
        if (source.ETag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", source.ETag);
        if (source.LastModified is not null) request.Headers.TryAddWithoutValidation("If-Modified-Since", source.LastModified);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified) return new FetchResult(true, null, source.ETag, source.LastModified);
        // Messages deliberately omit the URL: it carries credentials.
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        if (response.RequestMessage?.RequestUri is { Scheme: not ("http" or "https") })
            throw new HttpRequestException("The server redirected to an unsupported address.");

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var path = await CopyToTempAsync(body, ct);
        return new FetchResult(false, path, response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified?.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
    }

    private async Task<FetchResult> FetchFileAsync(Source source, CancellationToken ct)
    {
        var info = new FileInfo(source.Location);
        if (!info.Exists) throw new FileNotFoundException("The local source file was not found.");
        var modified = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
        if (source.LastSuccess is not null && modified == source.LastModified) return new FetchResult(true, null, null, modified);
        await using var input = info.OpenRead();
        return new FetchResult(false, await CopyToTempAsync(input, ct), null, modified);
    }

    private async Task<string> CopyToTempAsync(Stream input, CancellationToken ct)
    {
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = File.Create(path))
            {
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > maxBytes) throw new InvalidDataException("The source is larger than the allowed size.");
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }
}
