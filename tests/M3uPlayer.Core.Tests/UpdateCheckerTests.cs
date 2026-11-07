using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using M3uPlayer.Core.Updates;
using Xunit;

namespace M3uPlayer.Core.Tests;

public sealed class UpdateCheckerTests : IDisposable
{
    private static readonly Version Current = new(1, 0, 0);
    private const string Repo = "https://github.com/w00li/ShadowPlayer/releases/download/";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"updates-{Guid.NewGuid():N}");

    public UpdateCheckerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string Release(string tag = "v1.1.0", string? name = null, string? digest = null, string? url = null, bool asset = true)
    {
        var version = tag.TrimStart('v');
        var assets = asset
            ? new object[] { new { name = name ?? $"ShadowPlayer-Setup-{version}.exe", digest = digest ?? "sha256:" + Sha("abc"),
                browser_download_url = url ?? $"{Repo}{tag}/ShadowPlayer-Setup-{version}.exe" } }
            : [];
        return JsonSerializer.Serialize(new { tag_name = tag, html_url = $"https://github.com/w00li/ShadowPlayer/releases/tag/{tag}", assets });
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return respond(request);
        }
    }

    private static HttpResponseMessage Text(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body) };

    // ---- finding ----

    [Theory]
    [InlineData("v1.0.1", true)]
    [InlineData("v1.1.0", true)]
    [InlineData("v2.0.0", true)]
    [InlineData("v1.0.0", false)]
    [InlineData("v0.9.9", false)]
    [InlineData("1.0.1", false)]
    [InlineData("v1.0", false)]
    [InlineData("v1.0.1-beta", false)]
    public void Only_a_greater_vX_Y_Z_tag_is_an_update(string tag, bool update) =>
        Assert.Equal(update, UpdateChecker.Parse(Release(tag, name: $"ShadowPlayer-Setup-{tag.TrimStart('v')}.exe"), Current) is not null);

    [Fact]
    public void The_installer_asset_is_picked_and_described()
    {
        var u = UpdateChecker.Parse(Release(), Current)!;
        Assert.Equal(new Version(1, 1, 0), u.Version);
        Assert.Equal("ShadowPlayer-Setup-1.1.0.exe", u.FileName);
        Assert.Equal(new Uri(Repo + "v1.1.0/ShadowPlayer-Setup-1.1.0.exe"), u.Download);
        Assert.Equal(Sha("abc"), u.Sha256);
        Assert.Equal(new Uri("https://github.com/w00li/ShadowPlayer/releases/tag/v1.1.0"), u.ReleasePage);
    }

    [Theory]
    [InlineData("wrong name")]
    [InlineData("no asset")]
    [InlineData("no digest")]
    [InlineData("md5 digest")]
    [InlineData("short digest")]
    [InlineData("other repo url")]
    [InlineData("http url")]
    public void A_doubtful_asset_is_no_update(string variant)
    {
        var json = variant switch
        {
            "wrong name" => Release(name: "ShadowPlayer-Setup.exe"),
            "no asset" => Release(asset: false),
            "no digest" => Release().Replace("\"digest\":\"sha256:" + Sha("abc") + "\",", ""),
            "md5 digest" => Release(digest: "md5:0123456789abcdef0123456789abcdef"),
            "short digest" => Release(digest: "sha256:abc"),
            "other repo url" => Release(url: "https://github.com/someone/ShadowPlayer/releases/download/v1.1.0/ShadowPlayer-Setup-1.1.0.exe"),
            _ => Release(url: "http://github.com/w00li/ShadowPlayer/releases/download/v1.1.0/ShadowPlayer-Setup-1.1.0.exe"),
        };
        Assert.Null(UpdateChecker.Parse(json, Current));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"tag_name\":5}")]
    [InlineData("{\"tag_name\":\"v9.0.0\",\"assets\":{}}")]
    [InlineData("{\"tag_name\":\"v9.0.0\",\"assets\":[5]}")]
    public void Odd_json_is_no_update(string json) => Assert.Null(UpdateChecker.Parse(json, Current));

    [Fact]
    public async Task CheckAsync_sends_the_user_agent_and_reads_the_latest_release()
    {
        var h = new Handler(_ => Task.FromResult(Text(Release())));
        var u = await new UpdateChecker(new HttpClient(h), Current).CheckAsync(CancellationToken.None);
        Assert.NotNull(u);
        var r = Assert.Single(h.Requests);
        Assert.Equal("https://api.github.com/repos/w00li/ShadowPlayer/releases/latest", r.RequestUri!.ToString());
        Assert.Contains("ShadowPlayer/1.0.0", r.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task CheckAsync_throws_HttpRequestException_on_500()
    {
        var h = new Handler(_ => Task.FromResult(Text("boom", HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateChecker(new HttpClient(h), Current).CheckAsync(CancellationToken.None));
    }

    // ---- downloading ----

    private static UpdateInfo Info(string content = "abc") =>
        new(new Version(1, 1, 0), "ShadowPlayer-Setup-1.1.0.exe", new Uri(Repo + "v1.1.0/ShadowPlayer-Setup-1.1.0.exe"), Sha(content),
            new Uri("https://github.com/w00li/ShadowPlayer/releases/tag/v1.1.0"));

    private UpdateChecker Serving(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => new(new HttpClient(new Handler(respond)), Current);

    private string[] Files() => [.. Directory.GetFiles(_dir).Select(Path.GetFileName).Order()!];

    private sealed class Progress : IProgress<double>
    {
        public readonly List<double> Values = [];
        public void Report(double value) => Values.Add(value);
    }

    [Fact]
    public async Task Matching_hash_leaves_the_installer_in_place()
    {
        var progress = new Progress();
        var path = await Serving(_ => Task.FromResult(Text("abc"))).DownloadAsync(Info(), _dir, progress, CancellationToken.None);
        Assert.Equal(Path.Combine(_dir, "ShadowPlayer-Setup-1.1.0.exe"), path);
        Assert.Equal(["ShadowPlayer-Setup-1.1.0.exe"], Files());
        Assert.Equal(1.0, progress.Values[^1]);
    }

    [Fact]
    public async Task Mismatching_hash_removes_the_file()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Serving(_ => Task.FromResult(Text("not abc"))).DownloadAsync(Info(), _dir, null, CancellationToken.None));
        Assert.Empty(Files());
    }

    [Fact]
    public async Task Cancelling_removes_the_part_file()
    {
        using var cts = new CancellationTokenSource();
        var stalled = new StalledContent(cts);
        var checker = Serving(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = stalled }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.DownloadAsync(Info(), _dir, null, cts.Token));
        Assert.Empty(Files());
    }

    /// <summary>Sends a few bytes, then cancels the download and waits.</summary>
    private sealed class StalledContent(CancellationTokenSource cts) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync("ab"u8.ToArray());
            await stream.FlushAsync();
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, cts.Token);
        }

        protected override bool TryComputeLength(out long length) { length = 3; return true; }
    }

    [Fact]
    public async Task Network_failure_removes_the_part_file()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Serving(_ => Task.FromResult(Text("gone", HttpStatusCode.NotFound))).DownloadAsync(Info(), _dir, null, CancellationToken.None));
        Assert.Empty(Files());
    }

    [Fact]
    public async Task Download_follows_a_redirect_and_verifies()
    {
        var checker = Serving(r => Task.FromResult(r.RequestUri!.Host == "github.com"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://objects.githubusercontent.com/x") } }
            : Text("abc")));
        // The fake handler does not follow redirects; the checker must.
        var path = await checker.DownloadAsync(Info(), _dir, null, CancellationToken.None);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Existing_file_is_verified_again()
    {
        File.WriteAllText(Path.Combine(_dir, "ShadowPlayer-Setup-1.1.0.exe"), "tampered");
        var calls = 0;
        var path = await Serving(_ => { calls++; return Task.FromResult(Text("abc")); }).DownloadAsync(Info(), _dir, null, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal("abc", File.ReadAllText(path));
    }

    [Fact]
    public async Task A_verified_file_is_not_downloaded_again()
    {
        File.WriteAllText(Path.Combine(_dir, "ShadowPlayer-Setup-1.1.0.exe"), "abc");
        var calls = 0;
        await Serving(_ => { calls++; return Task.FromResult(Text("abc")); }).DownloadAsync(Info(), _dir, null, CancellationToken.None);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Clean_keeps_only_an_installer_newer_than_the_running_version()
    {
        foreach (var f in new[] { "ShadowPlayer-Setup-1.0.0.exe", "ShadowPlayer-Setup-1.1.0.exe", "ShadowPlayer-Setup-1.1.0.exe.part", "notes.txt" })
            File.WriteAllText(Path.Combine(_dir, f), "x");
        UpdateChecker.Clean(_dir, Current);
        Assert.Equal(["ShadowPlayer-Setup-1.1.0.exe"], Files());
    }
}
