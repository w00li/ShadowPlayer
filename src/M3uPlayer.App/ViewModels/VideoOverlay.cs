using M3uPlayer.Core.Epg;
using M3uPlayer.Core.Media;

namespace M3uPlayer.App.ViewModels;

/// <summary>What the video area says about playback; the mapping itself is OverlayInfo in Core.</summary>
public sealed class VideoOverlay : Observable
{
    private OverlayInfo _info = OverlayInfo.For(PlayerState.Idle, false, null, null);

    public bool Visible => _info.Visible;
    public string Text => _info.Text;
    public bool Spinner => _info.Spinner;
    public bool Button => _info.Button;
    public string ButtonText => _info.ButtonText;
    public double Dim => _info.Dim;

    public void Update(PlayerState state, bool spinnerArmed, string? lastError, string? channelName)
    {
        _info = OverlayInfo.For(state, spinnerArmed, lastError, channelName);
        Raise();
    }
}
