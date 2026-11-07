using M3uPlayer.Core.Storage;
using Xunit;

namespace M3uPlayer.Core.Tests;

public sealed class DataResetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"reset-{Guid.NewGuid():N}");

    public DataResetTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "logos"));
        Directory.CreateDirectory(Path.Combine(_dir, "tmp"));
        foreach (var f in new[] { "m3u.db", "m3u.db-wal", "m3u.db-shm", "app.log", @"logos\a.png", @"tmp\x.tmp" })
            File.WriteAllText(Path.Combine(_dir, f), "x");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string[] Left() => [.. Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(_dir, p)).Order()];

    [Fact]
    public void RunIfRequested_does_nothing_without_the_marker()
    {
        DataReset.RunIfRequested(_dir);
        Assert.Equal(8, Left().Length);
    }

    [Fact]
    public void RunIfRequested_removes_data_and_keeps_the_log()
    {
        DataReset.Request(_dir);
        DataReset.RunIfRequested(_dir);
        Assert.Equal(["app.log"], Left());
    }

    [Fact]
    public void RunIfRequested_keeps_the_marker_when_a_file_stays_locked()
    {
        DataReset.Request(_dir);
        using (new FileStream(Path.Combine(_dir, "m3u.db"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => DataReset.RunIfRequested(_dir, TimeSpan.FromMilliseconds(300)));
        Assert.True(File.Exists(Path.Combine(_dir, DataReset.MarkerName)));
    }
}
