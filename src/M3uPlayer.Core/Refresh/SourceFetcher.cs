using System.Globalization;
using M3uPlayer.Core.Storage;

namespace M3uPlayer.Core.Refresh;

public record FetchResult(bool NotModified, string? TempFilePath, string? ETag, string? LastModified);

/// <summary>Downloads a source (URL or local file) to a temp file. The caller owns, and must delete, the returned file.</summary>
public class SourceFetcher(HttpClient http, IReadOnlyList<TimeSpan> retryDelays, Func<TimeSpan, CancellationToken, Task> delay,
    string tempDir, long maxBytes = 500L * 1024 * 1024, TimeSpan? idleTimeout = null)
{
    private readonly TimeSpan _idle = idleTimeout ?? TimeSpan.FromSeconds(60);

    public static bool IsHttp(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    public async Task<FetchResult> FetchAsync(Source source, CancellationToken ct)
    {
        if (IsHttp(source.Location)) return await FetchHttpAsync(source, ct);
        if (Path.IsPathRooted(source.Location) && !source.Location.Contains("://")) return await FetchFileAsync(source, ct);
        throw new InvalidOperationException("Nieobsługiwany adres źródła. Usuń to źródło i dodaj je ponownie.");
    }

    private async Task<FetchResult> FetchHttpAsync(Source source, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await DownloadAsync(source, ct); }
            catch (Exception ex) when (attempt < retryDelays.Count && Retryable(ex))
            {
                await delay(retryDelays[attempt], ct);
            }
        }
    }

    // Permanent client errors would only burn minutes of delays.
    private static bool Retryable(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } sc } => (int)sc is < 400 or >= 500 || (int)sc is 408 or 425 or 429,
        HttpRequestException or IOException or TimeoutException => true,
        _ => false,
    };

    private async Task<FetchResult> DownloadAsync(Source source, CancellationToken callerCt)
    {
        // HttpClient.Timeout stops at the headers, so a stalled body needs its own idle timer, reset by every read.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        idle.CancelAfter(_idle);
        try { return await DownloadCoreAsync(source, idle); }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            throw new TimeoutException("Serwer nie odpowiada. Spróbujemy ponownie o następnej porze odświeżania.");
        }
    }

    private async Task<FetchResult> DownloadCoreAsync(Source source, CancellationTokenSource idle)
    {
        var ct = idle.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Location);
        if (source.ETag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", source.ETag);
        if (source.LastModified is not null) request.Headers.TryAddWithoutValidation("If-Modified-Since", source.LastModified);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified) return new FetchResult(true, null, source.ETag, source.LastModified);
        // Messages deliberately omit the URL: it carries credentials.
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Serwer odpowiedział błędem HTTP {(int)response.StatusCode}. Spróbujemy ponownie o następnej porze odświeżania.", null, response.StatusCode);
        if (response.RequestMessage?.RequestUri is { Scheme: not ("http" or "https") })
            throw new HttpRequestException("Serwer przekierował na nieobsługiwany adres. Sprawdź adres źródła.");

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var path = await CopyToTempAsync(body, ct, idle);
        return new FetchResult(false, path, response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified?.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
    }

    private async Task<FetchResult> FetchFileAsync(Source source, CancellationToken ct)
    {
        var info = new FileInfo(source.Location);
        if (!info.Exists) throw new FileNotFoundException("Nie znaleziono pliku. Sprawdź, czy nie został przeniesiony lub usunięty.");
        var modified = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
        if (source.LastSuccess is not null && modified == source.LastModified) return new FetchResult(true, null, null, modified);
        FileStream input;
        try { input = info.OpenRead(); }
        // OS messages carry the full path (home folder, provider file name), so they are replaced.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Nie można odczytać pliku. Sprawdź, czy inny program go nie używa i czy masz do niego dostęp.");
        }
        await using (input) return new FetchResult(false, await CopyToTempAsync(input, ct, null), null, modified);
    }

    private async Task<string> CopyToTempAsync(Stream input, CancellationToken ct, CancellationTokenSource? idle)
    {
        var path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            FileStream created;
            try { Directory.CreateDirectory(tempDir); created = File.Create(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Nie można zapisać pliku tymczasowego. Sprawdź wolne miejsce na dysku i uprawnienia.");
            }
            await using (var output = created)
            {
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    idle?.CancelAfter(_idle);
                    total += n;
                    if (total > maxBytes) throw new InvalidDataException("Źródło jest większe niż dozwolone 500 MB, więc go nie wczytano.");
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch (Exception) { }
            throw;
        }
    }
}
