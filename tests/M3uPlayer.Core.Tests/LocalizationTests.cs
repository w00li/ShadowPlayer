using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using M3uPlayer.Core.Localization;
using Xunit;

namespace M3uPlayer.Core.Tests;

public class LocalizationTests
{
    private static Dictionary<string, string> Read(CultureInfo c) =>
        Strings.ResourceManager.GetResourceSet(c, true, false)?.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!)
        ?? throw new Xunit.Sdk.XunitException($"No resources for {c.Name}");

    private static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order());

    [Fact]
    public void Every_language_has_every_key_with_the_same_placeholders()
    {
        var pl = Read(CultureInfo.InvariantCulture);
        foreach (var (code, _) in Languages.All.Where(l => l.Code != "pl"))
        {
            var other = Read(new CultureInfo(code));
            foreach (var (key, text) in pl)
            {
                Assert.True(other.TryGetValue(key, out var t) && t.Length > 0, $"{code}: missing {key}");
                Assert.True(Holes(text) == Holes(t), $"{code}: {key} has other placeholders");
            }
        }
    }

    [Theory]
    [InlineData("en", "pl-PL", "en")]
    [InlineData("pl", "en-US", "pl")]
    [InlineData(null, "pl-PL", "pl")]
    [InlineData(null, "en-GB", "en")]
    public void Resolve_picks_saved_then_system(string? saved, string system, string expected) =>
        Assert.Equal(expected, Languages.Resolve(saved, new CultureInfo(system)));

    [Fact]
    public void Resolve_falls_back_to_english() => Assert.Equal("en", Languages.Resolve(null, new CultureInfo("de-DE")));

    [Fact]
    public void Resolve_ignores_unknown_saved_code() => Assert.Equal("pl", Languages.Resolve("xx", new CultureInfo("pl-PL")));
}
