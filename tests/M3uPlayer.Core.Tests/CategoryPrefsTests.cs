using M3uPlayer.Core.Playlist;
using M3uPlayer.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace M3uPlayer.Core.Tests;

public sealed class CategoryPrefsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"m3u-{Guid.NewGuid():N}.db");
    private readonly Database _db;
    private readonly ChannelStore _channels;
    private readonly long _source;

    public CategoryPrefsTests()
    {
        _db = new Database(_path);
        _channels = new ChannelStore(_db);
        _source = new SourceStore(_db).Add(SourceKind.Playlist, "p", "P");
        Import("A", "B", "C", "D");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_path) + "*")) File.Delete(f);
    }

    private void Import(params string[] categories) =>
        _channels.Replace(_source, [.. categories.Select(c => new M3uEntry(
            c.ToLowerInvariant() + "1", c + " one", "http://example.invalid/" + Uri.EscapeDataString(c), null, null, null, 0, c, null, null, 0, null, null))]);

    private static object? Sql(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private string[] Names() => [.. _channels.Categories().Select(c => c.Name)];
    private CategoryCount Cat(string name) => _channels.Categories().Single(c => c.Name == name);

    [Fact]
    public void Pinned_categories_come_first_in_list_order()
    {
        _channels.SetCategoryPref("C", true, false);
        _channels.SetCategoryPref("B", true, false);
        Assert.Equal(["B", "C", "A", "D"], Names());
    }

    [Fact]
    public void Hidden_category_leaves_all_but_stays_in_search_and_favourites()
    {
        _channels.SetFavorite("c1", true);
        _channels.SetCategoryPref("C", false, true);
        Assert.True(Cat("C").Hidden);
        Assert.DoesNotContain(_channels.All(), ch => ch.Category == "C");
        Assert.Equal(3, _channels.CountAll());
        Assert.Contains(_channels.Search("C one"), ch => ch.Key == "c1");
        Assert.Contains(_channels.Favorites(), ch => ch.Key == "c1");
        Assert.Contains(_channels.ByCategory("C"), ch => ch.Key == "c1");
    }

    [Fact]
    public void Pinning_unhides_and_hiding_unpins()
    {
        _channels.SetCategoryPref("A", false, true);
        _channels.SetCategoryPref("A", true, false);
        Assert.Equal((true, false), (Cat("A").Pinned, Cat("A").Hidden));
        _channels.SetCategoryPref("A", false, true);
        Assert.Equal((false, true), (Cat("A").Pinned, Cat("A").Hidden));
    }

    [Fact]
    public void Clearing_both_flags_removes_the_row()
    {
        _channels.SetCategoryPref("A", true, false);
        _channels.SetCategoryPref("A", false, false);
        using var c = _db.Open();
        Assert.Equal(0L, Sql(c, "SELECT COUNT(*) FROM category_pref"));
    }

    [Fact]
    public void Both_flags_throw() => Assert.Throws<ArgumentException>(() => _channels.SetCategoryPref("A", true, true));

    [Fact]
    public void ShowAllCategories_keeps_pins()
    {
        _channels.SetCategoryPref("A", true, false);
        _channels.SetCategoryPref("B", false, true);
        _channels.ShowAllCategories();
        Assert.True(Cat("A").Pinned);
        Assert.False(Cat("B").Hidden);
    }

    [Fact]
    public void Prefs_survive_a_reimport_and_a_vanished_category_is_not_listed()
    {
        _channels.SetCategoryPref("B", false, true);
        Import("A", "C", "D");
        Assert.DoesNotContain("B", Names());
        Import("A", "B", "C", "D");
        Assert.True(Cat("B").Hidden);
    }

    [Fact]
    public void All_is_empty_when_every_category_is_hidden()
    {
        foreach (var n in new[] { "A", "B", "C", "D" }) _channels.SetCategoryPref(n, false, true);
        Assert.Empty(_channels.All());
        Assert.Equal(0, _channels.CountAll());
        Assert.All(_channels.Categories(), c => Assert.True(c.Hidden));
    }

    [Fact]
    public void Category_names_with_quotes_round_trip()
    {
        Import("O'Brien \"TV\"");
        _channels.SetCategoryPref("O'Brien \"TV\"", false, true);
        Assert.True(Cat("O'Brien \"TV\"").Hidden);
    }

    [Fact]
    public void A_version_1_database_upgrades_and_keeps_its_data()
    {
        using (var c = _db.Open())
        {
            Sql(c, "DROP TABLE category_pref");
            Sql(c, "PRAGMA user_version=1");
        }
        SqliteConnection.ClearAllPools();
        var db = new Database(_path);
        using (var c = db.Open()) Assert.Equal(2L, Convert.ToInt64(Sql(c, "PRAGMA user_version")));
        var channels = new ChannelStore(db);
        Assert.Equal(4, channels.CountAll());
        channels.SetCategoryPref("A", false, true);
        Assert.Equal(3, channels.CountAll());
    }

    [Fact]
    public void Search_can_reach_channels_of_hidden_categories()
    {
        _channels.SetCategoryPref("C", false, true);
        Assert.Contains(_channels.All(includeHidden: true), ch => ch.Key == "c1"); // the app filters this list by search matches
        Assert.Equal(4, _channels.CountAll(includeHidden: true));
    }
}
