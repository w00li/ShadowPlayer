using M3uPlayer.Core.Media;

namespace M3uPlayer.App.Player;

public enum PlayerState { Idle, Opening, Playing, Paused, Reconnecting, Failed }

public record TrackInfo(int Id, string Name);

/// <remarks>
/// StateChanged and PositionChanged fire on a non-UI thread; the view model must dispatch to the UI thread.
/// </remarks>
public interface IPlayer
{
    void Play(PlayRequest r);
    void Pause();
    void Resume();
    void Stop();

    /// <summary>0-100; values outside are clamped.</summary>
    int Volume { get; set; }
    bool Muted { get; set; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    bool IsSeekable { get; }
    void Seek(TimeSpan position);

    IReadOnlyList<TrackInfo> AudioTracks { get; }
    IReadOnlyList<TrackInfo> SubtitleTracks { get; }
    void SelectAudio(int id);
    void SelectSubtitle(int id);

    /// <summary>Polish, user-facing text for the last failure; never contains a URL. Null when there is none.</summary>
    string? LastError { get; }

    event Action<PlayerState>? StateChanged;
    event Action<TimeSpan>? PositionChanged;
}
