using M3uPlayer.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace M3uPlayer.Core.Tests;

public sealed class PreferencesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"m3u-{Guid.NewGuid():N}.db");
    private readonly Database _db;

    public PreferencesTests() => _db = new Database(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_path) + "*")) File.Delete(f);
    }

    [Fact]
    public void Defaults_match_todays_behaviour()
    {
        var p = new Preferences(_db);
        Assert.Equal("system", p.Theme);
        Assert.False(p.Autostart);
        Assert.False(p.StartFullscreen);
        Assert.True(p.HwDecoding);
        Assert.Equal(1500, p.BufferMs);
        Assert.Equal(10, p.SeekSeconds);
        Assert.Equal(3, p.HideControlsSeconds);
        Assert.Equal(720, p.DefaultIntervalMinutes);
    }

    [Fact]
    public void Values_round_trip()
    {
        var p = new Preferences(_db)
        {
            Theme = "dark", Autostart = true, StartFullscreen = true, HwDecoding = false,
            BufferMs = 5000, SeekSeconds = 30, HideControlsSeconds = 10, DefaultIntervalMinutes = 1440,
        };
        var again = new Preferences(_db);
        Assert.Equal(("dark", true, true, false), (again.Theme, again.Autostart, again.StartFullscreen, again.HwDecoding));
        Assert.Equal((5000, 30, 10, 1440), (again.BufferMs, again.SeekSeconds, again.HideControlsSeconds, again.DefaultIntervalMinutes));
    }

    [Theory]
    [InlineData("ui.theme", "blue")]
    [InlineData("playback.bufferMs", "abc")]
    [InlineData("playback.bufferMs", "1234")]
    [InlineData("playback.seekSeconds", "-5")]
    [InlineData("playback.autostart", "yes")]
    [InlineData("sources.defaultInterval", "7")]
    public void Invalid_values_fall_back_to_defaults(string key, string value)
    {
        _db.SetSetting(key, value);
        var p = new Preferences(_db);
        Assert.Equal(("system", false, 1500, 10, 720), (p.Theme, p.Autostart, p.BufferMs, p.SeekSeconds, p.DefaultIntervalMinutes));
    }

    [Fact]
    public void Setting_a_value_outside_the_choices_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Preferences(_db).BufferMs = 1234);
}
