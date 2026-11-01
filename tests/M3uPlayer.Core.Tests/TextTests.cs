using Xunit;
using M3uPlayer.Core.Diagnostics;
using M3uPlayer.Core.Text;

namespace M3uPlayer.Core.Tests;

public class TextTests
{
    [Theory]
    [InlineData("TVP 3 Białystok", "tvp3bialystok", "")]
    [InlineData("Polsat Café HD", "polsatcafe", "hd")]
    [InlineData("Canal+ Sport FHD", "canalsport", "fhd")]
    [InlineData("Sky UHD Sports 1", "skysports1", "uhd")]
    [InlineData("ViDocTV 4К", "vidoctv", "4k")] // Cyrillic K in provider EPG
    [InlineData("Eurosport 4k", "eurosport", "4k")]
    public void ParseChannelName_splits_base_and_quality(string input, string b, string q)
        => Assert.Equal(new ChannelNameParts(b, q), TextNormalizer.ParseChannelName(input));

    [Fact]
    public void ForSearch_folds_case_and_polish_letters()
        => Assert.Equal("milosc w sloncu", TextNormalizer.ForSearch("MIŁOŚĆ w Słońcu"));

    [Theory]
    [InlineData("http://h/a/mono.m3u8?token=abc123&x=1", "http://h/a/mono.m3u8?token=***&x=***")]
    [InlineData("http://user:pass@h/a.m3u8", "http://***@h/a.m3u8")]
    [InlineData("GET http://h/live/joe/secret/5.ts failed", "GET http://h/live/***/***/5.ts failed")]
    [InlineData("http://h/timeshift/joe/secret/60/5.ts", "http://h/timeshift/***/***/60/5.ts")]
    public void Redact_hides_credentials(string input, string expected)
        => Assert.Equal(expected, Redactor.Redact(input));

    [Fact]
    public void FileLog_never_writes_token()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        try
        {
            new FileLog(path).Write("failed http://h/a.m3u8?token=abc123");
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("abc123", text);
            Assert.Contains("token=***", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileLog_truncates_file_over_5MB()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        try
        {
            File.WriteAllBytes(path, new byte[5 * 1024 * 1024 + 1]);
            new FileLog(path).Write("fresh");
            Assert.Contains("fresh", File.ReadAllText(path));
            Assert.True(new FileInfo(path).Length < 1024);
        }
        finally { File.Delete(path); }
    }
}
