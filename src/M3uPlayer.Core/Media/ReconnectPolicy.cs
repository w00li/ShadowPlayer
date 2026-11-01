namespace M3uPlayer.Core.Media;

public class ReconnectPolicy
{
    private static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8)];
    private int _attempt;

    /// <summary>The wait before the next attempt, or null when attempts are exhausted.</summary>
    public TimeSpan? NextDelay() => _attempt < Delays.Length ? Delays[_attempt++] : null;

    /// <summary>Called once playback has really progressed for long enough; only then does the sequence restart.</summary>
    public void MarkStable() => _attempt = 0;
}
