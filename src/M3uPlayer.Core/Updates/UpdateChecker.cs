using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace M3uPlayer.Core.Updates;

/// <param name="Sha256">Lower-case hex, as GitHub's asset digest gives it.</param>
public sealed record UpdateInfo(Version Version, string FileName, Uri Download, string Sha256, Uri ReleasePage);

/// <summary>
/// Finds a newer release of this app on GitHub. Only this repository, only a vX.Y.Z tag, only the installer asset that carries
/// GitHub's SHA-256 digest: anything else is treated as no update, because the file found here is later run as administrator.
/// </summary>
public sealed partial class UpdateChecker(HttpClient http, Version current)
{
    public const string LatestUrl = "https://api.github.com/repos/w00li/ShadowPlayer/releases/latest";
    private const string DownloadPrefix = "https://github.com/w00li/ShadowPlayer/releases/download/";

    [GeneratedRegex(@"^v(\d{1,5})\.(\d{1,5})\.(\d{1,5})$")]
    private static partial Regex Tag();

    [GeneratedRegex("^sha256:([0-9a-f]{64})$")]
    private static partial Regex Digest();

    /// <returns>Null when there is no newer, trustworthy release. Network failures throw, so the caller decides whether to say so.</returns>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ShadowPlayer", current.ToString(3)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct), current);
    }

    public static UpdateInfo? Parse(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "tag_name") is not { } tag || Tag().Match(tag) is not { Success: true } m) return null;
            var version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            if (version <= current) return null;
            if (Str(root, "html_url") is not { } page || !Uri.TryCreate(page, UriKind.Absolute, out var pageUri) || pageUri.Scheme != Uri.UriSchemeHttps) return null;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            var fileName = $"ShadowPlayer-Setup-{version.ToString(3)}.exe";
            foreach (var a in assets.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object || Str(a, "name") != fileName) continue;
                if (Str(a, "digest") is not { } digest || Digest().Match(digest) is not { Success: true } d) return null;
                if (Str(a, "browser_download_url") is not { } url || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)
                    || !Uri.TryCreate(url, UriKind.Absolute, out var download)) return null;
                return new UpdateInfo(version, fileName, download, d.Groups[1].Value, pageUri);
            }
            return null;
        }
        catch (JsonException) { return null; } // a malformed answer is no update, like any other surprise
    }

    /// <summary>
    /// The verified installer in <paramref name="dir"/>. A file already there is hashed again rather than trusted. A wrong hash
    /// (InvalidDataException), a network failure or a cancel leaves no file behind.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo u, string dir, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, u.FileName);
        if (File.Exists(path) && Verify(path, u.Sha256)) { progress?.Report(1); return path; }
        var part = path + ".part";
        try
        {
            using (var response = await GetFollowingRedirects(u.Download, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long done = 0;
                for (int n; (n = await source.ReadAsync(buffer, ct)) > 0;)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(1, (double)done / total.Value));
                }
            }
            if (!Verify(part, u.Sha256)) throw new InvalidDataException("The downloaded installer does not match the release's SHA-256.");
            File.Move(part, path, overwrite: true);
            progress?.Report(1);
            return path;
        }
        finally
        {
            try { File.Delete(part); } catch (IOException) { } // gone already after the move
        }
    }

    // GitHub sends the file from another host; followed here so it never depends on the handler, and only over https.
    private async Task<HttpResponseMessage> GetFollowingRedirects(Uri url, CancellationToken ct)
    {
        for (var hops = 0; ; hops++)
        {
            var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } next) return response;
            response.Dispose();
            url = next.IsAbsoluteUri ? next : new Uri(url, next);
            if (hops >= 5 || url.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException("The download was redirected too often or away from https.");
        }
    }

    public static bool Verify(string path, string sha256)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file)) == sha256;
    }

    [GeneratedRegex(@"^ShadowPlayer-Setup-(\d{1,5})\.(\d{1,5})\.(\d{1,5})\.exe$")]
    private static partial Regex InstallerName();

    /// <summary>Removes everything in the folder except an installer newer than the running version (verified again before it runs).</summary>
    public static void Clean(string dir, Version current)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var m = InstallerName().Match(Path.GetFileName(file));
            var keep = m.Success && new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) > current;
            if (!keep) try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
