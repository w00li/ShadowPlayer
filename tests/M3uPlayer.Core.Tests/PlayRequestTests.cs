using Xunit;
using M3uPlayer.Core.Media;

namespace M3uPlayer.Core.Tests;

public class PlayRequestTests
{
    [Fact]
    public void ToString_does_not_print_the_url()
    {
        var r = new PlayRequest("http://example.test/live/user/secret/1.m3u8?token=abc", "UA", null, true);
        Assert.DoesNotContain("example.test", r.ToString());
        Assert.DoesNotContain("secret", r.ToString());
        Assert.DoesNotContain("abc", r.ToString());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Agent\r\nX-Evil: 1", null)]
    [InlineData("Agent\0", null)]
    [InlineData("Mozilla/5.0 Test", "Mozilla/5.0 Test")]
    public void HeaderValue_rejects_empty_and_control_characters(string? input, string? expected) =>
        Assert.Equal(expected, PlayRequest.HeaderValue(input));
}
