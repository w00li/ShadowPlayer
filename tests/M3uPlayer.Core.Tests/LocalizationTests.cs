using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using M3uPlayer.Core.Localization;
using M3uPlayer.Core.Playlist;
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
                var none = Record.Exception(() => string.Format(t, new object?[10])); // a stray { or } fails only at run time
                Assert.True(none is null, $"{code}: {key} is not a valid format string");
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
    public void Resolve_falls_back_to_english() => Assert.Equal("en", Languages.Resolve(null, new CultureInfo("ja-JP")));

    [Fact]
    public void Resolve_ignores_unknown_saved_code() => Assert.Equal("pl", Languages.Resolve("xx", new CultureInfo("pl-PL")));

    [Fact]
    public void The_default_category_is_stored_in_polish_and_shown_translated()
    {
        using var _ = TestCulture.Use("en");
        Assert.Equal("Other", Strings.Category_Other);
        Assert.Equal("Inne", M3uParser.DefaultCategory); // saved sessions keep "c:Inne", whatever the language
    }

    [Fact]
    public void English_counts_read_right_for_one()
    {
        // English has a separate singular, so a count next to a plural noun ("1 channels") is wrong; keys word counts plural-free instead.
        var bad = Read(new CultureInfo("en")).Where(e => e.Value.Contains("{0}"))
            .Select(e => (e.Key, Text: string.Format(e.Value, 1, 1, 1)))
            .Where(e => Regex.IsMatch(e.Text, @"\b1 (\w+ )?\w+s\b")).Select(e => e.Key + ": " + e.Text).ToList();
        Assert.Empty(bad);
    }
}
