using System.Security.Cryptography;
using System.Text;

namespace M3uPlayer.Core.Media;

/// <summary>Disk cache of channel logos. Playlist content is untrusted: http/https only, images only, at most 2 MB, at most 4 downloads at once.</summary>
public sealed class LogoCache(HttpClient http, string dir)
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private readonly SemaphoreSlim _slots = new(4);

    /// <returns>The cached file's path, or null when there is no usable logo.</returns>
    public async Task<string?> GetAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return null;
        var path = Path.Combine(dir, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))));
        if (File.Exists(path)) return path;

        await _slots.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return path;
            return await DownloadAsync(uri, path, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException) { return null; }
        finally { _slots.Release(); }
    }

    private async Task<string?> DownloadAsync(Uri uri, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
            || response.Content.Headers.ContentLength > MaxBytes) return null;

        Directory.CreateDirectory(dir);
        var tmp = path + ".part";
        try
        {
            await using (var file = File.Create(tmp))
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[16 * 1024];
                var total = 0;
                int n;
                while ((n = await body.ReadAsync(buffer, ct)) > 0)
                {
                    if ((total += n) > MaxBytes) return null; // the header may lie or be absent
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            File.Move(tmp, path, true);
            return path;
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }
}
