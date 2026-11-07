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
}
