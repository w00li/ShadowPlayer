using Xunit;
using M3uPlayer.Core.Sources;

namespace M3uPlayer.Core.Tests;

public class SourceInputTests
{
    [Fact]
    public void NextDefaultName_takes_next_free_number()
    {
        Assert.Equal("Lista 1", SourceInput.NextDefaultName("Lista", []));
        Assert.Equal("Lista 3", SourceInput.NextDefaultName("Lista", ["Lista 1", "Lista 2", "EPG 3"]));
        Assert.Equal("Lista 2", SourceInput.NextDefaultName("Lista", ["Lista 1", "Moja"]));
    }

    [Theory]
    [InlineData("http://example.test/a.m3u")]
    [InlineData("https://example.test:8080/a?x=1")]
    public void ValidateUrl_accepts_http_and_https(string url) => Assert.Null(SourceInput.ValidateUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("ftp://example.test/a.m3u")]
    [InlineData("file:///C:/a.m3u")]
    [InlineData("example.test/a.m3u")]
    [InlineData("http://")]
    public void ValidateUrl_rejects_others_with_polish_message(string? url)
        => Assert.Contains("http", SourceInput.ValidateUrl(url));

    [Theory]
    [InlineData("PL-m3u=0123456789abcdef0123456789abcdef(1)", true)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("Moja lista", false)]
    [InlineData("kanaly-2026", false)]
    public void IsHashLike_detects_provider_ids(string name, bool expected) => Assert.Equal(expected, SourceInput.IsHashLike(name));

    [Fact]
    public void DefaultNameForFile_uses_file_name_unless_hash_like()
    {
        Assert.Equal("Moja lista", SourceInput.DefaultNameForFile(@"C:\x\Moja lista.m3u", "Lista", []));
        Assert.Equal("Lista 2", SourceInput.DefaultNameForFile(@"C:\x\0123456789abcdef0123456789abcdef.m3u", "Lista", ["Lista 1"]));
    }

    [Fact]
    public void DisplayLocation_hides_url_and_folder()
    {
        Assert.Equal("Adres internetowy", SourceInput.DisplayLocation("http://h/live/joe/secret/a.m3u?token=abc"));
        Assert.Equal("Plik: Moja lista.m3u", SourceInput.DisplayLocation(@"C:\Users\test\Moja lista.m3u"));
        Assert.Equal("Plik lokalny", SourceInput.DisplayLocation(@"C:\Users\test\0123456789abcdef0123456789abcdef.m3u"));
    }

    [Theory]
    [InlineData("0123456789abcde", false)]  // 15 hex characters
    [InlineData("0123456789abcdef", true)]   // 16
    public void IsHashLike_boundary(string name, bool expected) => Assert.Equal(expected, SourceInput.IsHashLike(name));

    [Theory]
    [InlineData("  Moja lista ", "Moja lista")]
    [InlineData("http://h.invalid/get.php?username=joe&password=secret", "Lista 1")]
    [InlineData("Lista ftp://u:p@h.invalid/x", "Lista 1")]
    [InlineData("Dom ?token=abc123", "Dom ?token=***")]
    public void SanitizeName_redacts_and_never_keeps_an_address(string typed, string expected)
        => Assert.Equal(expected, SourceInput.SanitizeName(typed, "Lista 1"));

    [Fact]
    public void ValidateUrl_empty_message_mentions_choosing_a_file()
        => Assert.Contains("plik", SourceInput.ValidateUrl(""));
}
