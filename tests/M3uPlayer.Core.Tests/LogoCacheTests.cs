using System.Net;
using M3uPlayer.Core.Media;
using Xunit;

namespace M3uPlayer.Core.Tests;

public sealed class LogoCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"logos-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage Png(int size = 10, string type = "image/png") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[size]) { Headers = { ContentType = new(type) } } };

    private (LogoCache Cache, Handler Handler) Make(Func<HttpResponseMessage> respond)
    {
        var h = new Handler(respond);
        return (new LogoCache(new HttpClient(h), _dir), h);
    }

    [Fact]
    public async Task GetAsync_downloads_once_then_uses_disk()
    {
        var (cache, h) = Make(() => Png());
        var first = await cache.GetAsync("http://example.invalid/a.png", CancellationToken.None);
        var second = await cache.GetAsync("http://example.invalid/a.png", CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(first));
        Assert.Equal(1, h.Calls);
        Assert.Matches("^[0-9a-f]{64}$", Path.GetFileName(first!)); // SHA-256 of the address, so no URL or token on disk
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetAsync_null_or_empty_returns_null(string? url)
    {
        var (cache, h) = Make(() => Png());
        Assert.Null(await cache.GetAsync(url, CancellationToken.None));
        Assert.Equal(0, h.Calls);
    }

    [Theory]
    [InlineData("file:///c:/x.png")]
    [InlineData("ftp://example.invalid/x.png")]
    [InlineData("not a url")]
    public async Task GetAsync_rejects_non_http_scheme(string url)
    {
        var (cache, h) = Make(() => Png());
        Assert.Null(await cache.GetAsync(url, CancellationToken.None));
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task GetAsync_http_error_returns_null()
    {
        var (cache, _) = Make(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await cache.GetAsync("https://example.invalid/a.png", CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_rejects_over_2_mb()
    {
        var (cache, _) = Make(() => Png(2 * 1024 * 1024 + 1));
        Assert.Null(await cache.GetAsync("https://example.invalid/a.png", CancellationToken.None));
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : []);
    }

    [Fact]
    public async Task GetAsync_rejects_over_2_mb_without_content_length()
    {
        var (cache, _) = Make(() => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[2 * 1024 * 1024 + 1])) { Headers = { ContentType = new("image/png") } },
        });
        Assert.Null(await cache.GetAsync("https://example.invalid/a.png", CancellationToken.None));
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : []);
    }

    [Fact]
    public async Task GetAsync_rejects_non_image_content_type()
    {
        var (cache, _) = Make(() => Png(10, "text/html"));
        Assert.Null(await cache.GetAsync("https://example.invalid/a.png", CancellationToken.None));
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : []);
    }
}
