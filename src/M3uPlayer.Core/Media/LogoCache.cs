using System.Security.Cryptography;
using System.Text;

namespace M3uPlayer.Core.Media;

/// <summary>
/// Disk cache of channel logos. Playlist content is untrusted: http/https only, images only, at most 2 MB, at most 4 downloads at once.
/// A download that stalls is given up after 30 s so it cannot hold a slot for good.
/// Callers for one URL share one download; a URL that failed is not tried again for an hour.
/// </summary>
public sealed class LogoCache(HttpClient http, string dir, TimeProvider? time = null, TimeSpan? downloadTimeout = null)
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _timeout = downloadTimeout ?? TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _slots = new(4);
    private readonly object _lock = new();
    private readonly Dictionary<string, Task<string?>> _inFlight = [];
    private readonly Dictionary<string, DateTimeOffset> _failed = [];

    /// <summary>Removes the cached files and forgets failed downloads, so every logo is fetched again. Files in use are skipped.</summary>
    public void Clear()
    {
        lock (_lock) _failed.Clear();
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir))
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <returns>The cached file's path, or null when there is no usable logo.</returns>
    public async Task<string?> GetAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return null;
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) return path;

        Task<string?> work;
        lock (_lock)
        {
            if (_failed.TryGetValue(name, out var at))
            {
                if (_time.GetUtcNow() - at < RetryAfter) return null;
                _failed.Remove(name);
            }
            if (!_inFlight.TryGetValue(name, out work!))
                _inFlight[name] = work = RunAsync(name, uri, path);
        }
        // The download belongs to every caller, so one caller leaving must not cancel it.
        return await work.WaitAsync(ct);
    }

    private async Task<string?> RunAsync(string name, Uri uri, string path)
    {
        await Task.Yield(); // the caller registers this task under the lock first; a synchronous finish would be registered after its own cleanup
        string? result = null;
        try
        {
            await _slots.WaitAsync();
            try { result = File.Exists(path) ? path : await DownloadAsync(uri, path); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException) { }
            finally { _slots.Release(); }
        }
        finally
        {
            lock (_lock)
            {
                _inFlight.Remove(name);
                if (result is null) _failed[name] = _time.GetUtcNow();
            }
        }
        return result;
    }

    private async Task<string?> DownloadAsync(Uri uri, string path)
    {
        using var timeout = new CancellationTokenSource(_timeout);
        var ct = timeout.Token;
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
            || response.Content.Headers.ContentLength > MaxBytes) return null;

        Directory.CreateDirectory(dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".part";
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
