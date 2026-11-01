using Xunit;
using M3uPlayer.Core.Media;

namespace M3uPlayer.Core.Tests;

public class ReconnectPolicyTests
{
    [Fact]
    public void NextDelay_gives_1_3_8_then_null()
    {
        var p = new ReconnectPolicy();
        Assert.Equal(TimeSpan.FromSeconds(1), p.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(3), p.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), p.NextDelay());
        Assert.Null(p.NextDelay());
        Assert.Null(p.NextDelay());
    }

    [Fact]
    public void Reset_restarts_sequence()
    {
        var p = new ReconnectPolicy();
        p.NextDelay();
        p.NextDelay();
        p.Reset();
        Assert.Equal(TimeSpan.FromSeconds(1), p.NextDelay());
    }

    [Fact]
    public void NextDelay_keeps_advancing_without_MarkStable()
    {
        // a server that connects and dies at once must not restart the sequence
        var p = new ReconnectPolicy();
        p.NextDelay();
        p.NextDelay();
        Assert.Equal(TimeSpan.FromSeconds(8), p.NextDelay());
    }

    [Fact]
    public void MarkStable_restarts_sequence()
    {
        var p = new ReconnectPolicy();
        p.NextDelay();
        p.NextDelay();
        p.MarkStable();
        Assert.Equal(TimeSpan.FromSeconds(1), p.NextDelay());
    }
}
